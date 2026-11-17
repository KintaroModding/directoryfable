using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Models;

/// <summary>One button on the favorites bar: a folder with a display name.</summary>
public sealed class Favorite : ObservableObject
{
    private string _name = "";
    private string _path = "";

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    /// <summary>As stored; may contain environment variables like %USERPROFILE%.</summary>
    public string Path
    {
        get => _path;
        set
        {
            if (SetField(ref _path, value))
                OnPropertyChanged(nameof(FullPath));
        }
    }

    [JsonIgnore]
    public string FullPath => Environment.ExpandEnvironmentVariables(Path);

    [JsonIgnore]
    public ImageSource? Icon { get; set; }

    [JsonIgnore]
    public string ToolTip => FullPath;

    private bool _isDragging;

    /// <summary>True while this favorite is dragged to a new place (shown faded).</summary>
    [JsonIgnore]
    public bool IsDragging
    {
        get => _isDragging;
        set => SetField(ref _isDragging, value);
    }

    /// <summary>Default name for a folder: its own name, or "C:" for a drive root.</summary>
    public static string NameFor(string folder)
    {
        string name = System.IO.Path.GetFileName(folder.TrimEnd('\\'));
        return name.Length > 0 ? name : folder.TrimEnd('\\');
    }
}
