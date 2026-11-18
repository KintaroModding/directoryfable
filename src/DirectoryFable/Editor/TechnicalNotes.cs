using System.IO;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace DirectoryFable.Editor;

/// <summary>
/// "Technical Notes": highlighting for plain-text notes and logs (dates, status words, paths, file names,
/// dotted members, brackets, quotes, numbers; capitalized words always stand out). The rules come unchanged
/// from the Technical Notes VS Code language mode (Syntax/TechnicalNotes.tmLanguage.json, read by
/// <see cref="TextMateGrammar"/>); the colors are those VS Code's default dark theme gives its scopes, with
/// the language mode's own colors for dates (#6F9FC4) and capitalized words (#D47726), and brackets colored
/// by depth like VS Code's bracket pair colorization.
/// AvalonEdit's rule engine cannot color words inside a match differently (a capitalized word inside a path),
/// so this language has its own highlighter, used by the editor and the minimap.
/// </summary>
internal static class TechnicalNotes
{
    public const string Name = "Technical Notes";

    public static readonly string[] Extensions = [".tnotes"];

    private static readonly Lazy<TextMateGrammar?> Grammar = new(LoadGrammar);

    private static TextMateGrammar? LoadGrammar()
    {
        try
        {
            using var stream = typeof(TechnicalNotes).Assembly.GetManifestResourceStream("DirectoryFable.Editor.Syntax.TechnicalNotes.tmLanguage.json");
            if (stream is null)
                return null;
            using var reader = new StreamReader(stream);
            return TextMateGrammar.Load(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or IOException)
        {
            return null; // The file shows as plain text.
        }
    }

    // ---------- Colors ----------

    /// <summary>
    /// Scope selector -> color / style, as VS Code's default dark theme (Dark+) has them for the scopes the
    /// grammar uses; the two Technical Notes colors come last (more specific selectors win anyway).
    /// </summary>
    private static readonly (string Selector, string? Color, bool Bold, bool Underline)[] Theme =
    [
        ("comment", "#6A9955", false, false),
        ("constant.language", "#569CD6", false, false),
        ("constant.numeric", "#B5CEA8", false, false),
        ("constant.character", "#569CD6", false, false),
        ("constant.character.escape", "#D7BA7D", false, false),
        ("constant.regexp", "#646695", false, false),
        ("entity.name.tag", "#569CD6", false, false),
        ("entity.other.attribute-name", "#9CDCFE", false, false),
        ("entity.name.function", "#DCDCAA", false, false),
        ("entity.name.type", "#4EC9B0", false, false),
        ("entity.name.class", "#4EC9B0", false, false),
        ("entity.name.namespace", "#4EC9B0", false, false),
        ("support.function", "#DCDCAA", false, false),
        ("support.class", "#4EC9B0", false, false),
        ("support.type", "#4EC9B0", false, false),
        ("invalid", "#F44747", false, false),
        ("markup.underline", null, false, true),
        ("markup.bold", "#569CD6", true, false),
        ("markup.heading", "#569CD6", true, false),
        ("markup.inserted", "#B5CEA8", false, false),
        ("markup.deleted", "#CE9178", false, false),
        ("markup.changed", "#569CD6", false, false),
        ("markup.inline.raw", "#CE9178", false, false),
        ("punctuation.definition.tag", "#808080", false, false),
        ("storage", "#569CD6", false, false),
        ("string", "#CE9178", false, false),
        ("string.regexp", "#D16969", false, false),
        ("keyword", "#569CD6", false, false),
        ("keyword.control", "#C586C0", false, false),
        ("keyword.operator", "#D4D4D4", false, false),
        ("keyword.other.unit", "#B5CEA8", false, false),
        ("variable", "#9CDCFE", false, false),
        ("variable.language", "#569CD6", false, false),
        ("variable.other.constant", "#4FC1FF", false, false),
        // Technical Notes' own colors (its README: dates and times; capitalized and ALL-CAPS words).
        ("constant.other.timestamp", "#6F9FC4", false, false),
        ("keyword.control.capitalized", "#D47726", false, false),
    ];

    /// <summary>VS Code's bracket pair colors by depth, and the color of a closing bracket without an opening one.</summary>
    private static readonly Color[] BracketColors = [Rgb("#FFD700"), Rgb("#DA70D6"), Rgb("#179FFF")];
    private static readonly Color UnexpectedBracket = Rgb("#E51A1A");

    private static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private readonly record struct Style(Color? Foreground, bool Bold, bool Underline)
    {
        public bool IsPlain => Foreground is null && !Bold && !Underline;
    }

    private static readonly Dictionary<Scope, Style> StyleCache = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<Style, HighlightingColor> ColorCache = [];

    /// <summary>
    /// Foreground and font style of a scope stack: from the innermost scope outwards, the first scope that a
    /// theme selector matches (the longest selector wins); color and style are looked up separately.
    /// </summary>
    private static Style StyleOf(Scope scope)
    {
        if (StyleCache.TryGetValue(scope, out var cached))
            return cached;
        Color? foreground = null;
        bool? bold = null, underline = null;
        foreach (string name in scope.Names())
        {
            (string Selector, string? Color, bool Bold, bool Underline)? best = null;
            foreach (var rule in Theme)
                if (Matches(rule.Selector, name) && (best is null || rule.Selector.Length > best.Value.Selector.Length))
                    best = rule;
            if (best is not { } found)
                continue;
            if (foreground is null && found.Color is not null)
                foreground = Rgb(found.Color);
            if (bold is null && found.Bold)
                bold = true;
            if (underline is null && found.Underline)
                underline = true;
            if (foreground is not null)
                break;
        }
        var style = new Style(foreground, bold ?? false, underline ?? false);
        StyleCache[scope] = style;
        return style;
    }

    /// <summary>"keyword.control" matches "keyword.control" and "keyword.control.capitalized.technical-notes".</summary>
    private static bool Matches(string selector, string scope) =>
        scope.StartsWith(selector, StringComparison.Ordinal) && (scope.Length == selector.Length || scope[selector.Length] == '.');

    /// <summary>Brackets in strings are not colored by depth (as in VS Code).</summary>
    private static bool InString(Scope? scope)
    {
        for (var s = scope; s is not null; s = s.Parent)
            if (s.Name.StartsWith("string", StringComparison.Ordinal) && (s.Name.Length == 6 || s.Name[6] == '.'))
                return true;
        return false;
    }

    private static HighlightingColor ColorFor(Style style)
    {
        if (ColorCache.TryGetValue(style, out var color))
            return color;
        color = new HighlightingColor
        {
            Foreground = style.Foreground is { } c ? new SimpleHighlightingBrush(c) : null,
            FontWeight = style.Bold ? FontWeights.Bold : null,
            Underline = style.Underline ? true : null,
        };
        color.Freeze();
        ColorCache[style] = color;
        return color;
    }

    /// <summary>The colored runs of a line: (start, length, color). Longer lines than VS Code's limit stay plain.</summary>
    public static List<(int Start, int Length, HighlightingColor Color)> Runs(string line)
    {
        var runs = new List<(int, int, HighlightingColor)>();
        if (Grammar.Value is not { } grammar || line.Length == 0 || line.Length > 20000)
            return runs;
        var scopes = grammar.Tokenize(line);
        var styles = new Style[line.Length];
        for (int i = 0; i < line.Length; i++)
            styles[i] = scopes[i] is { } scope ? StyleOf(scope) : default;

        // Bracket pair colors by depth (one depth for all three kinds, as in VS Code).
        var open = new Stack<char>();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is not ('(' or ')' or '[' or ']' or '{' or '}') || InString(scopes[i]))
                continue;
            if (c is '(' or '[' or '{')
            {
                styles[i] = styles[i] with { Foreground = BracketColors[open.Count % BracketColors.Length] };
                open.Push(c);
            }
            else if (open.Count > 0 && open.Peek() == (c == ')' ? '(' : c == ']' ? '[' : '{'))
            {
                open.Pop();
                styles[i] = styles[i] with { Foreground = BracketColors[open.Count % BracketColors.Length] };
            }
            else
            {
                styles[i] = styles[i] with { Foreground = UnexpectedBracket };
            }
        }

