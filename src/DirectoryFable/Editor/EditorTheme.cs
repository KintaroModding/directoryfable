using System.Windows.Media;

namespace DirectoryFable.Editor;

/// <summary>The colors of the syntax highlighting (what a keyword, a string, a comment ... gets).</summary>
public sealed record SyntaxPalette(
    Color Comment, Color String, Color Number, Color Control, Color Method, Color Type, Color Attribute,
    Color Keyword, Color Namespace, Color Punctuation);

/// <summary>
/// A look for the inside of a file in Notepad#: the editor's background, text, line numbers, current line, the file
/// list on the left (a shade darker than the editor, so the two belong together) and the
/// colors of the syntax highlighting. "Default" is the look Notepad# always had; "VS Code" follows VS Code's dark theme.
/// </summary>
public sealed record EditorTheme(
    string Id, string Name, Color Background, Color Text, Color LineNumber, Color CurrentLine, Color Selection, Color Minimap,
    Color FileList,
    SyntaxPalette Palette, bool RecolorOwnLanguages)
{
    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>The look Notepad# had from the start (Dark+ colors on a #1A1A1A background).</summary>
    public static readonly EditorTheme Default = new("default", "Default",
        Background: Rgb(0x1A1A1A), Text: Rgb(0xD4D4D4), LineNumber: Rgb(0x6E6E6E), CurrentLine: Rgb(0x242424),
        Selection: Rgb(0x264F78), Minimap: Rgb(0x1A1A1A), FileList: Rgb(0x181818),
        Palette: new SyntaxPalette(
            Comment: Rgb(0x6A9955), String: Rgb(0xCE9178), Number: Rgb(0xB5CEA8), Control: Rgb(0xC586C0), Method: Rgb(0xDCDCAA),
            Type: Rgb(0x4EC9B0), Attribute: Rgb(0x9CDCFE), Keyword: Rgb(0x569CD6), Namespace: Rgb(0x569CD6), Punctuation: Rgb(0xD4D4D4)),
        RecolorOwnLanguages: false);

    /// <summary>VS Code's dark look: a near-black blue-gray background, gray comments, light blue strings, purple methods.</summary>
    public static readonly EditorTheme VsCode = new("vscode", "VS Code",
        Background: Rgb(0x121314), Text: Rgb(0xC9D1D9), LineNumber: Rgb(0x858889), CurrentLine: Rgb(0x242526),
        Selection: Rgb(0x264F78), Minimap: Rgb(0x121314), FileList: Rgb(0x0E0F10),
        Palette: new SyntaxPalette(
            Comment: Rgb(0x8B949E), String: Rgb(0xA5D6FF), Number: Rgb(0x79C0FF), Control: Rgb(0xC586C0), Method: Rgb(0xD2A8FF),
            Type: Rgb(0xFF7B72), Attribute: Rgb(0x9CDCFE), Keyword: Rgb(0x569CD6), Namespace: Rgb(0xC586C0), Punctuation: Rgb(0xC9D1D9)),
        RecolorOwnLanguages: true);

    /// <summary>
    /// DirectoryFable's own dark look: a deep blue-black background with soft contrast for long sessions; cool blue
    /// keywords and methods, warm strings and numbers, so the two kinds stand apart at a glance.
    /// </summary>
    public static readonly EditorTheme Midnight = new("midnight", "Midnight",
        Background: Rgb(0x14161C), Text: Rgb(0xD0D6E0), LineNumber: Rgb(0x4F5866), CurrentLine: Rgb(0x1D2029),
        Selection: Rgb(0x2B3D5C), Minimap: Rgb(0x14161C), FileList: Rgb(0x101217),
        Palette: new SyntaxPalette(
            Comment: Rgb(0x6C7889), String: Rgb(0xA3D58C), Number: Rgb(0xF2A96B), Control: Rgb(0xC792EA), Method: Rgb(0x7DB9FF),
            Type: Rgb(0xF2CC7C), Attribute: Rgb(0xF08A8A), Keyword: Rgb(0x5FC4D6), Namespace: Rgb(0xC792EA), Punctuation: Rgb(0xA9B2BF)),
        RecolorOwnLanguages: true);

    /// <summary>The themes of the Theme menu, in its order (Default first).</summary>
    public static IReadOnlyList<EditorTheme> All { get; } = [Default, VsCode, Midnight];

    /// <summary>A theme by its id ("default", "vscode"); an unknown id gives the default.</summary>
    public static EditorTheme ById(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;
}
