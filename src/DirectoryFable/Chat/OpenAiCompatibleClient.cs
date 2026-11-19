using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DirectoryFable.Chat;

/// <summary>
/// The OpenAI-compatible chat API (POST {base}/chat/completions with "stream": true): the answer comes as server-sent
/// events ("data: {json}" lines, "data: [DONE]" at the end) with pieces of text in choices[0].delta.content.
/// Reasoning models also send choices[0].delta.reasoning_content first (only counted here, for the "thinking" sign).
/// With tools (file work), the AI may ask for tool calls instead: they come in pieces in choices[0].delta.tool_calls
/// (by index: id, function name, arguments as growing JSON text) and are handed over whole at the end.
/// OpenAI's own API (GPT models, https://api.openai.com/v1) is one of these providers.
/// </summary>
public static class OpenAiCompatibleClient
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async IAsyncEnumerable<ChatPiece> StreamAsync(ChatProvider provider, string systemPrompt,
        IEnumerable<ChatMessage> history, JsonElement? tools, [EnumeratorCancellation] CancellationToken token)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = provider.Model.Trim(),
            ["messages"] = BuildMessages(systemPrompt, history),
            ["stream"] = true,
            // The token use comes in the last piece (the size of the chat for the model).
            ["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true },
        };
        if (tools is { } t)
            body["tools"] = t;
        if (provider.Effort.Trim().Length > 0)
            body["reasoning_effort"] = provider.Effort.Trim();

        string url = provider.BaseUrl.Trim().TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (provider.ApiKey.Trim().Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            string text = await response.Content.ReadAsStringAsync(token);
            throw new ChatApiException((int)response.StatusCode, ErrorText(text));
        }

        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
        (int Prompt, int Completion)? usageSeen = null;
        string? modelSeen = null;
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            string data = line[5..].Trim();
            if (data == "[DONE]")
                break;
            ChatPiece? piece = null;
            try
            {
                using var json = JsonDocument.Parse(data);
                var root = json.RootElement;
                if (root.TryGetProperty("error", out var error))
                    throw new ChatApiException(0, error.TryGetProperty("message", out var m) ? m.GetString() ?? data : data);
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    int Number(string name) => usage.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : 0;
                    usageSeen = (Number("prompt_tokens"), Number("completion_tokens"));
                }
                // The model that answers (the provider's full name for it, e.g. with a version).
                if (modelSeen is null && root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                    && model.GetString() is { Length: > 0 } modelName)
                    modelSeen = modelName;
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out var delta))
                {
                    if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                        AddToolCallPieces(calls, toolCalls);
                    if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                        && content.GetString() is { Length: > 0 } text)
                        piece = new ChatPiece(text, false);
                    else if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                        piece = new ChatPiece(null, true);
                }
            }
            catch (JsonException)
            {
                // A line that is no JSON (keep-alive comments and such): skipped.
            }
            if (piece is { } p)
                yield return p;
        }
        if (modelSeen is not null)
            yield return new ChatPiece(null, false, Model: modelSeen);
        if (usageSeen is { } seen)
            yield return new ChatPiece(null, false, Usage: seen);
        if (calls.Count > 0)
        {
            yield return new ChatPiece(null, false, [.. calls.Values.Select((c, i) => new ChatToolCall
            {
                Id = c.Id.Length > 0 ? c.Id : $"call_{i}",
                Name = c.Name,
                Arguments = c.Arguments.ToString(),
            })]);
        }
    }

    /// <summary>Tool calls come in pieces: the first piece of an index has the id and name, all add to the arguments.</summary>
    private static void AddToolCallPieces(SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)> calls, JsonElement toolCalls)
    {
        foreach (var call in toolCalls.EnumerateArray())
        {
            int index = call.TryGetProperty("index", out var i) && i.TryGetInt32(out int n) ? n : calls.Count;
            if (!calls.TryGetValue(index, out var entry))
                entry = ("", "", new StringBuilder());
            if (call.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } idText)
                entry.Id = idText;
            if (call.TryGetProperty("function", out var function))
            {
                if (function.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } nameText)
                    entry.Name = nameText;
                if (function.TryGetProperty("arguments", out var args) && args.GetString() is { } argsText)
                    entry.Arguments.Append(argsText);
            }
            calls[index] = entry;
        }
    }

    /// <summary>The messages as the API wants them: system prompt, user / assistant text, the AI's tool calls and their results.</summary>
    private static List<object> BuildMessages(string systemPrompt, IEnumerable<ChatMessage> history)
    {
        var messages = new List<object>();
        if (systemPrompt.Trim().Length > 0)
            messages.Add(new Dictionary<string, object?> { ["role"] = "system", ["content"] = systemPrompt });
        foreach (var m in history.Where(m => !m.IsError))
        {
            if (m.Role == "tool")
            {
                messages.Add(new Dictionary<string, object?> { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Content });
            }
            else if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 } toolCalls)
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = m.Content.Length > 0 ? m.Content : null,
                    ["tool_calls"] = toolCalls.Select(c => new Dictionary<string, object?>
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?> { ["name"] = c.Name, ["arguments"] = c.Arguments },
                    }).ToList(),
                });
            }
            else
            {
                messages.Add(new Dictionary<string, object?> { ["role"] = m.Role, ["content"] = m.Content });
            }
        }
        return messages;
    }

    /// <summary>The provider's models (GET {base}/models: data[].id), sorted; also a free test of address and key.</summary>
    public static async Task<List<string>> ListModelsAsync(ChatProvider provider, CancellationToken token)
    {
        string url = provider.BaseUrl.Trim().TrimEnd('/') + "/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (provider.ApiKey.Trim().Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey.Trim());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Http.SendAsync(request, timeout.Token);
        string body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new ChatApiException((int)response.StatusCode, ErrorText(body));
        var models = new List<string>();
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in data.EnumerateArray())
                {
                    if (model.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } name)
                        models.Add(name);
                }
            }
        }
        catch (JsonException)
        {
            throw new ChatApiException((int)response.StatusCode, "The answer was no model list.");
        }
        models.Sort(StringComparer.OrdinalIgnoreCase);
        return models;
    }

    /// <summary>The "message" of an error answer, else the body itself (shortened).</summary>
    private static string ErrorText(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? body;
                if (error.TryGetProperty("message", out var message))
                    return message.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Not JSON: the text itself.
        }
        return body.Length > 400 ? body[..400] + "..." : body;
    }
}

public sealed class ChatApiException(int status, string message) : Exception(message)
{
    /// <summary>The HTTP status (0 = an error inside the stream).</summary>
    public int Status { get; } = status;
}
