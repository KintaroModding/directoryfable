using System.ComponentModel;
using System.IO;
using System.Text;
using DirectoryFable.Infrastructure;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;

namespace DirectoryFable.Editor;

/// <summary>
/// One open file in Notepad#, or a new file that has no path yet ("new 1"). The text keeps its
/// encoding (BOM or not, UTF-16, or the Windows ANSI code page for files that are not valid
/// UTF-8) and its line endings; saving writes it back the same way. "Changed" follows the undo
/// stack, so undoing back to the saved text shows it as saved again.
/// </summary>
public sealed class EditorDocument : ObservableObject
{
    /// <summary>Larger files are refused (the editor keeps the whole text in memory).</summary>
    private const long MaxBytes = 200L * 1024 * 1024;

    private IHighlightingDefinition? _highlighting;
    private bool _isDirty;
    private bool _isDragging;
    private bool _isMissing;
    private bool _hasContent = true;
    private readonly string? _untitledName;

    static EditorDocument() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private EditorDocument(string path, string text, Encoding encoding, string? untitledName = null)
    {
        FilePath = path;
        _untitledName = untitledName;
        Encoding = encoding;
        Document = new TextDocument(text) { FileName = path.Length > 0 ? path : null };
        Document.UndoStack.PropertyChanged += UndoStack_PropertyChanged;
        Document.TextChanged += (_, _) => ChangeCount++;
        _highlighting = path.Length > 0 ? EditorHighlighting.ByExtension(path) : null;
    }

    /// <summary>The file's path; "" for a new file that was never saved.</summary>
    public string FilePath { get; private set; }

    /// <summary>A new file ("new 1") that has no path yet: Save asks for one.</summary>
    public bool IsUntitled => FilePath.Length == 0;

    /// <summary>Identifies a new file's kept text in the cache (session); a GUID.</summary>
    public string UntitledId { get; private init; } = Guid.NewGuid().ToString("N");

    /// <summary>Key of this entry's text in the cache: the path, or the new file's id.</summary>
    public string CacheKey => IsUntitled ? "untitled:" + UntitledId : FilePath;

    /// <summary>Counts text changes (the cache writes a text only when it changed).</summary>
    public int ChangeCount { get; private set; }

    /// <summary>Name without the extension (left column of the file list).</summary>
    public string Stem => IsUntitled ? _untitledName!
        : Path.GetFileNameWithoutExtension(FilePath) is { Length: > 0 } stem ? stem : Path.GetFileName(FilePath);

    /// <summary>".txt", or "" (right column of the file list).</summary>
    public string Extension => !IsUntitled && Path.GetFileNameWithoutExtension(FilePath).Length > 0 ? Path.GetExtension(FilePath) : "";

    public string FileName => IsUntitled ? _untitledName! : Path.GetFileName(FilePath);

    public TextDocument Document { get; }

    public Encoding Encoding { get; private set; }

