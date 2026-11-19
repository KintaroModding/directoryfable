using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DirectoryFable.Shell;

/// <summary>
/// The computers of the folder tree's "Network" group. Two sources: the ones known without asking the network
/// (<see cref="Known"/>: this computer, the servers of mapped network drives and of known paths such as favorites),
/// and the ones Windows' network discovery lists in its Network folder (<see cref="DiscoverAsync"/>). Discovery
/// answers over some seconds, often with nothing (it needs network discovery on, here and on the other computers),
/// so the known ones never wait for it.
/// </summary>
public static class NetworkComputers
{
    /// <summary>"\\NAME" of this computer, of mapped network drives and of the given network paths; sorted, each once.</summary>
    public static List<string> Known(IEnumerable<string> knownPaths)
    {
        var servers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { @"\\" + Environment.MachineName };
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Network && MappedTarget(drive.Name.TrimEnd('\\')) is { } target &&
                ServerOf(target) is { } mapped)
                servers.Add(mapped);
        }
        foreach (string path in knownPaths)
        {
            if (ServerOf(path) is { } server)
                servers.Add(server);
        }
        return [.. servers];
    }

    /// <summary>"\\server" of a network path ("\\server\share\folder"), or null for other paths.</summary>
    public static string? ServerOf(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return null;
        string name = path[2..].Split('\\', 2)[0];
        return name.Length > 0 ? @"\\" + name : null;
    }

    /// <summary>"\\server\share" of a mapped network drive ("Z:"), or null.</summary>
    public static string? MappedTarget(string letter)
    {
        var buffer = new StringBuilder(512);
        int length = buffer.Capacity;
        return WNetGetConnectionW(letter, buffer, ref length) == 0 ? buffer.ToString() : null;
    }

    /// <summary>
    /// Reads the computers of Windows' Network folder and reports each "\\NAME" once. Windows fills that folder
    /// while its discovery runs, and a single read often comes back empty, so it is read several times over about
    /// ten seconds. Runs on a thread of its own (the shell wants a single-threaded apartment); the task ends when
    /// the reads are done or <paramref name="token"/> is cancelled. It never throws.
    /// </summary>
    public static Task DiscoverAsync(IProgress<string> found, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Discover(found, token);
            }
            catch (Exception)
            {
                // No Network folder (a stripped system) or a failing discovery: only the known computers show.
            }
            done.SetResult();
        })
        {
            IsBackground = true,
            Name = "Network discovery",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private const int Rounds = 7;
    private static readonly TimeSpan RoundPause = TimeSpan.FromMilliseconds(1500);

    private static void Discover(IProgress<string> found, CancellationToken token)
    {
        Guid networkFolder = FOLDERID_NetworkFolder;
        Guid iidItem = typeof(IShellItem).GUID;
        SHGetKnownFolderItem(ref networkFolder, 0, IntPtr.Zero, ref iidItem, out IShellItem network);
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int round = 0; round < Rounds && !token.IsCancellationRequested; round++)
            {
                foreach (string server in Computers(network))
                {
                    if (seen.Add(server))
                        found.Report(server);
                }
                // Waiting on a handle (not Thread.Sleep) keeps this apartment's messages moving: the folder gets
                // its discovery answers through them.
                token.WaitHandle.WaitOne(RoundPause);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(network);
        }
    }

    /// <summary>The computers ("\\NAME") the Network folder holds now; media servers, routers and printers are left out.</summary>
    private static List<string> Computers(IShellItem network)
    {
        var result = new List<string>();
        Guid bhid = BHID_EnumItems;
        Guid iidEnum = typeof(IEnumShellItems).GUID;
        // No list at all: a failure, or an empty folder (it answers "nothing" without an error).
        if (network.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out IntPtr enumPointer) != 0 || enumPointer == IntPtr.Zero)
            return result;
        var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPointer);
        Marshal.Release(enumPointer);
        try
        {
            while (items.Next(1, out IShellItem item, out uint fetched) == 0 && fetched == 1)
            {
                try
                {
                    item.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out IntPtr name);
                    string parsing = Marshal.PtrToStringUni(name) ?? "";
                    Marshal.FreeCoTaskMem(name);
                    if (ServerOf(parsing) is { } server && server.Length == parsing.TrimEnd('\\').Length)
                        result.Add(server);
                }
                catch (Exception ex) when (ex is COMException or ArgumentException)
                {
                    // An item without a parsing name: not a computer.
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(items);
        }
        return result;
    }

    // ---------- Interop ----------

    private static readonly Guid FOLDERID_NetworkFolder = new("D20BEEC4-5CA8-4905-AE3B-BF251EA09B53");
    private static readonly Guid BHID_EnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetKnownFolderItem(ref Guid rfid, uint flags, IntPtr token, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string lpLocalName, StringBuilder lpRemoteName, ref int lpnLength);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem rgelt, out uint pceltFetched);

        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumShellItems ppenum);
    }
}
