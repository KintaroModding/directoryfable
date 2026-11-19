using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DirectoryFable.Views;

/// <summary>
/// Read-only, selectable text with the Markdown an AI answer usually has: headings, bold, italic, strikethrough,
/// `inline code`, bullet and numbered lists (nested by indent), quotes, tables, rules and http(s) links. Code blocks
/// (``` fences) are not handled here: GemmaChat shows them in their own boxes. Unknown syntax stays as it is.
/// While an answer streams in, the document is built again with each new piece (cheap for a text part).
/// </summary>
public sealed partial class MarkdownBox : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownBox), new PropertyMetadata("", (d, _) => ((MarkdownBox)d).Build()));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownBox()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true; // Links can be clicked.
        Document = new FlowDocument();
    }

    private static readonly Brush CodeBack = Frozen(0x12, 0x12, 0x12);
    private static readonly Brush RuleBrush = Frozen(0x3A, 0x3A, 0x3A);
    private static readonly Brush QuoteBrush = Frozen(0x4A, 0x4A, 0x4A);
    private static readonly Brush LinkBrush = Frozen(0x6C, 0xB6, 0xFF);
    private static readonly FontFamily Mono = new("Consolas");

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    /// <summary>Inline marks: `code`, **bold** / __bold__, ~~strike~~, *italic*, [text](http link).</summary>
    [GeneratedRegex(@"(`+)(.+?)\1|\*\*(.+?)\*\*|__(?=\S)(.+?)(?<=\S)__(?!\w)|~~(.+?)~~|(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])|\[([^\]]+)\]\((https?://[^\s)]+)\)")]
    private static partial Regex Inline();

    private void Build()
    {
        // Font, size and color come from the box (the document inherits them).
        var document = new FlowDocument { PagePadding = new Thickness(0) };
        var lines = (Markdown ?? "").ReplaceLineEndings("\n").Split('\n');
        Paragraph? paragraph = null;
        var lists = new List<(int Indent, List List)>(); // The open lists, outermost first.

        void EndParagraph() => paragraph = null;
        void EndLists()
        {
            if (lists.Count > 0)
                document.Blocks.Add(lists[0].List);
            lists.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0)
            {
                EndParagraph();
                // A blank line between list items keeps the list; anything else after it ends it.
                if (lists.Count > 0 && (i + 1 >= lines.Length || !ListItem().IsMatch(lines[i + 1])))
                    EndLists();
                continue;
            }

            if (Heading().Match(line) is { Success: true } heading)
            {
                EndParagraph();
                EndLists();
                int level = heading.Groups[1].Length;
                var p = new Paragraph
                {
                    FontSize = FontSize * (level switch { 1 => 1.45, 2 => 1.25, 3 => 1.1, _ => 1.0 }),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, document.Blocks.Count == 0 ? 0 : 10, 0, 4),
                };
                AddInlines(p.Inlines, heading.Groups[2].Value);
                document.Blocks.Add(p);
                continue;
            }

            if (Rule().IsMatch(line))
            {
                EndParagraph();
                EndLists();
                document.Blocks.Add(new BlockUIContainer(new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 6, 0, 6) }));
                continue;
            }

            // A table: a row of cells, then a separator row (|---|---|).
            if (line.Contains('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1]) && lines[i + 1].Contains('-'))
            {
                EndParagraph();
                EndLists();
                var rows = new List<string> { line };
                i += 2;
                while (i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0)
                    rows.Add(lines[i++]);
                i--;
                document.Blocks.Add(MakeTable(rows));
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                EndParagraph();
                EndLists();
                var quote = new Paragraph { Margin = new Thickness(0, 2, 0, 6), Foreground = Brushes.Gray };
                var lineInlines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                    lineInlines.Add(lines[i++].TrimStart()[1..].TrimStart());
                i--;
                for (int k = 0; k < lineInlines.Count; k++)
                {
                    if (k > 0)
                        quote.Inlines.Add(new LineBreak());
                    AddInlines(quote.Inlines, lineInlines[k]);
                }
                document.Blocks.Add(new Section(quote)
                {
                    BorderBrush = QuoteBrush,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 0, 0, 0),
                    Margin = new Thickness(0, 2, 0, 6),
                });
                continue;
            }

            if (ListItem().Match(line) is { Success: true } item)
            {
                EndParagraph();
                int indent = item.Groups[1].Value.Replace("\t", "    ").Length;
                string marker = item.Groups[2].Value;
                bool numbered = char.IsDigit(marker[0]);
                // Close lists that are deeper than this item.
                while (lists.Count > 1 && indent < lists[^1].Indent)
                    lists.RemoveAt(lists.Count - 1);
                // Bullets after numbers (or the other way round) on the same level: a new list.
                if (lists.Count > 0 && indent <= lists[^1].Indent + 1 && (lists[^1].List.MarkerStyle == TextMarkerStyle.Decimal) != numbered)
                {
                    if (lists.Count == 1)
                        EndLists();
                    else
                        lists.RemoveAt(lists.Count - 1);
                }
                if (lists.Count == 0 || indent > lists[^1].Indent + 1)
                {
                    var list = new List
                    {
                        MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, lists.Count == 0 ? 2 : 0, 0, lists.Count == 0 ? 6 : 0),
                        Padding = new Thickness(lists.Count == 0 ? 22 : 20, 0, 0, 0),
                    };
                    if (numbered && int.TryParse(marker[..^1], out int start) && start > 1)
                        list.StartIndex = start;
                    if (lists.Count > 0)
                        lists[^1].List.ListItems.LastListItem?.Blocks.Add(list);
                    lists.Add((indent, list));
                }
                var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                AddInlines(p.Inlines, item.Groups[3].Value);
                lists[^1].List.ListItems.Add(new System.Windows.Documents.ListItem(p));
                continue;
            }

            if (lists.Count > 0 && lists[^1].List.ListItems.LastListItem?.Blocks.LastBlock is Paragraph itemText && line.StartsWith(' '))
            {
                // A wrapped line of a list item.
                itemText.Inlines.Add(new LineBreak());
                AddInlines(itemText.Inlines, line.Trim());
                continue;
            }

            EndLists();
            if (paragraph is null)
            {
                paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 6) };
                document.Blocks.Add(paragraph);
            }
            else
            {
                paragraph.Inlines.Add(new LineBreak()); // One line break stays one (as the AI wrote it).
            }
            AddInlines(paragraph.Inlines, line);
        }
        EndLists();
        if (document.Blocks.LastBlock is Paragraph last)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        Document = document;
    }

    private Table MakeTable(List<string> rows)
    {
        static List<string> Cells(string row)
        {
            string trimmed = row.Trim();
            if (trimmed.StartsWith('|'))
                trimmed = trimmed[1..];
            if (trimmed.EndsWith('|'))
                trimmed = trimmed[..^1];
            return [.. trimmed.Split('|').Select(c => c.Trim())];
        }
        var cells = rows.Select(Cells).ToList();
        int columns = cells.Max(c => c.Count);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8), BorderBrush = RuleBrush, BorderThickness = new Thickness(1, 1, 0, 0) };
        for (int c = 0; c < columns; c++)
            table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        for (int r = 0; r < cells.Count; r++)
        {
            var row = new TableRow();
            if (r == 0)
            {
                row.FontWeight = FontWeights.SemiBold;
                row.Background = CodeBack;
            }
            for (int c = 0; c < columns; c++)
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                AddInlines(p.Inlines, c < cells[r].Count ? cells[r][c] : "");
                row.Cells.Add(new TableCell(p) { Padding = new Thickness(8, 3, 8, 3), BorderBrush = RuleBrush, BorderThickness = new Thickness(0, 0, 1, 1) });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    /// <summary>Text with its inline marks; the parts inside bold, italic and strike can carry marks too.</summary>
    private void AddInlines(InlineCollection target, string text)
    {
        int at = 0;
        foreach (Match m in Inline().Matches(text))
        {
            if (m.Index > at)
                target.Add(new Run(text[at..m.Index]));
            if (m.Groups[2].Success)
            {
                target.Add(new Run(m.Groups[2].Value.Trim()) { FontFamily = Mono, FontSize = FontSize * 0.95, Background = CodeBack });
            }
            else if (m.Groups[3].Success || m.Groups[4].Success)
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value);
                target.Add(bold);
            }
            else if (m.Groups[5].Success)
            {
                var strike = new Span { TextDecorations = TextDecorations.Strikethrough };
                AddInlines(strike.Inlines, m.Groups[5].Value);
                target.Add(strike);
            }
            else if (m.Groups[6].Success)
            {
                var italic = new Italic();
                AddInlines(italic.Inlines, m.Groups[6].Value);
                target.Add(italic);
            }
            else if (m.Groups[7].Success && Uri.TryCreate(m.Groups[8].Value, UriKind.Absolute, out var uri))
            {
                var link = new Hyperlink(new Run(m.Groups[7].Value)) { NavigateUri = uri, Foreground = LinkBrush, ToolTip = uri.AbsoluteUri, Cursor = System.Windows.Input.Cursors.Hand };
                link.Click += (_, _) => OpenLink(uri);
                target.Add(link);
            }
            else
            {
                target.Add(new Run(m.Value));
            }
            at = m.Index + m.Length;
        }
        if (at < text.Length)
            target.Add(new Run(text[at..]));
    }

    /// <summary>A link opens in the default browser (only http and https).</summary>
    private static void OpenLink(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser: nothing happens.
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
