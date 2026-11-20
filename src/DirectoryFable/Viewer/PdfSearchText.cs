using System.Text;

namespace DirectoryFable.Viewer;

/// <summary>
/// The text of one PDF page, made ready for a search: every run of spaces and line ends is one space, a word that
/// was split at a line end is joined again, and ligature characters are spelled out. Each character knows the
/// page's character number it came from, so a hit maps back to the page (see <see cref="PdfPageText"/>).
/// </summary>
public sealed class PdfSearchText
{
    /// <summary>More hits than this on one page are not listed (a search for one letter in a long table).</summary>
    public const int MaxHitsPerPage = 2000;

    private readonly string _text;
    private readonly int[] _positions; // For each character of _text: the page's character number.

    private PdfSearchText(string text, int[] positions)
    {
        _text = text;
        _positions = positions;
    }

    /// <summary>Builds the search text from the page's character codes, in reading order.</summary>
    public static PdfSearchText From(int[] codes)
    {
        var text = new StringBuilder(codes.Length);
        var positions = new List<int>(codes.Length);
        bool joined = false; // After the mark of a split word: the line end that follows is no space.
        for (int i = 0; i < codes.Length; i++)
        {
            int code = codes[i];
            if (code is 0x2 or 0xFFFE)
            {
                joined = true; // PDFium's mark for the hyphen of a word split at a line end.
                continue;
            }
            if (code <= 0xFFFF && char.IsWhiteSpace((char)code))
            {
                if (!joined && text.Length > 0 && text[^1] != ' ')
                {
                    text.Append(' ');
                    positions.Add(i);
                }
                continue;
            }
            joined = false;
            if (code < 0x20 || code is 0xAD or 0xFFFF || code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
                continue; // Control codes, the soft hyphen, broken codes.
            string piece = Spelled(code);
            text.Append(piece);
            for (int n = 0; n < piece.Length; n++)
                positions.Add(i);
        }
        return new PdfSearchText(text.ToString(), [.. positions]);
    }

    /// <summary>A ligature as its letters ("fi" for the one-character fi), so a typed word finds it.</summary>
    private static string Spelled(int code) => code switch
    {
        0xFB00 => "ff",
        0xFB01 => "fi",
        0xFB02 => "fl",
        0xFB03 => "ffi",
        0xFB04 => "ffl",
        0xFB05 or 0xFB06 => "st",
        _ => char.ConvertFromUtf32(code),
    };

    /// <summary>The typed text as it is searched: spaces at the ends go, runs of spaces become one.</summary>
    public static string Normalize(string query)
    {
        var text = new StringBuilder(query.Length);
        foreach (char c in query)
        {
            if (char.IsWhiteSpace(c))
            {
                if (text.Length > 0 && text[^1] != ' ')
                    text.Append(' ');
            }
            else if (c is >= (char)0xFB00 and <= (char)0xFB06)
                text.Append(Spelled(c));
            else
                text.Append(c);
        }
        return text.ToString().TrimEnd(' ');
    }

    /// <summary>
    /// Every place the text stands on the page, as the first and last page character of the hit (both included),
    /// in reading order. Hits do not overlap. <paramref name="query"/> must come from <see cref="Normalize"/>.
    /// </summary>
    public IReadOnlyList<(int From, int To)> Find(string query, bool matchCase)
    {
        var hits = new List<(int From, int To)>();
        if (query.Length == 0)
            return hits;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int at = 0;
        while (at <= _text.Length - query.Length && hits.Count < MaxHitsPerPage)
        {
            int found = _text.IndexOf(query, at, comparison);
            if (found < 0)
                break;
            hits.Add((_positions[found], _positions[found + query.Length - 1]));
            at = found + query.Length;
        }
        return hits;
    }
}
