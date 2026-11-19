using System.IO;
using System.Text.RegularExpressions;
using DirectoryFable.Editor;

namespace DirectoryFable.Search;

/// <summary>A hit inside a file: the line (1-based), where in it (0-based) and the line's text.</summary>
public sealed record ContentMatch(string Path, int Line, int Column, int Length, string LineText);

/// <summary>
/// Searching inside files (Notepad#'s Find in files, SonnetSearch's "Containing text"). A file is read whole and
/// decoded like Notepad# does it (byte order marks, UTF-16, UTF-8, else the Windows ANSI code page); files with
/// binary data and files larger than <see cref="MaxFileSize"/> are skipped. The pattern is plain text or a regular
/// expression, with match case and whole words.
/// </summary>
public static class ContentSearch
{
    /// <summary>Larger files are not searched (logs and dumps of that size are rarely what one looks for).</summary>
    public const long MaxFileSize = 64L * 1024 * 1024;

    /// <summary>The pattern as a regular expression (null for an empty pattern); throws ArgumentException for a broken regex.</summary>
    public static Regex? Build(string pattern, bool matchCase, bool wholeWords, bool regex)
    {
        if (pattern.Length == 0)
            return null;
        string body = regex ? pattern : Regex.Escape(pattern);
        if (wholeWords)
            body = $@"(?<!\w)(?:{body})(?!\w)";
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!matchCase)
            options |= RegexOptions.IgnoreCase;
        return new Regex(body, options, TimeSpan.FromSeconds(2));
    }

    /// <summary>The file's text, or null when it is binary, too large or cannot be read.</summary>
    public static string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileSize)
                return null;
            return EditorDocument.Decode(File.ReadAllBytes(path)).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
                                       or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Does the file contain the pattern? (Binary, large and unreadable files: no.)</summary>
    public static bool Contains(string path, Regex pattern)
    {
        string? text = ReadText(path);
        if (text is null)
            return false;
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The hits in a file, at most <paramref name="max"/>; one entry per hit (two hits in a line: two entries).</summary>
    public static List<ContentMatch> Matches(string path, Regex pattern, int max, CancellationToken token)
    {
        var result = new List<ContentMatch>();
        string? text = ReadText(path);
        if (text is null)
            return result;
        int line = 1, lineStart = 0, scanned = 0;
        try
        {
            foreach (Match m in pattern.Matches(text))
            {
                token.ThrowIfCancellationRequested();
                if (m.Length == 0)
                    continue;
                // Count the lines up to this hit (only the part not counted yet).
                for (int i = scanned; i < m.Index; i++)
                {
                    if (text[i] == '\n')
                    {
                        line++;
                        lineStart = i + 1;
                    }
                }
                scanned = m.Index;
                int lineEnd = text.IndexOf('\n', m.Index);
                if (lineEnd < 0)
                    lineEnd = text.Length;
                string lineText = text[lineStart..lineEnd].TrimEnd('\r');
                result.Add(new ContentMatch(path, line, m.Index - lineStart, Math.Min(m.Length, Math.Max(0, lineText.Length - (m.Index - lineStart))), lineText));
                if (result.Count >= max)
                    break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A pattern that runs too long: the hits so far.
        }
        return result;
    }
}
