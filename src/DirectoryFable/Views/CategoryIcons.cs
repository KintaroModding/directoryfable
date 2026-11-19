namespace DirectoryFable.Views;

/// <summary>
/// The icons a KeeSeek category can have (glyphs of the icon font, Segoe Fluent Icons / MDL2), and the one a
/// category gets by its name until the user picks one.
/// </summary>
public static class CategoryIcons
{
    public const string AllEntries = "";
    public const string Default = ""; // A tag.

    /// <summary>Glyph and name, in the order the picker shows them.</summary>
    public static readonly (string Glyph, string Name)[] All =
    [
        ("", "Tag"), ("", "Mail"), ("", "Web"), ("", "Bank"), ("", "Card"),
        ("", "Shopping"), ("", "Games"), ("", "Work"), ("", "Home"), ("", "Cloud"),
        ("", "Phone"), ("", "Wi-Fi"), ("", "Network"), ("", "Code"), ("", "People"),
        ("", "Chat"), ("", "Music"), ("", "Video"), ("", "Photos"), ("", "Document"),
        ("", "Folder"), ("", "School"), ("", "Car"), ("", "Travel"), ("", "Calendar"),
        ("", "Health"), ("", "Favorite"), ("", "Flag"), ("", "Key"), ("", "Lock"),
        ("", "Settings"), ("", "Contact"),
    ];

    /// <summary>The name of a glyph ("Mail"), for the picker's "Automatic" button.</summary>
    public static string Name(string glyph) => All.FirstOrDefault(i => i.Glyph == glyph).Name ?? "Tag";

    /// <summary>The user's icon, else one that fits the name ("Email" -> mail), else a tag.</summary>
    public static string For(string? chosen, string name)
    {
        if (!string.IsNullOrEmpty(chosen))
            return chosen;
        string n = name.ToLowerInvariant();
        (string[] Words, string Glyph)[] guesses =
        [
            (["mail", "e-mail", "email"], ""), (["web", "internet", "site"], ""),
            (["bank", "finance", "money"], ""), (["card", "credit", "pay"], ""),
            (["shop", "store", "amazon"], ""), (["game", "steam", "gaming"], ""),
            (["work", "job", "office", "business"], ""), (["home", "house"], ""),
            (["cloud"], ""), (["phone", "mobile"], ""), (["wifi", "wi-fi", "wlan", "router"], ""),
            (["network", "server", "ftp", "ssh"], ""), (["dev", "code", "git"], ""),
            (["social", "people", "friends", "family"], ""), (["chat", "messenger", "discord"], ""),
            (["music"], ""), (["video", "stream", "tv"], ""), (["school", "uni", "study"], ""),
            (["travel", "flight"], ""), (["health", "doctor"], ""), (["backup", "imported"], ""),
        ];
        foreach (var (words, glyph) in guesses)
            if (words.Any(n.Contains))
                return glyph;
        return Default;
    }
}
