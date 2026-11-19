using System.IO;
using System.Text;
using System.Text.Json;

namespace DirectoryFable.Chat;

/// <summary>A change the AI wants to make, shown for approval: what, which file, the diff.</summary>
public sealed record ChangeRequest(string Kind, string RelativePath, string FullPath, string Diff);

/// <summary>What a tool call gives back: the text for the AI, the line for the chat, and a change made (for Undo).</summary>
public sealed record ToolOutcome(string Content, string Summary, ChatChange? Change = null);

/// <summary>
/// GemmaChat's file tools in one workspace folder: list, read, search, create / overwrite, edit, delete. Every path
/// is relative to the workspace and must stay inside it (no "..", no other drive). Changes go through
/// <c>approve</c> (the chat asks the user, or not when the user switched that off; deleting always asks), and
/// before a change the file is copied to the chat's backup folder, so Undo can put it back. Existing files keep their
/// line endings (CRLF or LF) and their UTF-8 byte order mark.
/// </summary>
public sealed class WorkspaceTools(string root, string chatId, Func<ChangeRequest, Task<(bool Allowed, string Decision)>> approve,
    Func<string, Task<bool>> deleteToRecycleBin)
{
    private const int MaxRead = 400_000;     // Characters given to the AI from one file.
    private const int MaxListed = 600;
    private const int MaxMatches = 200;
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", "node_modules", "bin", "obj" };

    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>JSON for the model to read: quotes as \" (not ").</summary>
    private static readonly JsonSerializerOptions RawJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The tool descriptions in the API's format (JSON Schema for the arguments).</summary>
    public static JsonElement Definitions { get; } = JsonDocument.Parse("""
        [
          {"type":"function","function":{"name":"list_files","description":"List the files and folders in a folder of the workspace. Folders end with /.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Folder relative to the workspace; \".\" for the workspace itself."},"recursive":{"type":"boolean","description":"Also list everything below (skips .git, node_modules, bin, obj)."}},"required":["path"]}}},
          {"type":"function","function":{"name":"read_file","description":"Read a text file of the workspace. Long files can be read in parts by line numbers (1-based).","parameters":{"type":"object","properties":{"path":{"type":"string"},"start_line":{"type":"integer"},"end_line":{"type":"integer"}},"required":["path"]}}},
          {"type":"function","function":{"name":"search_files","description":"Search the text files of the workspace for a text (not case-sensitive). Returns file:line: line.","parameters":{"type":"object","properties":{"query":{"type":"string"},"path":{"type":"string","description":"Folder to search in, relative; default the whole workspace."},"file_pattern":{"type":"string","description":"For example *.cs; default all files."}},"required":["query"]}}},
          {"type":"function","function":{"name":"write_file","description":"Create a new file, or replace the whole content of a file. Folders are created as needed. The user approves it first.","parameters":{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}}},
          {"type":"function","function":{"name":"edit_file","description":"Change part of a file: old_text must appear exactly once in the file (copy it exactly, with enough lines to be unique) and is replaced by new_text. The user approves it first.","parameters":{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"}},"required":["path","old_text","new_text"]}}},
          {"type":"function","function":{"name":"delete_file","description":"Delete a file (to the Recycle Bin). The user is always asked first.","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}}
        ]
        """).RootElement.Clone();

    /// <summary>The instructions added to the system prompt while a workspace is open.</summary>
    public static string Instructions(string root) =>
        $"You can work on the files in the folder \"{root}\" with the tools list_files, read_file, search_files, write_file, " +
        "edit_file and delete_file. Paths are relative to that folder; nothing outside it can be reached. Read a file before " +
        "you change it. For changes to existing files use edit_file with an exact, unique piece of the file; use write_file for " +
        "new files or complete rewrites. The user approves every change and may refuse it; after a refusal, do not try the same " +
        "change again unless the user asks. Deleting a file always needs the user's approval. Say briefly what you changed.";

    public async Task<ToolOutcome> RunAsync(ChatToolCall call)
    {
        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(call.Arguments.Length > 0 ? call.Arguments : "{}");
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // The raw text goes back (the model can send the call again); built by the serializer so quotes are escaped.
            return new ToolOutcome("Error: the arguments are no valid JSON (perhaps cut off). " +
                                   JsonSerializer.Serialize(new Dictionary<string, string> { ["INVALID_JSON"] = call.Arguments }, RawJson),
                $"{call.Name}: unreadable arguments");
        }
        if (SchemaError(call.Name, args) is { } problem)
            return new ToolOutcome($"Error: {problem} Send the call again with all required arguments.", $"{call.Name}: {problem}");
        try
        {
            return call.Name switch
            {
                "list_files" => List(args),
                "read_file" => Read(args),
                "search_files" => Search(args),
                "write_file" => await WriteAsync(args),
                "edit_file" => await EditAsync(args),
                "delete_file" => await DeleteAsync(args),
                _ => new ToolOutcome($"Error: there is no tool \"{call.Name}\".", $"Unknown tool {call.Name}"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ToolOutcome($"Error: {ex.Message}", $"{call.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The arguments checked against the tool's schema (an object, the required ones there, texts as texts):
    /// a streamed call can come cut off or broken, and a missing "content" must not write an empty file.
    /// </summary>
    private static string? SchemaError(string tool, JsonElement args)
    {
        var definition = Definitions.EnumerateArray()
            .Select(d => d.GetProperty("function"))
            .FirstOrDefault(f => f.GetProperty("name").GetString() == tool);
        if (definition.ValueKind != JsonValueKind.Object)
            return null; // An unknown tool: RunAsync says so.
        if (args.ValueKind != JsonValueKind.Object)
            return "the arguments are no JSON object.";
        var schema = definition.GetProperty("parameters");
        foreach (var required in schema.GetProperty("required").EnumerateArray())
        {
            string name = required.GetString()!;
            if (!args.TryGetProperty(name, out _))
                return $"the argument \"{name}\" is missing.";
        }
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            if (!args.TryGetProperty(property.Name, out var value) || value.ValueKind == JsonValueKind.Null)
                continue;
            // Only texts are checked: numbers and switches of the wrong kind are ignored by the tools (as before).
            if (property.Value.GetProperty("type").GetString() == "string" && value.ValueKind != JsonValueKind.String)
                return $"the argument \"{property.Name}\" has the wrong type.";
        }
        return null;
    }

    // ---------- Paths ----------

    private static string Text(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : null;

    /// <summary>The full path of a workspace path; throws when it would leave the workspace.</summary>
    private string Resolve(string relative)
    {
        string trimmed = relative.Trim().Replace('/', '\\');
        if (trimmed.Length == 0 || trimmed == ".")
            return _root;
        if (Path.IsPathRooted(trimmed))
        {
            // An absolute path is accepted only inside the workspace.
            string absolute = Path.GetFullPath(trimmed);
            return Inside(absolute) ? absolute : throw new ArgumentException($"\"{relative}\" is outside the workspace.");
        }
        string full = Path.GetFullPath(Path.Combine(_root, trimmed));
        return Inside(full) ? full : throw new ArgumentException($"\"{relative}\" is outside the workspace.");
    }

    private bool Inside(string full) =>
        string.Equals(full, _root, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private string Relative(string full) => full.Length <= _root.Length ? "." : full[(_root.Length + 1)..];

    // ---------- Reading ----------

    private ToolOutcome List(JsonElement args)
    {
        string folder = Resolve(Text(args, "path"));
        if (!Directory.Exists(folder))
            return new ToolOutcome($"Error: the folder \"{Text(args, "path")}\" does not exist.", $"List {Text(args, "path")}: no such folder");
        bool recursive = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("recursive", out var r) && r.ValueKind == JsonValueKind.True;
        var lines = new List<string>();
        void Walk(string dir, int depth)
        {
            foreach (string sub in Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (lines.Count >= MaxListed)
                    return;
                lines.Add(Relative(sub) + "/");
                if (recursive && depth < 12 && !SkippedFolders.Contains(Path.GetFileName(sub)))
                    Walk(sub, depth + 1);
            }
            foreach (string file in Directory.EnumerateFiles(dir).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (lines.Count >= MaxListed)
                    return;
                lines.Add($"{Relative(file)} ({new FileInfo(file).Length:N0} bytes)");
            }
        }
        Walk(folder, 0);
        string more = lines.Count >= MaxListed ? $"\n(only the first {MaxListed} entries)" : "";
        return new ToolOutcome(lines.Count == 0 ? "(empty folder)" : string.Join("\n", lines) + more,
            $"Listed {(folder.Length <= _root.Length ? "the folder" : Relative(folder))} ({lines.Count} {(lines.Count == 1 ? "entry" : "entries")})");
    }

    private ToolOutcome Read(JsonElement args)
    {
        string path = Resolve(Text(args, "path"));
        if (!File.Exists(path))
            return new ToolOutcome($"Error: the file \"{Text(args, "path")}\" does not exist.", $"Read {Text(args, "path")}: no such file");
        if (IsBinary(path))
            return new ToolOutcome($"This is a binary file ({new FileInfo(path).Length:N0} bytes); it cannot be read as text.", $"Read {Relative(path)}: binary");
        var lines = File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n');
        int start = Math.Clamp(Int(args, "start_line") ?? 1, 1, Math.Max(1, lines.Length));
        int end = Math.Clamp(Int(args, "end_line") ?? lines.Length, start, lines.Length);
        string text = string.Join("\n", lines[(start - 1)..end]);
        string note = "";
        if (text.Length > MaxRead)
        {
            text = text[..MaxRead];
            note = $"\n(cut after {MaxRead:N0} characters: read the rest with start_line / end_line)";
        }
        string header = start > 1 || end < lines.Length ? $"Lines {start}-{end} of {lines.Length}:\n" : "";
        return new ToolOutcome(header + text + note,
            $"Read {Relative(path)} ({(header.Length > 0 ? $"lines {start}-{end} of " : "")}{lines.Length} lines)");
    }

    private ToolOutcome Search(JsonElement args)
    {
        string query = Text(args, "query");
        if (query.Length == 0)
            return new ToolOutcome("Error: an empty search.", "Search: empty");
        string folder = Resolve(Text(args, "path"));
        string pattern = Text(args, "file_pattern") is { Length: > 0 } p ? p : "*";
        var matches = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (string file in Directory.EnumerateFiles(folder, pattern, options))
        {
            if (matches.Count >= MaxMatches)
                break;
            if (Relative(file).Split('\\').Any(SkippedFolders.Contains) || new FileInfo(file).Length > 5_000_000 || IsBinary(file))
                continue;
            int number = 0;
            foreach (string line in File.ReadLines(file))
            {
                number++;
                if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add($"{Relative(file)}:{number}: {(line.Length > 300 ? line[..300] + "..." : line.Trim())}");
                    if (matches.Count >= MaxMatches)
                        break;
                }
            }
        }
        return new ToolOutcome(matches.Count == 0 ? "No matches." : string.Join("\n", matches),
            $"Searched for \"{query}\" ({matches.Count}{(matches.Count >= MaxMatches ? "+" : "")} {(matches.Count == 1 ? "match" : "matches")})");
    }

    private static bool IsBinary(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[8192];
        int read = stream.Read(buffer, 0, buffer.Length);
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }

    // ---------- Changing ----------

    private async Task<ToolOutcome> WriteAsync(JsonElement args)
    {
        string path = Resolve(Text(args, "path"));
        string content = Text(args, "content");
        bool exists = File.Exists(path);
        string? old = exists ? (IsBinary(path) ? null : File.ReadAllText(path)) : null;
        if (exists && old is null)
            return new ToolOutcome("Error: this is a binary file; it is not overwritten.", $"Write {Relative(path)}: binary, refused");
        string diff = exists ? ChatDiff.Lines(old!, content) : ChatDiff.Added(content);
        var (allowed, decision) = await approve(new ChangeRequest(exists ? "edit" : "create", Relative(path), path, diff));
        if (!allowed)
            return new ToolOutcome("The user refused this change.", $"{(exists ? "Rewrite" : "Create")} {Relative(path)}: refused",
                new ChatChange { Path = path, Kind = exists ? "edit" : "create", Diff = diff, Decision = decision });
        string? backup = exists ? Backup(path) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteKeepingStyle(path, content, exists);
        int lines = content.ReplaceLineEndings("\n").Split('\n').Length;
        return new ToolOutcome(exists ? $"The file was replaced ({lines} lines)." : $"The file was created ({lines} lines).",
            $"{(exists ? "Rewrote" : "Created")} {Relative(path)} ({lines} lines)",
            new ChatChange { Path = path, Kind = exists ? "edit" : "create", Diff = diff, Decision = decision, Backup = backup });
    }

    private async Task<ToolOutcome> EditAsync(JsonElement args)
    {
        string path = Resolve(Text(args, "path"));
        if (!File.Exists(path))
            return new ToolOutcome($"Error: the file \"{Text(args, "path")}\" does not exist.", $"Edit {Text(args, "path")}: no such file");
        if (IsBinary(path))
            return new ToolOutcome("Error: this is a binary file.", $"Edit {Relative(path)}: binary, refused");
        string text = File.ReadAllText(path).ReplaceLineEndings("\n");
        string oldText = Text(args, "old_text").ReplaceLineEndings("\n");
        string newText = Text(args, "new_text").ReplaceLineEndings("\n");
        if (oldText.Length == 0)
            return new ToolOutcome("Error: old_text is empty.", $"Edit {Relative(path)}: nothing to replace");
        int first = text.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
            return new ToolOutcome("Error: old_text was not found in the file. Read the file again and copy the text exactly.",
                $"Edit {Relative(path)}: text not found");
        if (text.IndexOf(oldText, first + 1, StringComparison.Ordinal) >= 0)
            return new ToolOutcome("Error: old_text appears more than once. Add lines around it so it is unique.",
                $"Edit {Relative(path)}: text not unique");
        string diff = ChatDiff.Replaced(oldText, newText);
        var (allowed, decision) = await approve(new ChangeRequest("edit", Relative(path), path, diff));
        if (!allowed)
            return new ToolOutcome("The user refused this change.", $"Edit {Relative(path)}: refused",
                new ChatChange { Path = path, Kind = "edit", Diff = diff, Decision = decision });
        string backup = Backup(path);
        WriteKeepingStyle(path, text[..first] + newText + text[(first + oldText.Length)..], exists: true);
        return new ToolOutcome("The change was made.", $"Edited {Relative(path)}",
            new ChatChange { Path = path, Kind = "edit", Diff = diff, Decision = decision, Backup = backup });
    }

    private async Task<ToolOutcome> DeleteAsync(JsonElement args)
    {
        string path = Resolve(Text(args, "path"));
        if (!File.Exists(path))
            return new ToolOutcome($"Error: the file \"{Text(args, "path")}\" does not exist.", $"Delete {Text(args, "path")}: no such file");
        string diff = IsBinary(path) ? $"…binary file, {new FileInfo(path).Length:N0} bytes" : ChatDiff.Removed(File.ReadAllText(path));
        var (allowed, decision) = await approve(new ChangeRequest("delete", Relative(path), path, diff));
        if (!allowed)
            return new ToolOutcome("The user refused to delete it.", $"Delete {Relative(path)}: refused",
                new ChatChange { Path = path, Kind = "delete", Diff = diff, Decision = decision });
        string backup = Backup(path);
        if (!await deleteToRecycleBin(path))
            return new ToolOutcome("Error: the file could not be deleted.", $"Delete {Relative(path)}: failed");
        return new ToolOutcome("The file was deleted (it is in the Recycle Bin).", $"Deleted {Relative(path)}",
            new ChatChange { Path = path, Kind = "delete", Diff = diff, Decision = decision, Backup = backup });
    }

    /// <summary>Copies the file to backups\{chat}\{time}\ before it changes; returns the copy's path.</summary>
    private string Backup(string path)
    {
        string folder = Path.Combine(ChatStore.BackupFolder, chatId, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, Path.GetFileName(path));
        File.Copy(path, copy, overwrite: true);
        return copy;
    }

    /// <summary>Writes text with the file's own line endings and byte order mark (new files: CRLF, no BOM).</summary>
    private static void WriteKeepingStyle(string path, string content, bool exists)
    {
        bool crlf = true, bom = false;
        if (exists)
        {
            byte[] head = new byte[3];
            using (var s = File.OpenRead(path))
                bom = s.Read(head, 0, 3) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
            string old = File.ReadAllText(path);
            crlf = old.Contains("\r\n") || !old.Contains('\n');
        }
        string text = content.ReplaceLineEndings("\n");
        if (crlf)
            text = text.Replace("\n", "\r\n");
        File.WriteAllText(path, text, new UTF8Encoding(bom));
    }

    /// <summary>Undo: the file as it was (a created file goes away).</summary>
    public static void Undo(ChatChange change)
    {
        if (change.Backup is { } backup && File.Exists(backup))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
            File.Copy(backup, change.Path, overwrite: true);
        }
        else if (change.Kind == "create" && File.Exists(change.Path))
        {
            File.Delete(change.Path);
        }
        change.Undone = true;
    }
}
