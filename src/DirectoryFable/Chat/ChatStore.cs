using System.IO;
using System.Text.Json;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Chat;

/// <summary>
/// One AI provider: an OpenAI-compatible chat API (OpenAI's GPT models, DeepSeek, a local Ollama or LM Studio, ...)
/// or Claude's own API (Kind "anthropic").
/// </summary>
public sealed class ChatProvider
{
    public const string ClaudeKind = "anthropic";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>"" = an OpenAI-compatible API; "anthropic" = Claude's Messages API.</summary>
    public string Kind { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsClaude => Kind == ClaudeKind;

    /// <summary>
    /// The API's base address. OpenAI-compatible: "/chat/completions" is added ("https://api.deepseek.com",
    /// "http://localhost:11434/v1"). Claude: "https://api.anthropic.com" (empty = that address).
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>The API key (empty for local servers that need none).</summary>
    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>
    /// "low", "medium", "high" (OpenAI-compatible: sent as reasoning_effort; Claude: output_config.effort, also
    /// "xhigh" and "max"), or "" = the provider's default (not sent).
    /// </summary>
    public string Effort { get; set; } = "";

    /// <summary>The models the provider listed last ("Load models"), for the bar's model list.</summary>
    public List<string> KnownModels { get; set; } = [];
}

/// <summary>GemmaChat's settings file: the providers (with their keys), the chosen one, the standing instructions.</summary>
public sealed class ChatConfig
{
    public List<ChatProvider> Providers { get; set; } = [];
    public string? SelectedProviderId { get; set; }

    /// <summary>Instructions sent before every chat ("system prompt"); empty = none.</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>File work: changes (create, edit) without asking first. Deleting always asks.</summary>
    public bool ChangesWithoutAsking { get; set; }

    public ChatProvider? Selected => Providers.FirstOrDefault(p => p.Id == SelectedProviderId) ?? Providers.FirstOrDefault();

    /// <summary>
    /// The newest ready-made providers this file has been given (0: the first three; 1: also Claude and OpenAI).
    /// A file from before gets the newer ones once, at the top of its list (see AddNewPresets).
    /// </summary>
    public int PresetsVersion { get; set; }

    public const int CurrentPresetsVersion = 1;

    /// <summary>The ready-made providers a new configuration starts with (keys and models filled in by the user).</summary>
    public static ChatConfig Default() => new() { Providers = [.. Presets.Select(p => p.Create())], PresetsVersion = CurrentPresetsVersion };

    /// <summary>
    /// Ready-made providers newer than the file: added once, at the top (unless one with the same API and address is
    /// there already). True when something changed. A provider the user removes afterwards stays removed.
    /// </summary>
    public bool AddNewPresets()
    {
        if (PresetsVersion >= CurrentPresetsVersion)
            return false;
        int at = 0;
        foreach (var provider in Presets.Where(p => p.Since > PresetsVersion).Select(p => p.Create()))
        {
            if (!Providers.Any(p => p.Kind == provider.Kind && SameAddress(p.BaseUrl, provider.BaseUrl)))
                Providers.Insert(at++, provider);
        }
        PresetsVersion = CurrentPresetsVersion;
        return true;
    }

