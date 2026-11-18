using System.IO;
using System.Runtime.InteropServices;

namespace DirectoryFable.Shell;

/// <summary>
/// Copy, move, delete and rename through the Windows copy engine (IFileOperation).
/// This gives Recycle Bin, elevation prompts and undo records. Copy and move show DirectoryFable's
/// own dark progress window (<see cref="OperationProgress"/>) instead of Windows' white one, and ask about
/// names that are already taken in DirectoryFable's own dialog before they start (<see cref="ConflictPlanner"/>),
/// so the engine never shows its question; delete and rename keep Windows' dialogs. Each operation runs on its own STA thread so the window stays responsive.
/// Copy, move, rename and delete-to-Recycle-Bin are recorded in <see cref="UndoManager"/> with
/// the exact results Windows reports (new names such as "a - Copy.txt", Recycle Bin items).
/// </summary>
public static class FileOperations
{
    public static async Task<bool> CopyAsync(IntPtr owner, IReadOnlyList<string> sources, string destinationFolder)
    {
        // Copying a file into its own folder must create "name - Copy" (Explorer behaviour),
        // not ask "replace or skip". Rename-on-collision only for those files, so other
        // conflicts still get the normal Windows question.
        var sameFolder = sources.Where(s => IsInFolder(s, destinationFolder)).ToList();
        var otherFolders = sources.Except(sameFolder).ToList();
        bool ok = true;

        var planned = new List<PlannedItem>();
        uint flags = CopyMoveFlags;
        if (otherFolders.Count > 0)
        {
            if (await ResolveConflictsAsync(owner, otherFolders, destinationFolder, move: false) is not { } choices)
                return false; // Cancelled in the conflict dialog: nothing happens.
            planned = ConflictPlanner.Plan(otherFolders, destinationFolder, choices).Items;
            if (choices.Count > 0)
                flags |= FOF_NOCONFIRMATION; // Every conflict left is a "replace" the user chose.
        }
        var recorder = new UndoRecorder(planned.Select(i => i.Source).Concat(sameFolder));
        if (planned.Count > 0)
            ok &= await RunPlannedAsync(owner, planned, flags, recorder, move: false);
        if (sameFolder.Count > 0)
        {
            ok &= await RunPlannedAsync(owner, [.. sameFolder.Select(s => new PlannedItem(s, destinationFolder, null))],
                CopyMoveFlags | FOF_RENAMEONCOLLISION, recorder, move: false);
        }
        UndoManager.Record(UndoKind.Copy, recorder.Items, destinationFolder);
        return ok;
    }

