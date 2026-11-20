using System.IO;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace DirectoryFable.Editor;

/// <summary>
/// Syntax highlighting for Notepad#. AvalonEdit's built-in definitions are
/// made for a white background: they are loaded again here from AvalonEdit's own resources,
/// their colors are changed to the dark palette of the chosen <see cref="EditorTheme"/> (Default: like VS Code's
/// Dark+) before they are built, and registered under the same names. A theme switch builds them again. Own definitions (Editor/Syntax/*.xshd, made for the dark background):
/// AutoHotkey, INI, Batch, YAML, TOML, TypeScript, Go, Rust, Lua, Shell script, Ruby, Kotlin, Swift, Dockerfile,
/// Makefile, Registry, Shader (GLSL / HLSL) and Log; and Technical Notes (its own highlighter, see
/// <see cref="TechnicalNotes"/>).
/// </summary>
public static class EditorHighlighting
{
    private static bool _ready;

    /// <summary>The names of the registered languages (AvalonEdit's manager lists a definition again each time it is registered).</summary>
    private static readonly List<string> Names = [];

    /// <summary>The theme the definitions are built with.</summary>
    public static EditorTheme Theme { get; private set; } = EditorTheme.Default;

    /// <summary>
    /// Chooses the theme. Before the first file opens this only remembers it; afterwards the definitions are built again
    /// (the caller gives its files the new definitions, see <see cref="ByName"/>).
    /// </summary>
    public static void UseTheme(EditorTheme theme)
    {
        if (theme == Theme)
            return;
        Theme = theme;
        if (_ready)
            Build();
    }

    /// <summary>Built-in definitions: name, extensions, resource file (from AvalonEdit's Resources.cs).</summary>
    private static readonly (string Name, string[] Extensions, string File)[] BuiltIn =
    [
        ("XmlDoc", [], "XmlDoc.xshd"), // Used by C# (doc comments): load first.
        ("C#", [".cs", ".csx"], "CSharp-Mode.xshd"),
        ("JavaScript", [".js", ".mjs", ".cjs", ".jsx"], "JavaScript-Mode.xshd"),
        ("HTML", [".htm", ".html", ".xhtml", ".vue"], "HTML-Mode.xshd"),
        ("ASP/XHTML", [".asp", ".aspx", ".asax", ".asmx", ".ascx", ".master"], "ASPX.xshd"),
        ("Boo", [".boo"], "Boo.xshd"),
        ("Coco", [".atg"], "Coco-Mode.xshd"),
        ("CSS", [".css", ".scss", ".less"], "CSS-Mode.xshd"),
        ("C++", [".c", ".h", ".cc", ".cpp", ".hpp", ".cxx", ".hxx", ".hh", ".inl", ".ino"], "CPP-Mode.xshd"),
        ("Java", [".java"], "Java-Mode.xshd"),
        ("Patch", [".patch", ".diff"], "Patch-Mode.xshd"),
        ("PowerShell", [".ps1", ".psm1", ".psd1"], "PowerShell.xshd"),
        ("PHP", [".php"], "PHP-Mode.xshd"),
        ("Python", [".py", ".pyw", ".pyi"], "Python-Mode.xshd"),
        ("TeX", [".tex"], "Tex-Mode.xshd"),
        ("TSQL", [".sql"], "TSQL-Mode.xshd"),
        ("VB", [".vb"], "VB-Mode.xshd"),
        ("XML", [".xml", ".xsl", ".xslt", ".xsd", ".manifest", ".config", ".xshd", ".proj", ".csproj", ".vbproj",
                 ".props", ".targets", ".xaml", ".axaml", ".resx", ".nuspec", ".ps1xml", ".svg", ".plist", ".wxs", ".vcxproj",
                 ".fsproj", ".kml", ".gpx", ".rss"], "XML-Mode.xshd"),
        ("MarkDown", [".md", ".markdown"], "MarkDown-Mode.xshd"),
        ("Json", [".json", ".jsonc", ".jsonl", ".json5", ".geojson", ".webmanifest", ".code-workspace", ".uplugin", ".uproject"], "Json.xshd"),
    ];

    /// <summary>Not offered in the Language menu (helpers of other definitions).</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "XmlDoc", "MarkDownWithFontSize" };

    /// <summary>Registers all definitions once (UI thread, before the first file opens).</summary>
    public static void EnsureReady()
    {
        if (_ready)
            return;
        _ready = true;
        Build();
    }

    private static void Register(HighlightingManager manager, string name, string[] extensions, IHighlightingDefinition definition)
    {
        manager.RegisterHighlighting(name, extensions, definition);
        if (!Names.Contains(name))
            Names.Add(name);
    }

