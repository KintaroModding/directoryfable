using System.Collections.ObjectModel;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Editor;

/// <summary>
/// A group in Notepad#'s file list. Own groups are named by the user and
/// keep the order the user gives their files. Files in no own group are grouped automatically
/// by type or by folder (setting): those automatic groups come and go with their files. Every
/// group can be collapsed and moved by drag and drop.
/// </summary>
public sealed class EditorGroup : ObservableObject
{
    private string _name;
    private bool _isExpanded = true;
    private bool _isDragging;

    /// <summary>An own group.</summary>
    public EditorGroup(string name)
    {
        _name = name;
        Key = "";
        Documents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CountText));
    }

    /// <summary>An automatic group: <paramref name="key"/> = ".txt" (by type) or the folder path (by folder).</summary>
    public EditorGroup(string name, string key, string toolTip) : this(name)
    {
        Key = key;
        IsAuto = true;
        ToolTip = toolTip;
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    /// <summary>True for an automatic group (by type or folder).</summary>
    public bool IsAuto { get; }

    /// <summary>Automatic groups: the extension or folder they stand for; own groups: "".</summary>
    public string Key { get; }

    public string? ToolTip { get; }

    /// <summary>For the list's row style: a header row (not a file).</summary>
    public bool IsGroupHeader => true;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    /// <summary>True while the group is dragged to a new place (header shown faded).</summary>
    public bool IsDragging
    {
        get => _isDragging;
        set => SetField(ref _isDragging, value);
    }

    public ObservableCollection<EditorDocument> Documents { get; } = [];

    /// <summary>"(3)": shown next to the name while the group is collapsed.</summary>
    public string CountText => $"({Documents.Count})";
}