    /// <summary>Runs the planned items as one copy or move (one progress window).</summary>
    private static Task<bool> RunPlannedAsync(IntPtr owner, IReadOnlyList<PlannedItem> items, uint flags, UndoRecorder recorder, bool move) =>
        RunAsync(owner, flags, op =>
        {
            var folders = new Dictionary<string, IShellItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (!folders.TryGetValue(item.TargetFolder, out var folder))
                    folders[item.TargetFolder] = folder = CreateItem(item.TargetFolder);
                if (move)
                    op.MoveItem(CreateItem(item.Source), folder, item.NewName, IntPtr.Zero);
                else
                    op.CopyItem(CreateItem(item.Source), folder, item.NewName, IntPtr.Zero);
            }
        }, recorder, progress: NewProgress(move ? OperationKind.Move : OperationKind.Copy), skipWhenEmpty: () => items.Count == 0);

    /// <summary>
    /// Finds names that are already taken in the target and asks about each in DirectoryFable's dialog ("do the same
    /// for the others" answers the rest; a file cannot replace a folder, so such a conflict is still asked).
    /// "Replace if newer" becomes Replace or Skip for each item by its own dates. Returns the choices by source
    /// path, or null when the user cancelled.
    /// </summary>
    private static async Task<Dictionary<string, ConflictChoice>?> ResolveConflictsAsync(
        IntPtr owner, IReadOnlyList<string> sources, string destinationFolder, bool move)
    {
        var conflicts = await Task.Run(() => ConflictPlanner.Find(sources, destinationFolder));
        var choices = new Dictionary<string, ConflictChoice>(StringComparer.OrdinalIgnoreCase);
        ConflictChoice? forAll = null;
        for (int i = 0; i < conflicts.Count; i++)
        {
            var conflict = conflicts[i];
            if (forAll is { } all && !(all is ConflictChoice.Replace or ConflictChoice.ReplaceIfNewer && conflict.TypeMismatch))
            {
                choices[conflict.Source] = Decide(all, conflict);
                continue;
            }
            if (Views.ConflictDialog.Ask(owner, conflict, i + 1, conflicts.Count, move) is not { } answer)
                return null;
            choices[conflict.Source] = Decide(answer.Choice, conflict);
            if (answer.ForAll)
                forAll = answer.Choice;
        }
        return choices;
    }

    private static ConflictChoice Decide(ConflictChoice choice, Conflict conflict) =>
        choice != ConflictChoice.ReplaceIfNewer ? choice
        : ConflictPlanner.IsNewer(conflict.Source, conflict.Target) ? ConflictChoice.Replace : ConflictChoice.Skip;

    private static bool IsInFolder(string path, string folder) =>
        string.Equals(Path.GetDirectoryName(path.TrimEnd('\\')), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> MoveAsync(IntPtr owner, IReadOnlyList<string> sources, string destinationFolder)
    {
        if (await ResolveConflictsAsync(owner, sources, destinationFolder, move: true) is not { } choices)
            return false; // Cancelled in the conflict dialog: nothing happens.
        var (planned, split) = ConflictPlanner.Plan(sources, destinationFolder, choices);
        var recorder = new UndoRecorder(planned.Select(i => i.Source));
        bool ok = await RunPlannedAsync(owner, planned, CopyMoveFlags | (choices.Count > 0 ? FOF_NOCONFIRMATION : 0), recorder, move: true);
        // A folder that was split into its children stays behind; remove it when nothing is left in it
        // (skipped items keep it). Deepest first.
        foreach (string folder in split.OrderByDescending(f => f.Length))
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep it.
            }
        }
        UndoManager.Record(UndoKind.Move, recorder.Items, destinationFolder);
        return ok;
    }

    /// <summary>
    /// Delete to the Recycle Bin, or permanently (the caller asks first, in DirectoryFable's own dialog; Windows
    /// still warns when an item cannot go to the Recycle Bin). Only the Recycle Bin case can be undone. <paramref name="record"/> = false: not undoable
    /// (used by Undo itself and by "move into a ZIP file").
    /// </summary>
    public static async Task<bool> DeleteAsync(IntPtr owner, IReadOnlyList<string> paths, bool permanent, bool record = true)
    {
        var recorder = !permanent && record ? new UndoRecorder(paths) : null;
        bool ok = await RunAsync(owner, permanent ? DeletePermanentFlags : RecycleFlags, op =>
        {
            foreach (string path in paths)
                op.DeleteItem(CreateItem(path), IntPtr.Zero);
        }, recorder, progress: NewProgress(permanent || paths.All(IsOnNetwork) ? OperationKind.Delete : OperationKind.Recycle));
        if (recorder is not null)
            UndoManager.Record(UndoKind.Delete, recorder.Items, "");
        return ok;
    }

    /// <summary>A network path has no Recycle Bin: Windows deletes it for good (after its own question).</summary>
    private static bool IsOnNetwork(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        try
        {
            return path.Length >= 2 && path[1] == ':' && new DriveInfo(path[..1]).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Copies one file or folder into <paramref name="destinationFolder"/> under a new name (Duplicate).</summary>
    public static async Task<bool> CopyAsAsync(IntPtr owner, string source, string destinationFolder, string newName)
    {
        var recorder = new UndoRecorder([source]);
        bool ok = await RunAsync(owner, CopyMoveFlags, op =>
            op.CopyItem(CreateItem(source), CreateItem(destinationFolder), newName, IntPtr.Zero), recorder, progress: NewProgress(OperationKind.Copy));
        UndoManager.Record(UndoKind.Copy, recorder.Items, destinationFolder);
        return ok;
    }

    public static async Task<bool> RenameAsync(IntPtr owner, string path, string newName, bool record = true)
    {
        var recorder = record ? new UndoRecorder([path]) : null;
        bool ok = await RunAsync(owner, UndoFlags, op =>
            op.RenameItem(CreateItem(path), newName, IntPtr.Zero), recorder);
        if (recorder is not null)
            UndoManager.Record(UndoKind.Rename, recorder.Items, "");
        return ok;
    }

    // ---------- Used by Undo (never recorded) ----------

    /// <summary>
    /// Moves items to a folder under a given name: moved items go back, Recycle Bin items are
    /// restored (<c>From</c> is then the item's Recycle Bin parsing name). Returns how many
    /// items could not be found any more.
    /// </summary>
    internal static async Task<(bool Ok, int Missing)> MoveBackAsync(
        IntPtr owner, IReadOnlyList<(string From, string ToFolder, string ToName)> moves)
    {
        int missing = 0;
        int queued = 0;
        bool ok = await RunAsync(owner, CopyMoveFlags, op =>
        {
            foreach (var (from, toFolder, toName) in moves)
            {
                IShellItem item, folder;
                try
                {
                    item = CreateItem(from);
                    // The folder may be gone (a merged move removes source folders it emptied): make it again.
                    if (!toFolder.StartsWith("::", StringComparison.Ordinal) && Path.IsPathFullyQualified(toFolder))
                        Directory.CreateDirectory(toFolder);
                    folder = CreateItem(toFolder);
                }
                catch (Exception ex) when (ex is COMException or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
                {
                    missing++; // Gone (emptied Recycle Bin, deleted, original folder removed).
                    continue;
                }
                op.MoveItem(item, folder, toName, IntPtr.Zero);
                queued++;
            }
        }, skipWhenEmpty: () => queued == 0);
        return (ok, missing);
    }

    // ---------- Runner ----------

    /// <summary>DirectoryFable's progress window for a copy, move or delete (created on the UI thread); null without one.</summary>
    private static OperationProgress? NewProgress(OperationKind kind) =>
        System.Windows.Application.Current?.Dispatcher is { } ui ? new OperationProgress(ui, kind) : null;

    /// <param name="recorder">Receives the per-item results (for Undo), or null.</param>
    /// <param name="skipWhenEmpty">After queuing: true = nothing was queued, do not run.</param>
    /// <param name="progress">Own progress window instead of Windows' dialog, or null.</param>
    private static Task<bool> RunAsync(
        IntPtr owner, uint flags, Action<IFileOperation> queue, UndoRecorder? recorder = null, Func<bool>? skipWhenEmpty = null,
        OperationProgress? progress = null)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            IFileOperation? op = null;
            IntPtr sink = IntPtr.Zero;
            IntPtr dialog = IntPtr.Zero;
            try
            {
                op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidFileOperation)!)!;
                op.SetOwnerWindow(owner);
                op.SetOperationFlags(flags);
                uint cookie = 0;
                if (recorder is not null)
                {
                    sink = Marshal.GetComInterfaceForObject<UndoRecorder, IFileOperationProgressSink>(recorder);
                    op.Advise(sink, out cookie);
                }
                if (progress is not null)
                {
                    progress.EngineThread = GetCurrentThreadId(); // Its question dialogs belong to this thread.
                    dialog = Marshal.GetComInterfaceForObject<OperationProgress, OperationProgress.IOperationsProgressDialog>(progress);
                    op.SetProgressDialog(dialog);
                }
                queue(op);
                if (skipWhenEmpty?.Invoke() == true)
                {
                    tcs.SetResult(true);
                    return;
                }

                int hr = op.PerformOperations();
                bool aborted = op.GetAnyOperationsAborted();
                if (recorder is not null)
                    op.Unadvise(cookie);
                tcs.SetResult(hr >= 0 && !aborted);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                if (sink != IntPtr.Zero)
                    Marshal.Release(sink);
                if (dialog != IntPtr.Zero)
                    Marshal.Release(dialog);
                if (op is not null)
                    Marshal.ReleaseComObject(op);
            }
        })
        {
            IsBackground = true,
            Name = "FileOperation",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }


    private static IShellItem CreateItem(string path)
    {
        Guid iid = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItem item);
        return item;
    }

    // ---------- Undo recording ----------

    /// <summary>
    /// Progress sink: Windows calls it after every item with the item that now exists (the
    /// copy, the moved/renamed item, or the Recycle Bin item). Only the requested top-level
    /// items count (a copied folder also reports its contents). A copy or move that replaced
    /// or merged into an existing item is not recorded: undoing it would delete or move data
    /// that was there before.
    /// </summary>
    [ComVisible(true)]
    public sealed class UndoRecorder(IEnumerable<string> sources) : IFileOperationProgressSink
    {
        private readonly HashSet<string> _sources = new(sources.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _existedBefore = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<UndoItem> _items = [];
        private readonly Lock _lock = new();

        public IReadOnlyList<UndoItem> Items
        {
            get
            {
                lock (_lock)
                    return [.. _items];
            }
        }

        private static string Normalize(string path) => path.TrimEnd('\\');

        private void NoteTarget(IShellItem item, IShellItem destination, string? newName)
        {
            try
            {
                if (PathOf(item) is not { } source || !_sources.Contains(source) || PathOf(destination) is not { } folder)
                    return;
                string target = Path.Combine(folder, newName ?? Path.GetFileName(source));
                if (File.Exists(target) || Directory.Exists(target))
                {
                    lock (_lock)
                        _existedBefore.Add(Normalize(target));
                }
            }
            catch (Exception)
            {
                // Never let the sink fail the operation.
            }
        }

        private void NoteResult(IShellItem item, int hr, IShellItem? created, bool recycleBin)
        {
            try
            {
                if (hr < 0 || created is null || PathOf(item) is not { } source || !_sources.Contains(source))
                    return;
                string? now = recycleBin ? NameOf(created, SIGDN_DESKTOPABSOLUTEPARSING) : PathOf(created);
                if (now is null)
                    return;
                lock (_lock)
                {
                    if (!recycleBin && _existedBefore.Contains(now))
                        return; // Replaced or merged into an existing item: not undoable.
                    _items.Add(new UndoItem(source, now));
                }
            }
            catch (Exception)
            {
                // Never let the sink fail the operation.
            }
        }

        private static string? PathOf(IShellItem item) => NameOf(item, SIGDN_FILESYSPATH) is { } p ? Normalize(p) : null;

        private static string? NameOf(IShellItem item, uint sigdn)
        {
            item.GetDisplayName(sigdn, out IntPtr name);
            try
            {
                return Marshal.PtrToStringUni(name);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }

        public void StartOperations() { }
        public void FinishOperations(int hrResult) { }
        public void PreRenameItem(uint flags, IShellItem item, string? newName) { }
        public void PostRenameItem(uint flags, IShellItem item, string? newName, int hr, IShellItem? created) =>
            NoteResult(item, hr, created, recycleBin: false);
        public void PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName) =>
            NoteTarget(item, destination, newName);
        public void PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName, int hr, IShellItem? created) =>
            NoteResult(item, hr, created, recycleBin: false);
        public void PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName) =>
            NoteTarget(item, destination, newName);
        public void PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName, int hr, IShellItem? created) =>
            NoteResult(item, hr, created, recycleBin: false);
        public void PreDeleteItem(uint flags, IShellItem item) { }
        public void PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem? created) =>
            NoteResult(item, hr, created, recycleBin: true);
        public void PreNewItem(uint flags, IShellItem destination, string? newName) { }
        public void PostNewItem(uint flags, IShellItem destination, string? newName, string? templateName, uint attributes, int hr, IShellItem? created) { }
        public void UpdateProgress(uint workTotal, uint workSoFar) { }
        public void ResetTimer() { }
        public void PauseTimer() { }
        public void ResumeTimer() { }
    }

    // ---------- Flags ----------

    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    private const uint FOF_RENAMEONCOLLISION = 0x0008;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMMKDIR = 0x0200;
    private const uint FOF_WANTNUKEWARNING = 0x4000;
    private const uint FOFX_SHOWELEVATIONPROMPT = 0x00040000;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;
    private const uint FOFX_ADDUNDORECORD = 0x20000000;

    private const uint UndoFlags = FOF_ALLOWUNDO | FOFX_ADDUNDORECORD | FOFX_SHOWELEVATIONPROMPT;
    private const uint CopyMoveFlags = UndoFlags | FOF_NOCONFIRMMKDIR;
    private const uint RecycleFlags = UndoFlags | FOFX_RECYCLEONDELETE | FOF_WANTNUKEWARNING;
    private const uint DeletePermanentFlags = FOF_NOCONFIRMATION | FOFX_SHOWELEVATIONPROMPT;

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // ---------- COM interop ----------

    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    public interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    // Method order must match shobjidl_core.h exactly.
    [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    public interface IFileOperationProgressSink
    {
        void StartOperations();
        void FinishOperations(int hrResult);
        void PreRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        void PostRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName,
            int hrRename, IShellItem? psiNewlyCreated);
        void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrMove, IShellItem? psiNewlyCreated);
        void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated);
        void PreDeleteItem(uint dwFlags, IShellItem psiItem);
        void PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated);
        void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem);
        void UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
        void ResetTimer();
        void PauseTimer();
        void ResumeTimer();
    }

    // Method order must match shobjidl_core.h exactly.
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr pfops, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(uint dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog(IntPtr popd);
        void SetProperties(IntPtr pproparray);
        void SetOwnerWindow(IntPtr hwndOwner);
        void ApplyPropertiesToItem(IShellItem psiItem);
        void ApplyPropertiesToItems(IntPtr punkItems);
        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        void RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);
        void MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);
        void CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
        void DeleteItems(IntPtr punkItems);
        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IntPtr pfopsItem);
        [PreserveSig] int PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }
}
