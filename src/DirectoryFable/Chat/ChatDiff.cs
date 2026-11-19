namespace DirectoryFable.Chat;

/// <summary>
/// Diffs for the chat's change cards, as text: one line per row, starting with "+" (added), "-" (removed), " " (kept,
/// as context) or "…" (rows left out). Long diffs are cut to <see cref="MaxRows"/> rows.
/// </summary>
public static class ChatDiff
{
    public const int MaxRows = 400;
    private const int Context = 2;

    public static string Added(string text) => Cut(Split(text).Select(l => "+" + l));

    public static string Removed(string text) => Cut(Split(text).Select(l => "-" + l));

    public static string Replaced(string oldText, string newText) =>
        Cut(Split(oldText).Select(l => "-" + l).Concat(Split(newText).Select(l => "+" + l)));

    /// <summary>A line diff (longest common subsequence) with two rows of context around each change.</summary>
    public static string Lines(string oldText, string newText)
    {
        var a = Split(oldText);
        var b = Split(newText);
        if ((long)a.Length * b.Length > 4_000_000)
            return Cut(new[] { $"…the whole file is replaced ({a.Length} -> {b.Length} lines)" }.Concat(b.Take(MaxRows).Select(l => "+" + l)));

        // LCS table from the end.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var rows = new List<string>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                rows.Add(" " + a[x]);
                x++;
                y++;
            }
            else if (y < b.Length && (x >= a.Length || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                rows.Add("+" + b[y++]);
            }
            else
            {
                rows.Add("-" + a[x++]);
            }
        }

        // Only the changes with a little context.
        var keep = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i][0] == ' ')
                continue;
            for (int k = Math.Max(0, i - Context); k <= Math.Min(rows.Count - 1, i + Context); k++)
                keep[k] = true;
        }
        var shown = new List<string>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (keep[i])
                shown.Add(rows[i]);
            else if (shown.Count == 0 || shown[^1] != "…")
                shown.Add("…");
        }
        if (shown.All(r => r == "…"))
            return " (no change)";
        return Cut(shown);
    }

    private static string[] Split(string text) => text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

    private static string Cut(IEnumerable<string> rows)
    {
        var list = rows.ToList();
        if (list.Count > MaxRows)
            list = [.. list.Take(MaxRows), $"…{list.Count - MaxRows} more rows"];
        return string.Join("\n", list);
    }
}
