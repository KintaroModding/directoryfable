using System.Text.Json;

namespace DirectoryFable.Chat;

/// <summary>
/// A piece of an answer: text, a sign that the model is still reasoning, (last) the tool calls, the token use
/// (prompt = everything sent, the chat's size for the model; completion = the answer), (Claude) the answer's
/// content blocks for the next request and a note to show (a refusal, the length limit), or the model that
/// answered as the provider reports it (also a fallback model).
/// </summary>
public readonly record struct ChatPiece(string? Text, bool Reasoning, IReadOnlyList<ChatToolCall>? ToolCalls = null,
    (int Prompt, int Completion)? Usage = null, IReadOnlyList<ChatBlock>? Blocks = null, ChatNotice? Notice = null,
    string? Model = null);

/// <summary>How a Claude answer ended, when it is not a normal end.</summary>
public sealed record ChatNotice(string Text, bool Refused);

/// <summary>Sends a chat to the chosen provider's API: OpenAI-compatible (also OpenAI's own GPT API) or Claude's.</summary>
public static class ChatClient
{
    public static IAsyncEnumerable<ChatPiece> StreamAsync(ChatProvider provider, string systemPrompt, IEnumerable<ChatMessage> history,
        JsonElement? tools, CancellationToken token) =>
        provider.IsClaude
            ? ClaudeClient.StreamAsync(provider, systemPrompt, history, tools, token)
            : OpenAiCompatibleClient.StreamAsync(provider, systemPrompt, history, tools, token);

    public static Task<List<string>> ListModelsAsync(ChatProvider provider, CancellationToken token) =>
        provider.IsClaude
            ? ClaudeClient.ListModelsAsync(provider, token)
            : OpenAiCompatibleClient.ListModelsAsync(provider, token);
}
