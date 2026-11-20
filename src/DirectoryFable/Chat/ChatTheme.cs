using System.Windows.Media;

namespace DirectoryFable.Chat;

/// <summary>
/// A look for GemmaChat's conversation: the background behind the messages, the chat list on the left, the AI's and
/// the user's bubbles, code blocks and the message text. "Default" is the look GemmaChat always had (white text on
/// dark gray); the others soften the contrast for long answers.
/// </summary>
public sealed record ChatTheme(
    string Id, string Name, Color Background, Color Side, Color Bubble, Color UserBubble, Color Code, Color Text)
{
    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>The look GemmaChat had from the start: white text on #202020 bubbles.</summary>
    public static readonly ChatTheme Default = new("default", "Default",
        Background: Rgb(0x191919), Side: Rgb(0x161616), Bubble: Rgb(0x202020), UserBubble: Rgb(0x163A5C),
        Code: Rgb(0x121212), Text: Rgb(0xFFFFFF));

    /// <summary>The same grays with a soft light gray text: much less glare in long answers.</summary>
    public static readonly ChatTheme Soft = new("soft", "Soft",
        Background: Rgb(0x1C1C1C), Side: Rgb(0x171717), Bubble: Rgb(0x242424), UserBubble: Rgb(0x1E3850),
        Code: Rgb(0x161616), Text: Rgb(0xCCCCCC));

    /// <summary>Near-black surfaces with a subtle cool tint and muted gray text for low-glare reading.</summary>
    public static readonly ChatTheme Midnight = new("midnight", "Midnight",
        Background: Rgb(0x101113), Side: Rgb(0x0D0E10), Bubble: Rgb(0x17191D), UserBubble: Rgb(0x202B3A),
        Code: Rgb(0x0D0F12), Text: Rgb(0xB8BDC5));

    /// <summary>Warm brown-black with a cream text, like old paper at night: easy on the eyes for reading.</summary>
    public static readonly ChatTheme Warm = new("warm", "Warm",
        Background: Rgb(0x1B1916), Side: Rgb(0x161411), Bubble: Rgb(0x26221D), UserBubble: Rgb(0x3A3024),
        Code: Rgb(0x14120F), Text: Rgb(0xDDD3C2));

    /// <summary>Muted sage-gray surfaces and subdued green-gray text for gentler contrast in long answers.</summary>
    public static readonly ChatTheme Sage = new("sage", "Sage",
        Background: Rgb(0x292F2B), Side: Rgb(0x242925), Bubble: Rgb(0x303832), UserBubble: Rgb(0x3B453B),
        Code: Rgb(0x252B27), Text: Rgb(0xB8C2B5));

    /// <summary>Soft mauve-gray surfaces and muted warm-gray text for comfortable evening reading.</summary>
    public static readonly ChatTheme Dusk = new("dusk", "Dusk",
        Background: Rgb(0x302B2D), Side: Rgb(0x292527), Bubble: Rgb(0x383234), UserBubble: Rgb(0x453C40),
        Code: Rgb(0x2B2729), Text: Rgb(0xC5BAB0));

    /// <summary>The themes of the Theme menu, in its order (Default first).</summary>
    public static IReadOnlyList<ChatTheme> All { get; } = [Default, Soft, Midnight, Warm, Sage, Dusk];

    /// <summary>A theme by its id ("default", "soft"); an unknown id gives the default.</summary>
    public static ChatTheme ById(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;
}
