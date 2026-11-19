using System.Windows.Media;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Models;

/// <summary>One row in a file list.</summary>
public sealed class FileItem : ObservableObject
{
    private bool _isRenaming;
    private bool _isDropTarget;
    private long? _size;
    private long? _folderSize;
    private bool _folderSizePending;
    private DateTime _dateCreated;
    private DateTime _dateModified;
    private bool _isHidden;
    private ImageSource? _icon;
    private bool _isCut;
    private string _compareMark = "";

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }

    public long? Size
    {
        get => _size;
        init => _size = value;
    }

    /// <summary>A folder's size: all files below it, from the drive's file table (null = unknown).</summary>
    public long? FolderSize
    {
        get => _folderSize;
        set
        {
            if (SetField(ref _folderSize, value))
                OnPropertyChanged(nameof(SizeText));
        }
    }

    /// <summary>True while the drive is read for the first time: the folder's Size shows "…".</summary>
    public bool FolderSizePending
    {
        get => _folderSizePending;
        set
        {
            if (SetField(ref _folderSizePending, value))
                OnPropertyChanged(nameof(SizeText));
        }
    }

    /// <summary>Sorting by size: a file's size, a folder's total (unknown = smallest).</summary>
    public long? SortSize => IsDirectory ? _folderSize : _size;

    public DateTime DateCreated
    {
        get => _dateCreated;
        init => _dateCreated = value;
    }

    public DateTime DateModified
    {
        get => _dateModified;
        init => _dateModified = value;
    }

    public bool IsHidden
    {
        get => _isHidden;
        init => _isHidden = value;
    }

    /// <summary>A share of a network server (shown when browsing "\\SERVER").</summary>
    public bool IsShare { get; init; }

    /// <summary>A file or folder inside a ZIP file (FullPath = "C:\x\a.zip\inner\path").</summary>
    public bool IsInArchive { get; init; }

    public string TypeName { get; set; } = "";
    /// <summary>First the icon of the extension; .exe/.lnk/.ico rows get their own one later (notifies).</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    /// <summary>True while the name is being edited in place (F2).</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set => SetField(ref _isRenaming, value);
    }

    /// <summary>On the clipboard as "cut" (Ctrl+X): the row is shown faded until pasted.</summary>
    public bool IsCut
    {
        get => _isCut;
        set => SetField(ref _isCut, value);
    }

    /// <summary>
    /// "Compare" (the two panes against each other): "only" (not on the other side), "newer", "older", "differs", or
    /// "" (the same, or no compare runs).
    /// </summary>
    public string CompareMark
    {
        get => _compareMark;
        set
        {
            if (SetField(ref _compareMark, value))
                OnPropertyChanged(nameof(CompareText));
        }
    }

    /// <summary>The mark as the row shows it.</summary>
    public string CompareText => _compareMark switch
    {
        "only" => "only here",
        "newer" => "newer",
        "older" => "older",
        "differs" => "differs",
        _ => "",
    };

    /// <summary>True while files are dragged over this folder row (drop goes into it).</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetField(ref _isDropTarget, value);
    }

    /// <summary>Extensions Windows Explorer never shows (shortcut types, "NeverShowExt").</summary>
    private static readonly string[] NeverShownExtensions = [".lnk", ".url", ".pif", ".appref-ms"];

    /// <summary>The hidden extension of a shortcut (".lnk"), or "" when the full name is shown.</summary>
    public string HiddenExtension =>
        IsDirectory ? "" : NeverShownExtensions.FirstOrDefault(x => Name.EndsWith(x, StringComparison.OrdinalIgnoreCase) && Name.Length > x.Length) ?? "";

    /// <summary>The name in the list: shortcuts without ".lnk" / ".url", like Explorer.</summary>
    public string DisplayName => Name[..^HiddenExtension.Length];

    /// <summary>File extension without the dot ("txt"); empty for folders.</summary>
    public string Extension => IsDirectory ? "" : System.IO.Path.GetExtension(Name).TrimStart('.');

    /// <summary>Folders: their total when known ("…" while the drive is read), else empty.</summary>
    public string SizeText => IsDirectory
        ? _folderSize is long total ? DisplayFormat.Size(total) : _folderSizePending ? "…" : ""
        : Size is long bytes ? DisplayFormat.Size(bytes) : "";

    public string DateCreatedText => DisplayFormat.Date(DateCreated);

    public string DateModifiedText => DisplayFormat.Date(DateModified);

    /// <summary>
    /// Auto-refresh: takes the new size/dates of the same file without replacing the row,
    /// so selection, focus and scroll position stay.
    /// </summary>
    public void UpdateFrom(FileItem fresh)
    {
        if (_size != fresh.Size)
        {
            _size = fresh.Size;
            OnPropertyChanged(nameof(Size));
            OnPropertyChanged(nameof(SizeText));
        }
        if (_dateCreated != fresh.DateCreated)
        {
            _dateCreated = fresh.DateCreated;
            OnPropertyChanged(nameof(DateCreated));
            OnPropertyChanged(nameof(DateCreatedText));
        }
        if (_dateModified != fresh.DateModified)
        {
            _dateModified = fresh.DateModified;
            OnPropertyChanged(nameof(DateModified));
            OnPropertyChanged(nameof(DateModifiedText));
        }
        if (_isHidden != fresh.IsHidden)
        {
            _isHidden = fresh.IsHidden;
            OnPropertyChanged(nameof(IsHidden));
        }
    }
}