    private static bool SameAddress(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The providers the settings page's "Add" offers (and a new configuration starts with).</summary>
    public static IReadOnlyList<ProviderPreset> Presets { get; } =
    [
        new("Claude (Anthropic)", 1, () => new()
        {
            Name = "Claude", Kind = ChatProvider.ClaudeKind, BaseUrl = "https://api.anthropic.com",
            // Claude Opus 5.5 thinks at "medium" when no effort is sent; set it explicitly (changeable in the bar).
            Model = "claude-opus-5-5", Effort = "medium",
        }),
        new("OpenAI (GPT)", 1, () => new() { Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", Model = "" }),
        new("DeepSeek", 0, () => new() { Name = "DeepSeek", BaseUrl = "https://api.deepseek.com", Model = "deepseek-chat" }),
        new("Ollama (local)", 0, () => new() { Name = "Ollama (local)", BaseUrl = "http://localhost:11434/v1", Model = "" }),
        new("LM Studio (local)", 0, () => new() { Name = "LM Studio (local)", BaseUrl = "http://localhost:1234/v1", Model = "" }),
    ];

    /// <summary>Claude's ready-made provider (its address and model fill a provider switched to Claude's API).</summary>
    public static ChatProvider ClaudePreset() => Presets.Select(p => p.Create()).First(p => p.IsClaude);
}

/// <summary>A ready-made provider: the menu text, the presets version that brought it, and a new provider with its address (and model).</summary>
public sealed record ProviderPreset(string Title, int Since, Func<ChatProvider> Create);

public sealed class ChatMessage
{
    /// <summary>"user", "assistant" or "tool" (the result of a file tool).</summary>
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>An error shown in the chat (not sent to the AI again).</summary>
    public bool IsError { get; set; }

    /// <summary>A user message: its pictures (names under data\gemmachat\images: "{chat}\{id}.png"), sent with it.</summary>
    public List<string>? Images { get; set; }

    /// <summary>A user message: its attached files (text files and PDFs), sent with it.</summary>
    public List<ChatFile>? Files { get; set; }

    /// <summary>An answer: the model that gave it (as the provider reports it) and the effort it was asked with ("" = default).</summary>
    public string? Model { get; set; }
    public string? Effort { get; set; }

    /// <summary>An assistant message: the tools the AI asked for.</summary>
    public List<ChatToolCall>? ToolCalls { get; set; }

    /// <summary>A tool message: the call it answers.</summary>
    public string? ToolCallId { get; set; }

    /// <summary>A tool message: the line shown in the chat ("Read Program.cs (120 lines)").</summary>
    public string? Summary { get; set; }

    /// <summary>A tool message that changed a file: what changed, and its backup for Undo.</summary>
    public ChatChange? Change { get; set; }

    /// <summary>
    /// A Claude answer: its content blocks as they came (thinking with its signature, text, tool calls, fallback
    /// marks), sent back unchanged in the next requests. Null for other providers' answers and stopped ones.
    /// </summary>
    public List<ChatBlock>? Blocks { get; set; }
}

/// <summary>A file attached to a message: its name, the copy kept with the chat, and how it is sent.</summary>
public sealed class ChatFile
{
    public string Name { get; set; } = "";

    /// <summary>The copy's name under data\gemmachat\files: "{chat}\{id}.txt".</summary>
    public string Stored { get; set; } = "";

    /// <summary>"text" (goes into the message as text) or "pdf" (goes as a document).</summary>
    public string Kind { get; set; } = "text";
}

/// <summary>One content block of a Claude answer.</summary>
public sealed class ChatBlock
{
    /// <summary>"text", "thinking", "redacted_thinking", "tool_use" or "fallback".</summary>
    public string Type { get; set; } = "";

    /// <summary>text: the text; thinking: the (summarized or empty) thinking text.</summary>
    public string? Text { get; set; }

    /// <summary>thinking: its signature (must come back unchanged).</summary>
    public string? Signature { get; set; }

    /// <summary>redacted_thinking: its data.</summary>
    public string? Data { get; set; }

    /// <summary>tool_use: the call's id, the tool, the input (JSON text as it came).</summary>
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Input { get; set; }

    /// <summary>fallback: the model that declined and the one that took over.</summary>
    public string? FromModel { get; set; }
    public string? ToModel { get; set; }
}

public sealed class ChatToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The arguments as the AI wrote them (JSON).</summary>
    public string Arguments { get; set; } = "";
}

/// <summary>A file change by the AI: the file, how, its diff for the chat, and the copy that Undo puts back.</summary>
public sealed class ChatChange
{
    public string Path { get; set; } = "";

    /// <summary>"create", "edit" or "delete".</summary>
    public string Kind { get; set; } = "";

    /// <summary>The diff shown in the chat: lines starting with "+", "-", " " or "…".</summary>
    public string Diff { get; set; } = "";

    /// <summary>"allowed", "allowed automatically", "refused".</summary>
    public string Decision { get; set; } = "";

    /// <summary>The file as it was before (null: it did not exist).</summary>
    public string? Backup { get; set; }

    public bool Undone { get; set; }
}

public sealed class ChatThread
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New chat";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = [];

    /// <summary>The folder the AI may read and change in this chat (null: no file work).</summary>
    public string? Workspace { get; set; }

    /// <summary>Tokens the last request sent (the chat's current size for the model) and the last answer's; 0 = not known.</summary>
    public int ContextTokens { get; set; }
    public int AnswerTokens { get; set; }

    /// <summary>The list's second line: the date in Windows' own format (a XAML StringFormat would use en-US), and the folder's name.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ModifiedText => Workspace is { } folder
        ? $"{System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder))} · {Modified:g}"
        : Modified.ToString("g");

    /// <summary>
    /// What the chat does right now, for the list ("Answering...", "Waits for your decision"); empty = nothing. Not
    /// saved: an answer runs only while DirectoryFable runs.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Activity { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBusy => Activity.Length > 0;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsIdle => Activity.Length == 0;

    /// <summary>The chat works in a folder: the list marks it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasWorkspace => Workspace is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? WorkspaceToolTip => Workspace is { } folder ? $"Works in the folder {folder}" : null;
}

/// <summary>
/// GemmaChat's files, all in data\gemmachat next to the exe (portable): gemmachat.json (providers with their API
/// keys as plain text - the user's choice; the folder is the protection) and chats\{id}.json, one file per chat.
/// </summary>
public static class ChatStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string Folder => Path.Combine(AppSettings.DataFolder, "gemmachat");

