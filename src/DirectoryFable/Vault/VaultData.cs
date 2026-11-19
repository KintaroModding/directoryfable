namespace DirectoryFable.Vault;

/// <summary>What KeeSeek's vault holds (saved encrypted as JSON, see <see cref="VaultFile"/>).</summary>
public sealed class VaultData
{
    public int Version { get; set; } = 1;

    /// <summary>The categories in the user's order.</summary>
    public List<VaultCategory> Categories { get; set; } = [];

    public List<VaultEntry> Entries { get; set; } = [];

    /// <summary>A new vault starts with a few categories (renamed or deleted as the user likes).</summary>
    public static VaultData NewVault() => new()
    {
        Categories = [new() { Name = "General" }, new() { Name = "Email" }, new() { Name = "Web" }, new() { Name = "Banking" }],
    };
}

public sealed class VaultCategory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>The icon the user picked (a glyph of the icon font); null = one that fits the name.</summary>
    public string? Icon { get; set; }
}

public sealed class VaultEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CategoryId { get; set; } = "";
    public string Title { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>UTC.</summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>UTC; changes when any field changes.</summary>
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    /// <summary>UTC; when the password itself changed last.</summary>
    public DateTime PasswordChanged { get; set; } = DateTime.UtcNow;

    /// <summary>The user's highlight color "#RRGGBB"; null = none.</summary>
    public string? Color { get; set; }

    /// <summary>Shown as a divider: a row with only the title (in its color); not counted, not searched.</summary>
    public bool IsDivider { get; set; }
}
