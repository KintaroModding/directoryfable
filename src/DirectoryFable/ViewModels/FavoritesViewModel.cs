using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using DirectoryFable.Infrastructure;
using DirectoryFable.Models;
using DirectoryFable.Shell;

namespace DirectoryFable.ViewModels;

/// <summary>
/// The favorites bar. Stored in <c>favorites.json</c> next to settings.json; the app
/// writes this file itself after each change (settings.json is never written).
/// </summary>
public sealed class FavoritesViewModel
{
    private const string FileName = "favorites.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public ObservableCollection<Favorite> Items { get; } = [];

    /// <summary>The app's one favorites list (for "Add to favorites" in tab, list and tree menus).</summary>
    public static FavoritesViewModel? Current { get; set; }

    /// <summary>Menu text for "Add to favorites": says when the folder already is one.</summary>
    public static (string Header, bool Enabled) AddMenuText(string folder, string label = "Add to favorites") =>
        Current?.Contains(folder) == true
            ? ($"\"{Favorite.NameFor(folder)}\" is already a favorite", false)
            : (label, Current is not null);

    private static string FilePath => Path.Combine(AppSettings.DataFolder, FileName);

    public void Load()
    {
        List<Favorite>? loaded = null;
        try
        {
            if (File.Exists(FilePath))
                loaded = JsonSerializer.Deserialize<List<Favorite>>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable file: start with the defaults, but do not overwrite the broken file.
            loaded = null;
        }

        bool firstRun = loaded is null && !File.Exists(FilePath);
        loaded ??= firstRun ? Defaults() : [];

        Items.Clear();
        foreach (var favorite in loaded.Where(f => !string.IsNullOrWhiteSpace(f.Path)))
        {
            favorite.Icon = ShellInfo.GetFolderIcon(favorite.FullPath);
            Items.Add(favorite);
        }

        if (firstRun)
            Save();
    }

    public bool Contains(string folder) =>
        Items.Any(f => string.Equals(f.FullPath.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    public void Add(string folder, int index = -1)
    {
        if (Contains(folder))
            return; // Already there.

        var favorite = new Favorite { Name = Favorite.NameFor(folder), Path = folder };
        favorite.Icon = ShellInfo.GetFolderIcon(favorite.FullPath);
        Items.Insert(index < 0 || index > Items.Count ? Items.Count : index, favorite);
        Save();
    }

    public void Remove(Favorite favorite)
    {
        if (Items.Remove(favorite))
            Save();
    }

    public void Rename(Favorite favorite, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName == favorite.Name)
            return;
        favorite.Name = newName;
        Save();
    }

    /// <summary>Moves a favorite (drag to reorder). Pass save: false while the drag is still running.</summary>
    public void Move(Favorite favorite, int newIndex, bool save = true)
    {
        int oldIndex = Items.IndexOf(favorite);
        newIndex = Math.Clamp(newIndex, 0, Items.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex)
            return;
        Items.Move(oldIndex, newIndex);
        if (save)
            Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the bar still works for this session.
        }
    }

    private static List<Favorite> Defaults()
    {
        static Favorite F(string name, Environment.SpecialFolder folder) =>
            new() { Name = name, Path = Environment.GetFolderPath(folder) };

        return
        [
            F("Desktop", Environment.SpecialFolder.DesktopDirectory),
            F("Documents", Environment.SpecialFolder.MyDocuments),
            new Favorite { Name = "Downloads", Path = KnownFolders.Downloads },
            F("Home", Environment.SpecialFolder.UserProfile),
        ];
    }
}