    /// <summary>Loads and registers every definition with the colors of <see cref="Theme"/>.</summary>
    private static void Build()
    {
        var manager = HighlightingManager.Instance;
        var palette = Theme.Palette;
        var avalon = typeof(HighlightingManager).Assembly;

        foreach (var (name, extensions, file) in BuiltIn)
        {
            try
            {
                using var stream = avalon.GetManifestResourceStream("ICSharpCode.AvalonEdit.Highlighting.Resources." + file);
                if (stream is null)
                    continue;
                using var reader = XmlReader.Create(stream);
                var xshd = HighlightingLoader.LoadXshd(reader);
                Darken(xshd, palette);
                Register(manager, name, extensions, HighlightingLoader.Load(xshd, manager));
            }
            catch (Exception ex) when (ex is XmlException or HighlightingDefinitionInvalidException or IOException)
            {
                // Keep AvalonEdit's own (light) definition.
            }
        }

        var own = typeof(EditorHighlighting).Assembly;
        foreach (string resource in own.GetManifestResourceNames().Where(r => r.EndsWith(".xshd", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var stream = own.GetManifestResourceStream(resource)!;
                using var reader = XmlReader.Create(stream);
                var xshd = HighlightingLoader.LoadXshd(reader);
                // Made for the dark background already: only a theme that has its own colors changes them.
                if (Theme.RecolorOwnLanguages)
                    Darken(xshd, palette);
                Register(manager, xshd.Name, [.. xshd.Extensions], HighlightingLoader.Load(xshd, manager));
            }
            catch (Exception ex) when (ex is XmlException or HighlightingDefinitionInvalidException or ArgumentException)
            {
                // A broken definition: that language is left out, the others load.
            }
        }

        Register(manager, TechnicalNotes.Name, TechnicalNotes.Extensions, TechnicalNotesDefinition.Instance);
    }

    /// <summary>The languages of the Language menu, sorted by name.</summary>
    public static IReadOnlyList<IHighlightingDefinition> Languages
    {
        get
        {
            EnsureReady();
            return [.. Names.Where(n => !Hidden.Contains(n))
                .Select(n => HighlightingManager.Instance.GetDefinition(n))
                .OfType<IHighlightingDefinition>()
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    public static IHighlightingDefinition? ByName(string? name)
    {
        EnsureReady();
        return string.IsNullOrEmpty(name) ? null : HighlightingManager.Instance.GetDefinition(name);
    }

    public static IHighlightingDefinition? ByExtension(string path)
    {
        EnsureReady();
        string extension = Path.GetExtension(path);
        return extension.Length == 0 ? null : HighlightingManager.Instance.GetDefinitionByExtension(extension);
    }

    // ---------- Dark palette ----------

    /// <summary>Gives every named color a foreground that reads well on the dark editor background.</summary>
    private static void Darken(XshdSyntaxDefinition xshd, SyntaxPalette palette)
    {
        foreach (var color in xshd.Elements.OfType<XshdColor>())
        {
            color.Background = null; // Light backgrounds (e.g. Patch) would glare.
            Color? mapped = ByColorName(color.Name ?? "", palette);
            if (mapped is null && color.Foreground?.GetColor(null) is Color original)
                mapped = Lighten(original);
            if (mapped is { } c)
                color.Foreground = new SimpleHighlightingBrush(c);
        }
    }

    private static Color? ByColorName(string name, SyntaxPalette p)
    {
        bool Has(params string[] parts) => parts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));

        if (Has("Comment", "XmlDoc", "DocComment"))
            return p.Comment;
        if (Has("String", "Char", "Regex", "AttributeValue", "XmlString"))
            return p.String;
        if (Has("Number", "Digit"))
            return p.Number;
        if (Has("Preprocessor", "Directive", "Pragma", "Jump", "Goto", "Iteration", "Selection", "Exception", "Return"))
            return p.Control;
        if (Has("Method", "Function"))
            return p.Method;
        if (Has("ReferenceType", "ValueType", "TypeKeyword", "Class"))
            return p.Type;
        if (Has("AttributeName", "Property", "Variable", "Attribute"))
            return p.Attribute;
        if (Has("Punctuation", "Operator", "Slash"))
            return name.Contains("Keyword", StringComparison.OrdinalIgnoreCase) ? p.Keyword : p.Punctuation;
        if (Has("Namespace"))
            return p.Namespace; // using, namespace.
        if (Has("Keyword", "Modifier", "Visibility", "Access", "TrueFalse", "Null", "This", "Void", "Tag", "Context",
                "Semantic", "Unsafe", "Checked", "GetSet", "Parameter"))
            return p.Keyword;
        return null;
    }

    /// <summary>A dark color (made for white) gets the same hue, but light enough for a dark background.</summary>
    private static Color Lighten(Color c)
    {
        double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        if (luminance >= 0.5)
            return c;
        // Mix with white until it is light enough.
        double t = Math.Min(1, (0.62 - luminance) / (1 - luminance));
        return Color.FromRgb(Mix(c.R, t), Mix(c.G, t), Mix(c.B, t));
    }

    private static byte Mix(byte value, double t) => (byte)Math.Round(value + (255 - value) * t);
}
