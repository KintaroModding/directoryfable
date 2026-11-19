using System.IO;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace DirectoryFable.Editor;

/// <summary>
/// Syntax highlighting for Notepad#. AvalonEdit's built-in definitions are
/// made for a white background: they are loaded again here from AvalonEdit's own resources,
/// their colors are changed to a dark palette (like VS Code's Dark+) before they are built,
/// and registered under the same names. Own definitions (Editor/Syntax/*.xshd, made for the dark background):
/// AutoHotkey, INI, Batch, YAML, TOML, TypeScript, Go, Rust, Lua, Shell script, Ruby, Kotlin, Swift, Dockerfile,
/// Makefile, Registry, Shader (GLSL / HLSL) and Log; and Technical Notes (its own highlighter, see
/// <see cref="TechnicalNotes"/>).
/// </summary>
public static class EditorHighlighting
{
    private static bool _ready;

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
        var manager = HighlightingManager.Instance;
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
                Darken(xshd);
                manager.RegisterHighlighting(name, extensions, HighlightingLoader.Load(xshd, manager));
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
                manager.RegisterHighlighting(xshd.Name, [.. xshd.Extensions], HighlightingLoader.Load(xshd, manager));
            }
            catch (Exception ex) when (ex is XmlException or HighlightingDefinitionInvalidException or ArgumentException)
            {
                // A broken definition: that language is left out, the others load.
            }
        }

        manager.RegisterHighlighting(TechnicalNotes.Name, TechnicalNotes.Extensions, TechnicalNotesDefinition.Instance);
    }

    /// <summary>The languages of the Language menu, sorted by name.</summary>
    public static IReadOnlyList<IHighlightingDefinition> Languages
    {
        get
        {
            EnsureReady();
            return [.. HighlightingManager.Instance.HighlightingDefinitions
                .Where(d => !Hidden.Contains(d.Name))
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

    private static readonly Color CommentColor = Color.FromRgb(0x6A, 0x99, 0x55);
    private static readonly Color StringColor = Color.FromRgb(0xCE, 0x91, 0x78);
    private static readonly Color NumberColor = Color.FromRgb(0xB5, 0xCE, 0xA8);
    private static readonly Color KeywordColor = Color.FromRgb(0x56, 0x9C, 0xD6);
    private static readonly Color ControlColor = Color.FromRgb(0xC5, 0x86, 0xC0);
    private static readonly Color MethodColor = Color.FromRgb(0xDC, 0xDC, 0xAA);
    private static readonly Color TypeColor = Color.FromRgb(0x4E, 0xC9, 0xB0);
    private static readonly Color AttributeColor = Color.FromRgb(0x9C, 0xDC, 0xFE);
    private static readonly Color PunctuationColor = Color.FromRgb(0xD4, 0xD4, 0xD4);

    /// <summary>Gives every named color a foreground that reads well on the dark editor background.</summary>
    private static void Darken(XshdSyntaxDefinition xshd)
    {
        foreach (var color in xshd.Elements.OfType<XshdColor>())
        {
            color.Background = null; // Light backgrounds (e.g. Patch) would glare.
            Color? mapped = ByColorName(color.Name ?? "");
            if (mapped is null && color.Foreground?.GetColor(null) is Color original)
                mapped = Lighten(original);
            if (mapped is { } c)
                color.Foreground = new SimpleHighlightingBrush(c);
        }
    }

    private static Color? ByColorName(string name)
    {
        bool Has(params string[] parts) => parts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));

        if (Has("Comment", "XmlDoc", "DocComment"))
            return CommentColor;
        if (Has("String", "Char", "Regex", "AttributeValue", "XmlString"))
            return StringColor;
        if (Has("Number", "Digit"))
            return NumberColor;
        if (Has("Preprocessor", "Directive", "Pragma", "Jump", "Goto", "Iteration", "Selection", "Exception", "Return"))
            return ControlColor;
        if (Has("Method", "Function"))
            return MethodColor;
        if (Has("ReferenceType", "ValueType", "TypeKeyword", "Class"))
            return TypeColor;
        if (Has("AttributeName", "Property", "Variable", "Attribute"))
            return AttributeColor;
        if (Has("Punctuation", "Operator", "Slash"))
            return name.Contains("Keyword", StringComparison.OrdinalIgnoreCase) ? KeywordColor : PunctuationColor;
        if (Has("Keyword", "Modifier", "Visibility", "Access", "TrueFalse", "Null", "This", "Void", "Tag", "Context",
                "Semantic", "Unsafe", "Checked", "Namespace", "GetSet", "Parameter"))
            return KeywordColor;
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
