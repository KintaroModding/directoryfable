using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace DirectoryFable.Chat;

/// <summary>
/// Claude's Messages API through the official Anthropic SDK, streamed. Thinking is adaptive (the model decides how
/// much), effort goes into output_config. The answer's content blocks (thinking with its signature, text, tool
/// calls) are kept with the message and sent back unchanged: Claude checks that thinking blocks come back exactly,
/// in the same conversation. Should the chat still change before them (other instructions, a folder opened later,
/// so other tools), "drop_block" lets the API leave such blocks out instead of refusing the request.
/// A classifier refusal is answered again by Anthropic's recommended model ("fallbacks": "default").
/// </summary>
public static class ClaudeClient
{
    private const string DefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>Room for the thinking and the answer (thinking counts toward it); streaming has no timeout trouble.</summary>
    private const int MaxTokens = 64000;

    private static readonly Lock ClientsLock = new();
    private static readonly Dictionary<(string Key, string Url), AnthropicClient> Clients = [];

    /// <summary>The workspace tools in Claude's format; input streams as it is written (long file contents).</summary>
    private static readonly Lazy<List<BetaToolUnion>> WorkspaceToolList = new(() =>
    [
        .. WorkspaceTools.Definitions.EnumerateArray().Select(d =>
        {
            var function = d.GetProperty("function");
            var schema = function.GetProperty("parameters");
            return (BetaToolUnion)new BetaTool
            {
                Name = function.GetProperty("name").GetString()!,
                Description = function.GetProperty("description").GetString(),
                InputSchema = new()
                {
                    Properties = schema.GetProperty("properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
                    Required = [.. schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!)],
                },
                EagerInputStreaming = true,
            };
        }),
    ]);