        for (int start = 0; start < line.Length;)
        {
            int end = start + 1;
            while (end < line.Length && styles[end] == styles[start])
                end++;
            if (!styles[start].IsPlain)
                runs.Add((start, end - start, ColorFor(styles[start])));
            start = end;
        }
        return runs;
    }

    /// <summary>A highlighter for the Technical Notes language (editor and minimap).</summary>
    public static IHighlighter CreateHighlighter(IDocument document) => new Highlighter(document);

    /// <summary>
    /// Runs by line text, shared by all highlighters (UI thread only): the minimap colors every line again after
    /// each change with a new highlighter, so unchanged lines come from here.
    /// </summary>
    private static readonly Dictionary<string, List<(int Start, int Length, HighlightingColor Color)>> RunCache = new(StringComparer.Ordinal);

    private static List<(int Start, int Length, HighlightingColor Color)> CachedRuns(string text)
    {
        if (!RunCache.TryGetValue(text, out var runs))
        {
            runs = Runs(text);
            if (RunCache.Count > 100000)
                RunCache.Clear();
            RunCache[text] = runs;
        }
        return runs;
    }

    /// <summary>The highlighter of the language: each line on its own (the grammar's regions end at the line end).</summary>
    private sealed class Highlighter(IDocument document) : IHighlighter
    {

        public IDocument Document => document;

        public HighlightingColor DefaultTextColor => null!;

        public event HighlightingStateChangedEventHandler HighlightingStateChanged { add { } remove { } }

        public IEnumerable<HighlightingColor> GetColorStack(int lineNumber) => [];

        public HighlightedLine HighlightLine(int lineNumber)
        {
            var line = document.GetLineByNumber(lineNumber);
            var highlighted = new HighlightedLine(document, line);
            string text = document.GetText(line.Offset, line.Length);
            foreach (var (start, length, color) in CachedRuns(text))
                highlighted.Sections.Add(new HighlightedSection { Offset = line.Offset + start, Length = length, Color = color });
            return highlighted;
        }

        public void UpdateHighlightingState(int lineNumber)
        {
        }

        public void BeginHighlighting()
        {
        }

        public void EndHighlighting()
        {
        }

        public HighlightingColor GetNamedColor(string name) => null!;

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// The Language menu's entry for Technical Notes. Its rule set is empty: the editor and the minimap see this
/// definition and use <see cref="TechnicalNotes.CreateHighlighter"/> instead of AvalonEdit's rule engine.
/// </summary>
internal sealed class TechnicalNotesDefinition : IHighlightingDefinition
{
    public static readonly TechnicalNotesDefinition Instance = new();

    public string Name => TechnicalNotes.Name;

    public HighlightingRuleSet MainRuleSet { get; } = new();

    public HighlightingRuleSet GetNamedRuleSet(string name) => null!;

    public HighlightingColor GetNamedColor(string name) => null!;

    public IEnumerable<HighlightingColor> NamedHighlightingColors => [];

    public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
}

/// <summary>Notepad#'s editor: AvalonEdit's TextEditor, with the Technical Notes highlighter for that language.</summary>
public sealed class NotepadTextEditor : TextEditor
{
    protected override IVisualLineTransformer CreateColorizer(IHighlightingDefinition highlightingDefinition) =>
        highlightingDefinition is TechnicalNotesDefinition ? new TechnicalNotesColorizer() : base.CreateColorizer(highlightingDefinition);

    private sealed class TechnicalNotesColorizer : HighlightingColorizer
    {
        protected override IHighlighter CreateHighlighter(TextView textView, TextDocument document) => TechnicalNotes.CreateHighlighter(document);
    }
}
