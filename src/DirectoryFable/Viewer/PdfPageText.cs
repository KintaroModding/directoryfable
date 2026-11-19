using System.Text;
using System.Windows;

namespace DirectoryFable.Viewer;

/// <summary>
/// The text of one PDF page for selection: each character's Unicode code and its box, in a 0..1 square from the
/// page's top left corner (so it fits every zoom). Characters that PDFium adds (spaces between words, line ends) have
/// no box. Positions are character numbers in reading order. <paramref name="aspect"/> is the page's width divided by
/// its height (the 0..1 square is not square on paper).
/// </summary>
public sealed class PdfPageText(int[] codes, Rect[] boxes, double aspect)
{
    public static readonly PdfPageText Empty = new([], [], 1);

    public int Count => codes.Length;

    public bool IsEmpty => codes.Length == 0;

    private bool HasBox(int i) => boxes[i].Width > 0 && boxes[i].Height > 0;

    /// <summary>The character under the point, or -1.</summary>
    public int Hit(Point point)
    {
        for (int i = 0; i < boxes.Length; i++)
        {
            if (HasBox(i) && boxes[i].Contains(point))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// The character nearest to the point (dragging a selection past the end of a line or into a margin), or -1 when the
    /// page has no text. The line counts far more than the place in it: a point right of a line belongs to that line.
    /// </summary>
    public int Nearest(Point point)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < boxes.Length; i++)
        {
            if (!HasBox(i))
                continue;
            var box = boxes[i];
            double dx = point.X < box.Left ? box.Left - point.X : point.X > box.Right ? point.X - box.Right : 0;
            double dy = point.Y < box.Top ? box.Top - point.Y : point.Y > box.Bottom ? point.Y - box.Bottom : 0;
            double distance = dy * 50 + dx;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    /// <summary>The word around a character (letters and digits), as first and last character.</summary>
    public (int From, int To) WordAt(int index)
    {
        if (!IsWordPart(index))
            return (index, index);
        int from = index, to = index;
        while (from > 0 && IsWordPart(from - 1))
            from--;
        while (to < codes.Length - 1 && IsWordPart(to + 1))
            to++;
        return (from, to);
    }

    private bool IsWordPart(int i) => codes[i] is > 0 and <= 0x10FFFF && codes[i] is not (>= 0xD800 and <= 0xDFFF) &&
                                      (Rune.IsLetterOrDigit(new Rune(codes[i])) || codes[i] is '_' or '\'');

    /// <summary>The line of a character (up to the line ends PDFium found), as first and last character.</summary>
    public (int From, int To) LineAt(int index)
    {
        int from = index, to = index;
        while (from > 0 && codes[from - 1] is not ('\r' or '\n'))
            from--;
        while (to < codes.Length - 1 && codes[to + 1] is not ('\r' or '\n'))
            to++;
        return (from, to);
    }

    /// <summary>The text from character <paramref name="from"/> to <paramref name="to"/> (both included), lines ending with CR LF.</summary>
    public string Text(int from, int to)
    {
        var text = new StringBuilder();
        for (int i = Math.Max(0, from); i <= to && i < codes.Length; i++)
        {
            int code = codes[i];
            if (code == '\n')
            {
                // PDFium gives CR LF, or LF alone: always one Windows line end.
                if (text.Length == 0 || text[^1] != '\r')
                    text.Append('\r');
                text.Append('\n');
            }
            else if (code == '\r')
            {
                text.Append('\r');
            }
            else if (code == '\t' || (code >= 0x20 && code != 0xFFFE && code != 0xFFFF && code <= 0x10FFFF && code is not (>= 0xD800 and <= 0xDFFF)))
            {
                text.Append(char.ConvertFromUtf32(code));
            }
            // Other control codes (PDFium's mark for a word split at a line end, ...) are left out.
        }
        return text.ToString().Replace("\r\r\n", "\r\n", StringComparison.Ordinal);
    }

    /// <summary>The highlight for a range: one rectangle per line (characters that stand on the same line are joined).</summary>
    public IReadOnlyList<Rect> Rects(int from, int to)
    {
        var rects = new List<Rect>();
        Rect line = default, previous = default;
        bool open = false;
        for (int i = Math.Max(0, from); i <= to && i < boxes.Length; i++)
        {
            if (!HasBox(i))
                continue;
            var box = boxes[i];
            if (open && (SameLine(line, box) || RunsDown(previous, box)))
            {
                line.Union(box);
                previous = box;
                continue;
            }
            if (open)
                rects.Add(line);
            line = previous = box;
            open = true;
        }
        if (open)
            rects.Add(line);
        return rects;
    }

    /// <summary>Two boxes share a line when they overlap by more than half of the lower one's height.</summary>
    private static bool SameLine(Rect line, Rect box)
    {
        double overlap = Math.Min(line.Bottom, box.Bottom) - Math.Max(line.Top, box.Top);
        return overlap > 0.5 * Math.Min(line.Height, box.Height);
    }

    /// <summary>
    /// Text that runs down or up the page (a turned page, a label along an edge): the next character stands above or
    /// below the one before it, in the same column, close to it.
    /// </summary>
    private bool RunsDown(Rect previous, Rect box)
    {
        double overlap = Math.Min(previous.Right, box.Right) - Math.Max(previous.Left, box.Left);
        if (overlap <= 0.5 * Math.Min(previous.Width, box.Width))
            return false;
        double acrossPage = Math.Abs((box.Left + box.Right) - (previous.Left + previous.Right)) / 2 * aspect;
        double downPage = Math.Abs((box.Top + box.Bottom) - (previous.Top + previous.Bottom)) / 2;
        double gap = Math.Max(box.Top - previous.Bottom, previous.Top - box.Bottom);
        return downPage > acrossPage && gap < 1.5 * Math.Max(previous.Height, box.Height);
    }
}