    public static async IAsyncEnumerable<ChatPiece> StreamAsync(ChatProvider provider, string systemPrompt,
        IEnumerable<ChatMessage> history, JsonElement? tools, [EnumeratorCancellation] CancellationToken token)
    {
        var client = ClientFor(provider);
        string model = provider.Model.Trim();
        bool adaptive = SupportsAdaptiveThinking(model);
        var betas = new List<ApiEnum<string, AnthropicBeta>>();
        if (adaptive)
            betas.Add(AnthropicBeta.ThinkingBindingControls2026_08_01);
        if (SupportsFallbacks(model))
            betas.Add(AnthropicBeta.ServerSideFallback2026_07_01);

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxTokens,
            Messages = BuildMessages(history),
            // The chat so far is cached: the next request (also every tool round) reads it at a fraction of the price.
            CacheControl = new BetaCacheControlEphemeral(),
        };
        if (systemPrompt.Trim().Length > 0)
            parameters = parameters with { System = systemPrompt };
        if (tools is not null)
            parameters = parameters with { Tools = WorkspaceToolList.Value };
        if (adaptive)
        {
            parameters = parameters with
            {
                Thinking = new BetaThinkingConfigAdaptive
                {
                    BlockBinding = new BetaThinkingBlockBinding { PrefixMismatchBehavior = BetaThinkingPrefixMismatchBehavior.DropBlock },
                },
            };
        }
        if (provider.Effort.Trim().Length > 0)
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = provider.Effort.Trim() } };
        if (SupportsFallbacks(model))
            parameters = parameters with { Fallbacks = new Default() };
        if (betas.Count > 0)
            parameters = parameters with { Betas = betas };

        var blocks = new SortedDictionary<long, Block>();
        long input = 0, output = 0;
        string? answeredBy = null;
        string? stopReason = null;
        string? refusalCategory = null, refusalExplanation = null;

        await using var events = client.Beta.Messages.CreateStreaming(parameters, token).GetAsyncEnumerator(token);
        while (await NextAsync(events))
        {
            var e = events.Current;
            if (e.TryPickStart(out var start))
            {
                input = Prompt(start.Message.Usage);
                output = start.Message.Usage.OutputTokens;
                answeredBy = start.Message.Model.Raw(); // A conversation kept on a fallback model names it here.
            }
            else if (e.TryPickContentBlockStart(out var blockStart))
            {
                var block = new Block();
                var content = blockStart.ContentBlock;
                if (content.TryPickBetaText(out var text))
                {
                    block.Type = "text";
                    block.Text.Append(text.Text);
                }
                else if (content.TryPickBetaThinking(out var thinking))
                {
                    block.Type = "thinking";
                    block.Text.Append(thinking.Thinking);
                    block.Signature = thinking.Signature;
                    yield return new ChatPiece(null, true);
                }
                else if (content.TryPickBetaRedactedThinking(out var redacted))
                {
                    block.Type = "redacted_thinking";
                    block.Data = redacted.Data;
                    yield return new ChatPiece(null, true);
                }
                else if (content.TryPickBetaToolUse(out var toolUse))
                {
                    block.Type = "tool_use";
                    block.Id = toolUse.ID;
                    block.Name = toolUse.Name;
                }
                else if (content.TryPickBetaFallback(out var fallback))
                {
                    block.Type = "fallback";
                    block.FromModel = fallback.From.Model;
                    block.ToModel = fallback.To.Model;
                    answeredBy = block.ToModel;
                }
                else
                {
                    block.Type = "other"; // Server tools and such: not used by GemmaChat, not sent back.
                }
                blocks[blockStart.Index] = block;
                if (block.Type == "text" && block.Text.Length > 0)
                    yield return new ChatPiece(block.Text.ToString(), false);
            }
            else if (e.TryPickContentBlockDelta(out var blockDelta))
            {
                if (!blocks.TryGetValue(blockDelta.Index, out var block))
                    continue;
                var delta = blockDelta.Delta;
                if (delta.TryPickText(out var text))
                {
                    block.Text.Append(text.Text);
                    if (text.Text.Length > 0)
                        yield return new ChatPiece(text.Text, false);
                }
                else if (delta.TryPickThinking(out var thinking))
                {
                    block.Text.Append(thinking.Thinking);
                }
                else if (delta.TryPickSignature(out var signature))
                {
                    block.Signature = signature.Signature;
                }
                else if (delta.TryPickInputJson(out var json))
                {
                    block.Input.Append(json.PartialJson);
                }
            }
            else if (e.TryPickDelta(out var messageDelta))
            {
                if (messageDelta.Delta.StopReason is { } reason)
                    stopReason = reason.Raw();
                if (messageDelta.Delta.StopDetails is { } details)
                {
                    refusalCategory = details.Category?.Raw();
                    refusalExplanation = details.Explanation;
                }
                output = messageDelta.Usage.OutputTokens;
                long prompt = (messageDelta.Usage.InputTokens ?? 0) + (messageDelta.Usage.CacheCreationInputTokens ?? 0)
                              + (messageDelta.Usage.CacheReadInputTokens ?? 0);
                if (prompt > 0)
                    input = prompt;
            }
        }

        if (answeredBy is { Length: > 0 })
            yield return new ChatPiece(null, false, Model: answeredBy);
        yield return new ChatPiece(null, false, Usage: ((int)Math.Min(input, int.MaxValue), (int)Math.Min(output, int.MaxValue)));

        if (stopReason == "refusal")
        {
            // Nothing of a declined turn is kept for the next request (its partial answer is not an answer).
            string why = (refusalCategory, refusalExplanation) switch
            {
                (_, { Length: > 0 } explanation) => explanation,
                ({ Length: > 0 } category, _) => $"category: {category}",
                _ => "no reason given",
            };
            yield return new ChatPiece(null, false, Notice: new ChatNotice($"Claude declined this request ({why}).", true));
            yield break;
        }

        var kept = blocks.Values.Where(b => b.Type != "other").Select(b => b.ToChatBlock()).ToList();
        yield return new ChatPiece(null, false, Blocks: kept);

        // Tool calls of a turn that a fallback model took over count only after the last switch (the declined
        // model's calls are not answered and not sent back, see BuildMessages).
        int lastFallback = kept.FindLastIndex(b => b.Type == "fallback");
        var calls = kept.Skip(lastFallback + 1).Where(b => b.Type == "tool_use")
            .Select(b => new ChatToolCall { Id = b.Id ?? "", Name = b.Name ?? "", Arguments = b.Input ?? "" }).ToList();
        if (calls.Count > 0)
            yield return new ChatPiece(null, false, calls);
        if (stopReason == "max_tokens")
        {
            // A cut-off tool call has broken JSON: the tools answer it with an error and Claude can try again.
            yield return new ChatPiece(null, false, Notice: new ChatNotice($"(The answer reached the length limit of {MaxTokens:N0} tokens and was cut off.)", false));
        }
    }

    /// <summary>The next stream event; the SDK's errors become ChatApiException / HttpRequestException for the chat view.</summary>
    private static async Task<bool> NextAsync(IAsyncEnumerator<BetaRawMessageStreamEvent> events)
    {
        try
        {
            return await events.MoveNextAsync();
        }
        catch (AnthropicApiException ex)
        {
            throw new ChatApiException((int)ex.StatusCode, ErrorText(ex));
        }
        catch (AnthropicSseException ex)
        {
            throw new ChatApiException(0, ex.Message);
        }
        catch (AnthropicIOException ex)
        {
            throw new HttpRequestException(ex.Message, ex);
        }
    }

    /// <summary>Everything the request sent: new input plus the cached parts (written now or read).</summary>
    private static long Prompt(BetaUsage usage) =>
        usage.InputTokens + (usage.CacheCreationInputTokens ?? 0) + (usage.CacheReadInputTokens ?? 0);

    /// <summary>
    /// The chat in Claude's format: user turns as text; Claude's own answers from their saved blocks, unchanged;
    /// answers of other providers (or stopped ones) as text and tool calls; the tool results of one turn together in
    /// one user turn. Errors are not sent.
    /// </summary>
    private static List<BetaMessageParam> BuildMessages(IEnumerable<ChatMessage> history)
    {
        var messages = new List<BetaMessageParam>();
        List<BetaContentBlockParam>? results = null;
        void FlushResults()
        {
            if (results is { Count: > 0 })
                messages.Add(new BetaUserMessageParam { Content = results });
            results = null;
        }

        foreach (var m in history.Where(m => !m.IsError))
        {
            if (m.Role == "tool")
            {
                results ??= [];
                results.Add(new BetaToolResultBlockParam
                {
                    ToolUseID = m.ToolCallId ?? "",
                    Content = m.Content,
                    IsError = m.Content.StartsWith("Error:", StringComparison.Ordinal),
                });
                continue;
            }
            FlushResults();
            if (m.Role == "user")
            {
                if (m.Images is { Count: > 0 } || m.Files is { Count: > 0 })
                {
                    // Pictures and PDF documents first, then the text (as Claude reads them best); attached text
                    // files are part of the text.
                    var blocks = new List<BetaContentBlockParam>();
                    foreach (string name in m.Images ?? [])
                    {
                        if (ChatImages.ForApi(name) is { } image)
                            blocks.Add(new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = image.Base64, MediaType = image.MediaType } });
                    }
                    foreach (var (name, base64) in ChatFiles.Pdfs(m))
                        blocks.Add(new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = base64 }, Title = name });
                    string text = ChatFiles.TextWithFiles(m);
                    if (text.Length > 0)
                        blocks.Add(new BetaTextBlockParam { Text = text });
                    if (blocks.Count > 0)
                        messages.Add(new BetaUserMessageParam { Content = blocks });
                }
                else
                {
                    messages.Add(new BetaUserMessageParam { Content = m.Content });
                }
                continue;
            }
            var content = m.Blocks is { Count: > 0 } saved ? FromBlocks(saved) : FromText(m);
            if (content.Count > 0)
                messages.Add(new BetaAssistantMessageParam { Content = content });
        }
        FlushResults();
        return messages;
    }

    /// <summary>
    /// A saved Claude answer, block by block. Where a fallback model took over, the declined model's thinking and tool
    /// calls before the last switch are left out; text and the fallback marks stay where they were.
    /// </summary>
    private static List<BetaContentBlockParam> FromBlocks(List<ChatBlock> blocks)
    {
        int lastFallback = blocks.FindLastIndex(b => b.Type == "fallback");
        var content = new List<BetaContentBlockParam>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            bool beforeSwitch = i < lastFallback;
            switch (b.Type)
            {
                case "text" when b.Text is { Length: > 0 } text:
                    content.Add(new BetaTextBlockParam { Text = text });
                    break;
                case "thinking" when !beforeSwitch:
                    content.Add(new BetaThinkingBlockParam { Thinking = b.Text ?? "", Signature = b.Signature ?? "" });
                    break;
                case "redacted_thinking" when !beforeSwitch:
                    content.Add(new BetaRedactedThinkingBlockParam { Data = b.Data ?? "" });
                    break;
                case "tool_use" when !beforeSwitch:
                    content.Add(new BetaToolUseBlockParam { ID = b.Id ?? "", Name = b.Name ?? "", Input = InputOf(b.Input) });
                    break;
                case "fallback":
                    content.Add(new BetaFallbackBlockParam
                    {
                        From = new BetaFallbackInfoParam { Model = b.FromModel ?? "" },
                        To = new BetaFallbackInfoParam { Model = b.ToModel ?? "" },
                    });
                    break;
            }
        }
        return content;
    }

    /// <summary>An answer without Claude blocks: its text and its tool calls.</summary>
    private static List<BetaContentBlockParam> FromText(ChatMessage m)
    {
        var content = new List<BetaContentBlockParam>();
        if (m.Content.Length > 0)
            content.Add(new BetaTextBlockParam { Text = m.Content });
        foreach (var call in m.ToolCalls ?? [])
            content.Add(new BetaToolUseBlockParam { ID = call.Id, Name = call.Name, Input = InputOf(call.Arguments) });
        return content;
    }

    /// <summary>A tool call's input as an object; broken JSON (a cut-off call) goes back as {} - its result says why.</summary>
    private static Dictionary<string, JsonElement> InputOf(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json is { Length: > 0 } ? json : "{}");
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        }
        catch (JsonException)
        {
            // Falls through.
        }
        return [];
    }

    /// <summary>Claude's models (GET /v1/models), the newest first; also a free test of the key.</summary>
    public static async Task<List<string>> ListModelsAsync(ChatProvider provider, CancellationToken token)
    {
        var client = ClientFor(provider);
        try
        {
            var page = await client.Models.List(new Anthropic.Models.Models.ModelListParams { Limit = 1000 }, token);
            return [.. page.Items.Select(m => m.ID)];
        }
        catch (AnthropicApiException ex)
        {
            throw new ChatApiException((int)ex.StatusCode, ErrorText(ex));
        }
        catch (AnthropicIOException ex)
        {
            throw new HttpRequestException(ex.Message, ex);
        }
    }

    /// <summary>One SDK client per key and address (it keeps its connections).</summary>
    private static AnthropicClient ClientFor(ChatProvider provider)
    {
        string key = provider.ApiKey.Trim();
        if (key.Length == 0)
            throw new ChatApiException(401, "No API key. Create one in the Claude Console (platform.claude.com) and enter it on the settings page.");
        if (key.StartsWith("sk-ant-oat", StringComparison.Ordinal))
        {
            // A token of a Claude subscription (Pro / Max, made by "claude setup-token") is for Claude Code only.
            throw new ChatApiException(401, "This is a Claude subscription token (for Claude Code), not an API key. " +
                                            "GemmaChat needs an API key from the Claude Console (platform.claude.com); API use is billed there, separately from a subscription.");
        }
        string url = provider.BaseUrl.Trim().TrimEnd('/');
        if (url.Length == 0)
            url = DefaultBaseUrl;
        lock (ClientsLock)
        {
            if (!Clients.TryGetValue((key, url), out var client))
            {
                client = new AnthropicClient
                {
                    ApiKey = key,
                    BaseUrl = url,
                    // A long answer (a big file written by a tool) streams for many minutes.
                    Timeout = TimeSpan.FromMinutes(30),
                };
                Clients[(key, url)] = client;
            }
            return client;
        }
    }

    /// <summary>The API's error message (its JSON "error.message"), else the exception's text.</summary>
    private static string ErrorText(AnthropicApiException ex)
    {
        try
        {
            using var json = JsonDocument.Parse(ex.ResponseBody);
            if (json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
                && message.GetString() is { Length: > 0 } text)
                return text;
        }
        catch (Exception parse) when (parse is JsonException or ArgumentException)
        {
            // Not JSON: the exception's own text.
        }
        return ex.Message;
    }

    /// <summary>Models that take adaptive thinking: Claude 4.6 and later (older ones would refuse the request).</summary>
    private static bool SupportsAdaptiveThinking(string model)
    {
        var match = System.Text.RegularExpressions.Regex.Match(model, @"^claude-(?:opus|sonnet|haiku|fable|mythos)-(\d+)(?:-(\d{1,2}))?(?!\d)");
        if (!match.Success)
            return false;
        int major = int.Parse(match.Groups[1].Value);
        int minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        return major >= 5 || (major == 4 && minor >= 6);
    }

    /// <summary>The models that take server-side fallbacks on a refusal.</summary>
    private static bool SupportsFallbacks(string model) =>
        model is "claude-opus-5-5" or "claude-opus-5" or "claude-sonnet-5-5" or "claude-fable-5-1" or "claude-fable-5-2";

    /// <summary>A content block while it streams in.</summary>
    private sealed class Block
    {
        public string Type = "";
        public readonly StringBuilder Text = new();
        public readonly StringBuilder Input = new();
        public string? Signature, Data, Id, Name, FromModel, ToModel;

        public ChatBlock ToChatBlock() => new()
        {
            Type = Type,
            Text = Type is "text" or "thinking" ? Text.ToString() : null,
            Signature = Signature,
            Data = Data,
            Id = Id,
            Name = Name,
            Input = Type == "tool_use" ? (Input.Length > 0 ? Input.ToString() : "{}") : null,
            FromModel = FromModel,
            ToModel = ToModel,
        };
    }
}
