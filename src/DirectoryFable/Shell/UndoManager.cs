using System.IO;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Shell;

public enum UndoKind
{
    Copy,
    Move,
    Rename,
    Delete,
}

/// <summary>
/// One item of an operation: where it was, and where it is now (the new path, or for a
/// delete the item's Recycle Bin parsing name).
/// </summary>
public sealed record UndoItem(string Original, string Current);

public sealed record UndoEntry(UndoKind Kind, IReadOnlyList<UndoItem> Items, string Description);

/// <summary>
/// App-wide undo list (Ctrl+Z, "Undo" in the action bar), newest last. Filled by
/// <see cref="FileOperations"/> from what Windows reports per item; undo runs the reverse
/// operation through the same copy engine (progress, conflicts, elevation as usual):
/// copy → copies to the Recycle Bin, move/rename → back to the old place and name,
/// delete → restored from the Recycle Bin. Permanent deletes and ZIP changes are not recorded.
/// </summary>
public static class UndoManager
{
    private const int Limit = 50;
    private static readonly List<UndoEntry> Entries = [];
    private static bool _busy;

    /// <summary>Raised on the UI thread when the list changed.</summary>
    public static event Action? Changed;

    /// <summary>What Ctrl+Z would undo now, or null.</summary>
    public static UndoEntry? Next => Entries.Count > 0 ? Entries[^1] : null;

    /// <summary>Adds an operation (call on the UI thread). Operations without results are ignored.</summary>
    internal static void Record(UndoKind kind, IReadOnlyList<UndoItem> items, string destinationFolder)
    {
        if (items.Count == 0)
            return;
        Entries.Add(new UndoEntry(kind, items, Describe(kind, items, destinationFolder)));
        if (Entries.Count > Limit)
            Entries.RemoveAt(0);
        Changed?.Invoke();
    }

    private static string Describe(UndoKind kind, IReadOnlyList<UndoItem> items, string folder)
    {
        string what = items.Count == 1 ? $"\"{Path.GetFileName(items[0].Original)}\"" : $"{items.Count:N0} items";
        string to = folder.Length > 0 ? $" to {PathHelper.GetName(folder)}" : "";
        return kind switch
        {
            UndoKind.Copy => $"Copy {what}{to}",
            UndoKind.Move => $"Move {what}{to}",
            UndoKind.Rename => $"Rename {what} to \"{Path.GetFileName(items[0].Current)}\"",
            _ => $"Delete {what}",
        };
    }

    /// <summary>Undoes the newest operation. Returns a message for the status bar, or null when there is nothing to do.</summary>
    public static async Task<string?> UndoAsync(IntPtr owner)
    {
        if (_busy || Next is not { } entry)
            return null;
        _busy = true;
        Entries.RemoveAt(Entries.Count - 1);
        Changed?.Invoke();
        try
        {
            var (ok, missing) = await ReverseAsync(owner, entry);
            if (missing == entry.Items.Count)
                return $"Nothing to undo: the items of \"{entry.Description}\" are no longer there.";
            string note = missing > 0 ? $" ({missing:N0} item(s) no longer there)" : "";
            return ok ? $"Undone: {entry.Description}{note}" : $"Undo did not complete: {entry.Description}{note}";
        }
        finally
        {
            _busy = false;
        }
    }

    private static async Task<(bool Ok, int Missing)> ReverseAsync(IntPtr owner, UndoEntry entry)
    {
        switch (entry.Kind)
        {
            case UndoKind.Copy:
            {
                var existing = entry.Items.Select(i => i.Current).Where(Exists).ToList();
                int missing = entry.Items.Count - existing.Count;
                bool ok = existing.Count == 0 || await FileOperations.DeleteAsync(owner, existing, permanent: false, record: false);
                return (ok, missing);
            }

            case UndoKind.Move:
            case UndoKind.Delete:
            {
                // Moved items go back; deleted items come back out of the Recycle Bin.
                var moves = entry.Items
                    .Where(i => entry.Kind == UndoKind.Delete || Exists(i.Current))
                    .Select(i => (i.Current, Path.GetDirectoryName(i.Original) ?? "", Path.GetFileName(i.Original)))
                    .ToList();
                int notFound = entry.Items.Count - moves.Count;
                var (ok, missing) = await FileOperations.MoveBackAsync(owner, moves);
                return (ok, notFound + missing);
            }

            default: // Rename
            {
                int missing = 0;
                bool ok = true;
                foreach (var item in entry.Items)
                {
                    if (!Exists(item.Current))
                    {
                        missing++;
                        continue;
                    }
                    ok &= await FileOperations.RenameAsync(owner, item.Current, Path.GetFileName(item.Original), record: false);
                }
                return (ok, missing);
            }
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
