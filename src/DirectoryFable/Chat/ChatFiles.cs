using System.IO;
using System.Text;
using DirectoryFable.Editor;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Chat;

/// <summary>A file for the next message: its name, kind ("text" or "pdf"), what is stored and its size as text.</summary>
public sealed record PendingFile(string Name, string Kind, byte[] Bytes, string Extension, string SizeText);

/// <summary>
/// Files attached to GemmaChat messages. A text file is read like Notepad# reads it (any usual encoding) and goes
/// into the message as text; a PDF goes as a document (the provider must read PDFs: Claude and GPT do). Other files
/// (binary) cannot be attached. A copy as it was when attached is kept next to the chat
/// (data\gemmachat\files\{chat}\), so the chat stays the same when the file changes later.
/// </summary>
public static class ChatFiles
{
    /// <summary>Longer texts are not attached (open their folder as the chat's workspace instead: the AI then reads parts).</summary>
    public const int MaxTextChars = 400_000;
    public const long MaxPdfBytes = 20L * 1024 * 1024;

    /// <summary>The file ready to attach, or null with what is wrong with it.</summary>
    public static PendingFile? FromFile(string path, out string? problem)
    {
        problem = null;
        string name = Path.GetFileName(path);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                problem = $"{name}: not a file.";
                return null;
            }
            if (string.Equals(info.Extension, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                if (info.Length > MaxPdfBytes)
                {
                    problem = $"{name}: PDF files up to {DisplayFormat.Size(MaxPdfBytes)} can be attached.";
                    return null;
                }
                return new PendingFile(name, "pdf", File.ReadAllBytes(path), ".pdf", DisplayFormat.Size(info.Length));
            }
            if (info.Length > MaxTextChars * 4L)
            {
                problem = $"{name}: too large to attach. Open its folder in GemmaChat instead: the AI then reads the parts it needs.";
                return null;
            }
            string text = EditorDocument.Decode(File.ReadAllBytes(path)).Text;
            if (text.Length > MaxTextChars)
            {
                problem = $"{name}: too long to attach ({text.Length:N0} characters; at most {MaxTextChars:N0}). Open its folder in GemmaChat instead.";
                return null;
            }
            // Kept as UTF-8, whatever the file's own encoding was.
            return new PendingFile(name, "text", Encoding.UTF8.GetBytes(text), info.Extension.Length > 0 ? info.Extension : ".txt",
                DisplayFormat.Size(info.Length));
        }
        catch (InvalidDataException)
        {
            problem = $"{name}: not a text file (text files, PDF files and pictures can be attached).";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = $"{name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// The message's text for the AI: each attached text file first, as &lt;file name="..."&gt; ... &lt;/file&gt;, then
    /// what the user wrote. Files whose copy is gone are named as missing.
    /// </summary>
    public static string TextWithFiles(ChatMessage message)
    {
        if (message.Files is not { Count: > 0 } files || files.All(f => f.Kind != "text"))
            return message.Content;
        var text = new StringBuilder();
        foreach (var file in files.Where(f => f.Kind == "text"))
        {
            string path = ChatStore.FilePath(file.Stored);
            try
            {
                text.Append("<file name=\"").Append(file.Name).Append("\">\n").Append(File.ReadAllText(path, Encoding.UTF8));
                if (text[^1] != '\n')
                    text.Append('\n');
                text.Append("</file>\n\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                text.Append("(The attached file \"").Append(file.Name).Append("\" is no longer there.)\n\n");
            }
        }
        return text.Append(message.Content).ToString();
    }

    /// <summary>The message's PDF files for the API: name and bytes as base64 (those whose copy is gone are left out).</summary>
    public static List<(string Name, string Base64)> Pdfs(ChatMessage message)
    {
        var result = new List<(string, string)>();
        foreach (var file in message.Files?.Where(f => f.Kind == "pdf") ?? [])
        {
            try
            {
                result.Add((file.Name, Convert.ToBase64String(File.ReadAllBytes(ChatStore.FilePath(file.Stored)))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left out.
            }
        }
        return result;
    }
}
