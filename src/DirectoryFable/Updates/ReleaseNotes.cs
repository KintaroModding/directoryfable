using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DirectoryFable.Updates;

/// <summary>
/// Shows release notes as GitHub shows them, for the parts release notes use: lines, lists ("- " or "* ", two
/// spaces more per level), headings ("#" lines, bold), **bold**, `code` and [links](https://...). Anything else
/// stays as written. A link to an address that is not http or https shows as text only.
/// </summary>
internal static partial class ReleaseNotes
{
    private sealed record Span(string Text, bool Bold = false, bool Code = false, string? Url = null);

    private sealed record Line(int Level, bool Gap, IReadOnlyList<Span> Spans);

    /// <summary>Adds the notes to <paramref name="panel"/>, one element per paragraph or list item.</summary>
    /// <param name="openLink">Runs when a link is clicked, with its address.</param>
    public static void AddTo(Panel panel, string? notes, Brush strongBrush, Action<string> openLink)
    {
        foreach (var line in Parse(notes))
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            foreach (var span in line.Spans)
                text.Inlines.Add(Piece(span, strongBrush, openLink));
            var margin = new Thickness(0, line.Gap ? 10 : 2, 0, 0);
            if (line.Level == 0)
            {
                text.Margin = margin;
                panel.Children.Add(text);
                continue;
            }
            var bullet = new TextBlock { Width = 14, LineHeight = 18, Text = line.Level == 1 ? "•" : "◦" };
            DockPanel.SetDock(bullet, Dock.Left);
            panel.Children.Add(new DockPanel { Margin = margin with { Left = (line.Level - 1) * 16 }, Children = { bullet, text } });
        }
    }

    private static Inline Piece(Span span, Brush strongBrush, Action<string> openLink)
    {
        var run = new Run(span.Text);
        if (span.Code)
        {
            run.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            run.FontSize = 11.5;
        }
        Inline piece = run;
        if (span.Url is { } url)
        {
            var link = new Hyperlink(run) { ToolTip = url, Foreground = strongBrush };
            link.Click += (_, _) => openLink(url);
            piece = link;
        }
        if (span.Bold)
        {
            piece.FontWeight = FontWeights.SemiBold;
            piece.Foreground = strongBrush;
        }
        return piece;
    }

    private static List<Line> Parse(string? notes)
    {
        var lines = new List<Line>();
        bool gap = false;
        foreach (string raw in (notes ?? "").Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                gap = lines.Count > 0;
                continue;
            }
            int indent = raw.Length - raw.TrimStart(' ').Length;
            string text = raw.Trim();
            int level = 0;
            if (text.Length > 2 && text[0] is '-' or '*' && text[1] == ' ')
            {
                level = 1 + indent / 2;
                text = text[2..].TrimStart();
            }
            else if (Heading().Match(text) is { Success: true } heading)
            {
                lines.Add(new Line(0, gap, [new Span(heading.Groups["text"].Value, Bold: true)]));
                gap = false;
                continue;
            }
            lines.Add(new Line(level, gap, Spans(text, bold: false)));
            gap = false;
        }
        return lines;
    }

    /// <summary>The pieces of a line; inside bold text, code and links are still found.</summary>
    private static List<Span> Spans(string text, bool bold)
    {
        var spans = new List<Span>();
        int plain = 0;
        void Plain(int end)
        {
            if (end > plain)
                spans.Add(new Span(text[plain..end], bold));
        }

        int i = 0;
        while (i < text.Length)
        {
            if (!bold && text.AsSpan(i).StartsWith("**") && text.IndexOf("**", i + 2, StringComparison.Ordinal) is var close and > 0
                && close > i + 2)
            {
                Plain(i);
                spans.AddRange(Spans(text[(i + 2)..close], bold: true));
                i = plain = close + 2;
            }
            else if (text[i] == '`' && text.IndexOf('`', i + 1) is var end and > 0 && end > i + 1)
            {
                Plain(i);
                spans.Add(new Span(text[(i + 1)..end], bold, Code: true));
                i = plain = end + 1;
            }
            else if (text[i] == '[' && Link().Match(text, i) is { Success: true } link && link.Index == i)
            {
                Plain(i);
                string url = link.Groups["url"].Value;
                bool web = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
                spans.Add(new Span(link.Groups["text"].Value, bold, Url: web ? url : null));
                i = plain = i + link.Length;
            }
            else
            {
                i++;
            }
        }
        Plain(text.Length);
        return spans;
    }

    [GeneratedRegex(@"^#{1,6}\s+(?<text>.+?)\s*#*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\G\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)\)")]
    private static partial Regex Link();
}
