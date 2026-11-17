using System.IO;
using System.Runtime.InteropServices;

namespace DirectoryFable.Shell;

/// <summary>
/// Lists the disk shares of a network server (<c>\\SERVER</c> is not a folder: .NET cannot
/// enumerate it). Hidden admin shares (<c>C$</c>, <c>IPC$</c>) are skipped, like Explorer.
/// Call off the UI thread: an offline server can take seconds to answer.
/// </summary>
public static class NetworkShares
{
    public static List<string> List(string server)
    {
        string name = server.TrimStart('\\').TrimEnd('\\');
        int result = NetShareEnum(name, 1, out IntPtr buffer, MAX_PREFERRED_LENGTH, out int read, out _, IntPtr.Zero);
        try
        {
            if (result != 0)
                throw new IOException($"Cannot list the shares of \\\\{name} (error {result}).");

            var shares = new List<string>(read);
            int size = Marshal.SizeOf<SHARE_INFO_1>();
            for (int i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<SHARE_INFO_1>(buffer + i * size);
                bool disk = (info.shi1_type & 0xFF) == STYPE_DISKTREE;
                bool special = (info.shi1_type & STYPE_SPECIAL) != 0 || info.shi1_netname.EndsWith('$');
                if (disk && !special)
                    shares.Add(info.shi1_netname);
            }
            shares.Sort(StringComparer.CurrentCultureIgnoreCase);
            return shares;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                NetApiBufferFree(buffer);
        }
    }

    private const int MAX_PREFERRED_LENGTH = -1;
    private const uint STYPE_DISKTREE = 0;
    private const uint STYPE_SPECIAL = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        public string shi1_netname;
        public uint shi1_type;
        public string shi1_remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string serverName, int level, out IntPtr bufPtr, int prefMaxLen,
        out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