    /// <summary>Copies of files before the AI changed them (Undo): backups\{chat id}\{time}\{file name}.</summary>
    public static string BackupFolder => Path.Combine(Folder, "backups");
    public static string ConfigPath => Path.Combine(Folder, "gemmachat.json");

    /// <summary>Pictures of the messages: images\{chat id}\{id}.png (or .jpg / .gif).</summary>
    public static string ImagesFolder => Path.Combine(Folder, "images");

    public static string ImagePath(string name) => Path.Combine(ImagesFolder, name);

    /// <summary>Copies of the files attached to messages: files\{chat id}\{id}{extension}.</summary>
    public static string FilesFolder => Path.Combine(Folder, "files");

    public static string FilePath(string name) => Path.Combine(FilesFolder, name);

    /// <summary>Saves the copy of an attached file; returns its name for <see cref="ChatFile.Stored"/>.</summary>
    public static string SaveFile(string chatId, byte[] bytes, string extension)
    {
        string name = Path.Combine(chatId, Guid.NewGuid().ToString("N") + extension);
        string path = FilePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return name;
    }

    /// <summary>Saves a picture of a message; returns its name for <see cref="ChatMessage.Images"/>.</summary>
    public static string SaveImage(string chatId, byte[] bytes, string extension)
    {
        string name = Path.Combine(chatId, Guid.NewGuid().ToString("N") + extension);
        string path = ImagePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return name;
    }
    private static string ChatsFolder => Path.Combine(Folder, "chats");

    public static ChatConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath) && JsonSerializer.Deserialize<ChatConfig>(File.ReadAllText(ConfigPath), Json) is { } config)
            {
                config.AddNewPresets(); // Kept in memory; written with the next save.
                return config;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken file: start from the defaults (the file is not overwritten until the user saves).
        }
        return ChatConfig.Default();
    }

    public static void SaveConfig(ChatConfig config) => WriteAtomic(ConfigPath, JsonSerializer.Serialize(config, Json));

    /// <summary>All chats, the newest first.</summary>
    public static List<ChatThread> LoadChats()
    {
        var chats = new List<ChatThread>();
        if (!Directory.Exists(ChatsFolder))
            return chats;
        foreach (string file in Directory.EnumerateFiles(ChatsFolder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<ChatThread>(File.ReadAllText(file), Json) is { } chat)
                    chats.Add(chat);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Skipped; the file stays for the user to look at.
            }
        }
        return [.. chats.OrderByDescending(c => c.Modified)];
    }

    public static void SaveChat(ChatThread chat) => WriteAtomic(Path.Combine(ChatsFolder, chat.Id + ".json"), JsonSerializer.Serialize(chat, Json));

    public static void DeleteChat(ChatThread chat)
    {
        string path = Path.Combine(ChatsFolder, chat.Id + ".json");
        if (File.Exists(path))
            File.Delete(path);
        string backups = Path.Combine(BackupFolder, chat.Id);
        if (Directory.Exists(backups))
            Directory.Delete(backups, recursive: true); // Its Undo copies go with it.
        string images = Path.Combine(ImagesFolder, chat.Id);
        if (Directory.Exists(images))
            Directory.Delete(images, recursive: true); // And its pictures.
        string files = Path.Combine(FilesFolder, chat.Id);
        if (Directory.Exists(files))
            Directory.Delete(files, recursive: true); // And the copies of its attached files.
    }

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