    /// <summary>
    /// The file no longer exists on disk (deleted or moved outside Notepad#). The entry stays,
    /// shown red and read-only; Save writes it back, "Dismiss" removes the entry.
    /// </summary>
    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (SetField(ref _isMissing, value))
                OnPropertyChanged(nameof(ToolTip));
        }
    }

    /// <summary>
    /// False for an entry restored from the session whose file was already gone: there is no
    /// text to show or save (only Dismiss). The text loads when the file comes back.
    /// </summary>
    public bool HasContent
    {
        get => _hasContent;
        private set => SetField(ref _hasContent, value);
    }

    /// <summary>The row's tooltip: the path, plus a note when the file is gone or new.</summary>
    public string ToolTip => IsUntitled ? $"{FileName}\nA new file, not saved yet."
        : IsMissing ? $"{FilePath}\nThis file no longer exists." : FilePath;

    // ---------- The file on disk ----------

    /// <summary>Write time and size of the file when its text was last read or written by Notepad#.</summary>
    public DateTime DiskWriteTimeUtc { get; private set; }
    public long DiskLength { get; private set; }

    /// <summary>
    /// Another program changed the file while this entry has unsaved changes: Notepad# asks
    /// whether to reload it when the file is shown.
    /// </summary>
    public bool ChangedOutside { get; set; }

    /// <summary>The file on disk differs from what Notepad# last read or wrote.</summary>
    public bool DiffersFromDisk(DateTime writeTimeUtc, long length) => writeTimeUtc != DiskWriteTimeUtc || length != DiskLength;

    /// <summary>Takes the disk state as known (no reload question for this change).</summary>
    public void AcceptDiskState(DateTime writeTimeUtc, long length)
    {
        DiskWriteTimeUtc = writeTimeUtc;
        DiskLength = length;
    }

    private void RememberDiskState()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Exists)
                AcceptDiskState(info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---------- Create ----------

    /// <summary>A new, empty file without a path ("new 1"); <paramref name="id"/> from the session keeps its cached text.</summary>
    public static EditorDocument CreateUntitled(string name, string? id = null) =>
        new("", "", new UTF8Encoding(false), name) { UntitledId = id ?? Guid.NewGuid().ToString("N") };

    /// <summary>An entry for a file that is gone (session restore): no text, shown red.</summary>
    public static EditorDocument CreateMissing(string path) =>
        new(path, "", new UTF8Encoding(false)) { IsMissing = true, HasContent = false };

    /// <summary>An entry for a file that is gone, with its text from the cache: read-only, Save writes it back.</summary>
    public static EditorDocument CreateMissing(string path, string text, string? encodingKey) =>
        new(path, text, EncodingFromKey(encodingKey)) { IsMissing = true };

    /// <summary>Reads a text file (off the UI thread). Binary files and very large files are refused.</summary>
    public static async Task<EditorDocument> LoadAsync(string path)
    {
        var (text, encoding, time, length) = await Task.Run(() => Read(path));
        var document = new EditorDocument(path, text, encoding);
        document.AcceptDiskState(time, length);
        return document;
    }

    private static (string Text, Encoding Encoding, DateTime Time, long Length) Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
            throw new InvalidDataException($"The file is too large for Notepad# ({DisplayFormat.Size(info.Length)}).");
        var (text, encoding) = Decode(File.ReadAllBytes(path));
        info.Refresh();
        return (text, encoding, info.LastWriteTimeUtc, info.Length);
    }

    /// <summary>The encoding as a short key for the session ("utf-8", "utf-8-bom", "utf-16le", "utf-16be", "1252").</summary>
    public string EncodingKey => Encoding switch
    {
        UTF8Encoding u => u.GetPreamble().Length > 0 ? "utf-8-bom" : "utf-8",
        UnicodeEncoding u => u.CodePage == 1201 ? "utf-16be" : "utf-16le",
        _ => Encoding.CodePage.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static Encoding EncodingFromKey(string? key) => key switch
    {
        "utf-8-bom" => new UTF8Encoding(true),
        "utf-16le" => new UnicodeEncoding(false, true),
        "utf-16be" => new UnicodeEncoding(true, true),
        null or "utf-8" => new UTF8Encoding(false),
        _ => int.TryParse(key, out int codePage) ? Encoding.GetEncoding(codePage) : new UTF8Encoding(false),
    };

    // ---------- Text from elsewhere ----------

    /// <summary>The file is back (e.g. restored from the Recycle Bin): read its text (off the UI thread).</summary>
    public async Task LoadContentAsync()
    {
        var (text, encoding, time, length) = await Task.Run(() => Read(FilePath));
        Encoding = encoding;
        Document.Text = text;
        Document.UndoStack.ClearAll();
        Document.UndoStack.MarkAsOriginalFile();
        AcceptDiskState(time, length);
        IsDirty = false;
        HasContent = true;
    }

    /// <summary>
    /// Reads the file again after another program changed it. The new text replaces the old one
    /// as one undoable step (Ctrl+Z brings the old text back) and counts as saved.
    /// </summary>
    public async Task ReloadAsync()
    {
        var (text, encoding, time, length) = await Task.Run(() => Read(FilePath));
        Encoding = encoding;
        if (text != Document.Text)
            Document.Replace(0, Document.TextLength, text);
        Document.UndoStack.MarkAsOriginalFile();
        AcceptDiskState(time, length);
        ChangedOutside = false;
        IsDirty = false;
    }

    /// <summary>
    /// Session restore: the unsaved text that was kept in the cache replaces the file's text as
    /// one undoable step, so the entry shows as changed and Ctrl+Z goes back to the saved text.
    /// </summary>
    public void ApplyUnsavedText(string text, string? encodingKey)
    {
        if (encodingKey is not null)
            Encoding = EncodingFromKey(encodingKey);
        if (text != Document.Text)
            Document.Replace(0, Document.TextLength, text);
    }

    /// <summary>Changed since the last save (or load).</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetField(ref _isDirty, value);
    }

    /// <summary>For the list's row style: a file row (not a group header).</summary>
    public bool IsGroupHeader => false;

    /// <summary>True while the file is dragged to a new place in the list (shown faded).</summary>
    public bool IsDragging
    {
        get => _isDragging;
        set => SetField(ref _isDragging, value);
    }

    /// <summary>The syntax highlighting of this file (Language menu); null = normal text.</summary>
    public IHighlightingDefinition? Highlighting
    {
        get => _highlighting;
        set
        {
            if (SetField(ref _highlighting, value))
                OnPropertyChanged(nameof(LanguageName));
        }
    }

    public string LanguageName => _highlighting?.Name ?? "Normal Text";

    /// <summary>Where the caret and the view were (kept while another file is shown).</summary>
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
    public double HorizontalOffset { get; set; }

    public string EncodingName => Encoding switch
    {
        UTF8Encoding u => u.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8",
        UnicodeEncoding u => (u.CodePage == 1201 ? "UTF-16 BE" : "UTF-16 LE"),
        _ => Encoding.WebName.ToUpperInvariant() == "WINDOWS-1252" ? "ANSI (1252)" : Encoding.WebName.ToUpperInvariant(),
    };

    /// <summary>"CRLF", "LF", "CR" or "Mixed" (from the first lines).</summary>
    public string LineEnding
    {
        get
        {
            int crlf = 0, lf = 0, cr = 0;
            foreach (var line in Document.Lines.Take(2000))
            {
                switch (line.DelimiterLength == 0 ? "" : Document.GetText(line.EndOffset, line.DelimiterLength))
                {
                    case "\r\n": crlf++; break;
                    case "\n": lf++; break;
                    case "\r": cr++; break;
                }
            }
            int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
            return kinds > 1 ? "Mixed" : lf > 0 ? "LF" : cr > 0 ? "CR" : "CRLF";
        }
    }

    private void UndoStack_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
            IsDirty = !Document.UndoStack.IsOriginalFile;
    }

    // ---------- Save ----------

    /// <summary>Writes the text back with the same encoding (and BOM) it was read with.</summary>
    public void Save()
    {
        if (IsUntitled)
            throw new InvalidOperationException("A new file needs a path: use Save as.");
        if (!HasContent)
            throw new IOException("The file no longer exists and its text is not known. Dismiss the entry instead.");
        File.WriteAllText(FilePath, Document.Text, Encoding);
        RememberDiskState();
        ChangedOutside = false;
        Document.UndoStack.MarkAsOriginalFile();
        IsDirty = false;
        IsMissing = false; // Saving writes the file back.
    }

    /// <summary>Saves under a new path; from then on the entry is that file.</summary>
    public void SaveAs(string path)
    {
        string oldPath = FilePath;
        FilePath = path;
        Document.FileName = path;
        try
        {
            HasContent = true;
            Save();
        }
        catch
        {
            FilePath = oldPath;
            Document.FileName = oldPath.Length > 0 ? oldPath : null;
            throw;
        }
        if (_highlighting is null)
            Highlighting = EditorHighlighting.ByExtension(path);
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(IsUntitled));
        OnPropertyChanged(nameof(Stem));
        OnPropertyChanged(nameof(Extension));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(ToolTip));
    }

    private static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
            return (new UTF8Encoding(true).GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
        if (bytes is [0xFF, 0xFE, ..])
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true));
        if (bytes is [0xFE, 0xFF, ..])
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(true, true));

        // Without a UTF-16 BOM, zero bytes mean a binary file (exe, image, ...).
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0)
            throw new InvalidDataException("This is not a text file (it contains binary data).");

        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: the Windows ANSI code page (Western European).
            var ansi = Encoding.GetEncoding(1252);
            return (ansi.GetString(bytes), ansi);
        }
    }
}
