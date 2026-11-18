using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DirectoryFable.Search;

/// <summary>
/// The NTFS change journal ("USN journal") of a drive: Windows writes a short record for every change of a file
/// or folder (created, deleted, renamed or moved, data grown or cut). Reading it from a known position tells
/// what changed since then, without reading the drive's file table again. Needs administrator rights (a read of
/// the volume), like <see cref="MftReader"/>. Only reads: DirectoryFable never creates a journal (that would add
/// writes to the drive); a drive without one keeps the full reads.
/// </summary>
internal static class UsnJournal
{
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;

    public const uint DataExtend = 0x00000002;
    public const uint DataTruncation = 0x00000004;
    public const uint FileCreate = 0x00000100;
    public const uint FileDelete = 0x00000200;
    public const uint RenameOldName = 0x00001000;
    public const uint RenameNewName = 0x00002000;

    /// <summary>The changes that can change a size or the folder tree.</summary>
    private const uint SizeReasons = DataExtend | DataTruncation | FileCreate | FileDelete | RenameOldName | RenameNewName;

    /// <summary>One change: the item's and its folder's MFT record numbers (low 48 bits of the file reference).</summary>
    public readonly record struct Change(long Record, long ParentRecord, uint Reason, bool IsFolder, string Name);

    /// <summary>The journal's id and the position of its next record; null when the drive has no active journal.</summary>
    public static (ulong JournalId, long NextUsn)? Query(SafeFileHandle volume)
    {
        var data = new byte[80];
        if (!DeviceIoControl(volume, FsctlQueryUsnJournal, null, 0, data, (uint)data.Length, out uint returned, IntPtr.Zero) || returned < 24)
            return null;
        return (BinaryPrimitives.ReadUInt64LittleEndian(data), BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16)));
    }

    /// <summary>
    /// The changes from <paramref name="startUsn"/> on, added to <paramref name="changes"/> (at most about
    /// <paramref name="limit"/>); returns the position to read from next time. Throws when the journal cannot be
    /// read from there (it was deleted, or its oldest records were dropped): then the drive must be read again.
    /// </summary>
    public static long Read(string root, ulong journalId, long startUsn, List<Change> changes, int limit)
    {
        using var volume = MftReader.OpenVolume(root);
        if (volume.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot read {root} directly.");
        var input = new byte[40];
        var buffer = new byte[64 * 1024];
        long position = startUsn;
        while (true)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input, position);                 // StartUsn
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), SizeReasons);  // ReasonMask
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12), 0);           // ReturnOnlyOnClose: every record
            BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16), 0);            // Timeout
            BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(24), 0);            // BytesToWaitFor: do not wait
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);   // UsnJournalID
            if (!DeviceIoControl(volume, FsctlReadUsnJournal, input, (uint)input.Length, buffer, (uint)buffer.Length, out uint returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"The change journal of {root} cannot be read.");
            long next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
            Parse(buffer.AsSpan(8, (int)Math.Max(0, returned - 8)), changes);
            if (returned <= 8 || next == position || changes.Count >= limit)
                return next;
            position = next;
        }
    }

    /// <summary>USN_RECORD_V2 entries (NTFS writes these for the V0 request).</summary>
    private static void Parse(ReadOnlySpan<byte> data, List<Change> changes)
    {
        int p = 0;
        while (p + 60 <= data.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
            if (length < 60 || p + length > data.Length)
                break;
            var record = data.Slice(p, length);
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) == 2)
            {
                long file = (long)(BinaryPrimitives.ReadUInt64LittleEndian(record[8..]) & 0xFFFF_FFFF_FFFF);
                long parent = (long)(BinaryPrimitives.ReadUInt64LittleEndian(record[16..]) & 0xFFFF_FFFF_FFFF);
                uint reason = BinaryPrimitives.ReadUInt32LittleEndian(record[40..]);
                uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
                int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
                string name = nameOffset + nameLength <= length
                    ? new string(MemoryMarshal.Cast<byte, char>(record.Slice(nameOffset, nameLength)))
                    : "";
                changes.Add(new Change(file, parent, reason, (attributes & 0x10) != 0, name));
            }
            p += length;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, uint inputSize,
        byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
