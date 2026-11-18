using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DirectoryFable.Search;

/// <summary>
/// Reads every file and folder of an NTFS drive from its Master File Table: the drive's own list
/// of all files, read directly in large blocks (a few seconds for millions of files). Needs
/// administrator rights (a raw read of the volume); it only reads, nothing on the drive changes.
/// Per entry: name, parent folder, real size ($DATA), creation / modification / access time and
/// attributes ($STANDARD_INFORMATION). Records that continue in extension records are merged into
/// their base record; NTFS's own metadata files ($MFT, $Extend, ...) are left out. Hard links show
/// once (their first name).
/// Written after the managed MFT parser of Lertaro (MIT, see NOTICE.md).
/// </summary>
public static class MftReader
{
    private const uint FsctlGetNtfsVolumeData = 0x00090064;
    private const int FirstUserRecord = 16;   // 0-15: $MFT, $MFTMirr, $LogFile, $Volume, ..., $Extend (11).
    private const int RootRecord = 5;

    /// <summary>True for an NTFS drive (no access check).</summary>
    public static bool IsNtfs(string root)
    {
        try
        {
            return string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True when the volume can be read raw (administrator, NTFS).</summary>
    public static bool CanRead(string root)
    {
        try
        {
            if (!string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                return false;
            using var volume = OpenVolume(root);
            return !volume.IsInvalid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static DriveIndex Read(string root, Action<double>? progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        using var volume = OpenVolume(root);
        if (volume.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot read {root} directly (administrator rights needed).");

        var data = new byte[512];
        if (!DeviceIoControl(volume, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, data, (uint)data.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{root} is not an NTFS drive.");
        int bytesPerSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40));
        int bytesPerCluster = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44));
        int recordSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48));
        long mftValidLength = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(56));
        long mftStartLcn = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(64));
        if (bytesPerSector <= 0 || bytesPerCluster <= 0 || recordSize <= 0 || recordSize % bytesPerSector != 0)
            throw new InvalidDataException($"Unexpected NTFS layout on {root}.");

        // Record 0 is $MFT itself: its $DATA runs say where the whole table lies on the disk.
        var record0 = new byte[recordSize];
        ReadAt(volume, mftStartLcn * bytesPerCluster, record0);
        if (!ApplyFixup(record0, bytesPerSector))
            throw new InvalidDataException($"The file table of {root} cannot be read.");
        var extents = MftExtents(record0, bytesPerCluster);

        long recordCount = Math.Min(mftValidLength / recordSize, int.MaxValue - 1);
        // The change journal's position before the read: changes from here on can be followed (UsnJournal).
        var journal = UsnJournal.Query(volume);
        var reader = new RecordParser(root, (int)recordCount, bytesPerSector);
        int chunkRecords = Math.Max(1, (4 << 20) / recordSize);
        var buffer = new byte[chunkRecords * recordSize];
        long record = 0;
        foreach (var (offset, length) in extents)
        {
            long position = offset, left = length;
            while (left > 0 && record < recordCount)
            {
                token.ThrowIfCancellationRequested();
                int bytes = (int)Math.Min(buffer.Length, left);
                bytes -= bytes % recordSize;
                if (bytes == 0)
                    break;
                ReadAt(volume, position, buffer.AsSpan(0, bytes));
                for (int p = 0; p + recordSize <= bytes && record < recordCount; p += recordSize, record++)
                    reader.Parse(buffer.AsSpan(p, recordSize), (int)record);
                position += bytes;
                left -= bytes;
                progress?.Invoke((double)record / recordCount);
            }
        }
        var index = reader.Finish();
        index.ReadTime = watch.Elapsed;
        index.ReadAt = DateTime.Now;
        if (journal is { } j)
        {
            index.JournalId = j.JournalId;
            index.StartUsn = j.NextUsn;
            index.HasJournal = true;
        }
        return index;
    }

    internal static SafeFileHandle OpenVolume(string root) =>
        CreateFileW(@"\\.\" + root.TrimEnd('\\'), 0x80000000 /* GENERIC_READ */, 7 /* share read, write, delete */,
            IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);

    private static void ReadAt(SafeFileHandle volume, long offset, Span<byte> target)
    {
        int done = 0;
        while (done < target.Length)
        {
            int n = RandomAccess.Read(volume, target[done..], offset + done);
            if (n <= 0)
                throw new EndOfStreamException("The drive ended while its file table was read.");
            done += n;
        }
    }

    /// <summary>
    /// NTFS protects each record with an "update sequence": the last two bytes of every sector
    /// hold a check value; the real bytes are stored in the record header. False = torn record.
    /// </summary>
    private static bool ApplyFixup(Span<byte> record, int bytesPerSector)
    {
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaOffset + usaCount * 2 > record.Length || usaCount == 0)
            return false;
        ushort check = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (int s = 1; s < usaCount; s++)
        {
            int end = s * bytesPerSector - 2;
            if (end + 2 > record.Length)
                break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != check)
                return false;
            record.Slice(usaOffset + s * 2, 2).CopyTo(record[end..]);
        }
        return true;
    }

    /// <summary>Where $MFT's unnamed $DATA lies on the disk: (byte offset, byte length) per run.</summary>
    private static List<(long Offset, long Length)> MftExtents(ReadOnlySpan<byte> record, int bytesPerCluster)
    {
        int a = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        while (a + 16 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[a..]);
            if (type == 0xFFFFFFFF)
                break;
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(a + 4)..]);
            if (length <= 0 || a + length > record.Length)
                break;
            if (type == 0x80 && record[a + 8] != 0 && record[a + 9] == 0)
            {
                int runs = a + BinaryPrimitives.ReadUInt16LittleEndian(record[(a + 0x20)..]);
                return DataRuns(record[runs..(a + length)], bytesPerCluster);
            }
            a += length;
        }
        throw new InvalidDataException("The file table's own record has no data runs.");
    }

    /// <summary>Decodes NTFS data runs: (cluster count, signed cluster offset relative to the previous run).</summary>
    private static List<(long Offset, long Length)> DataRuns(ReadOnlySpan<byte> runs, int bytesPerCluster)
    {
        var result = new List<(long, long)>();
        long lcn = 0;
        int p = 0;
        while (p < runs.Length && runs[p] != 0)
        {
            int lengthBytes = runs[p] & 0x0F, offsetBytes = runs[p] >> 4;
            p++;
            if (p + lengthBytes + offsetBytes > runs.Length)
                break;
            long clusters = 0;
            for (int k = 0; k < lengthBytes; k++)
                clusters |= (long)runs[p + k] << (8 * k);
            p += lengthBytes;
            if (offsetBytes == 0)
                continue; // Sparse run (not used by $MFT).
            long delta = 0;
            for (int k = 0; k < offsetBytes; k++)
                delta |= (long)runs[p + k] << (8 * k);
            if ((runs[p + offsetBytes - 1] & 0x80) != 0)
                delta |= -1L << (8 * offsetBytes); // Negative offset: sign-extend.
            p += offsetBytes;
            lcn += delta;
            result.Add((lcn * bytesPerCluster, clusters * bytesPerCluster));
        }
        return result;
    }

    /// <summary>Collects the attributes of all records; <see cref="Finish"/> turns them into a <see cref="DriveIndex"/>.</summary>
    private sealed class RecordParser
    {
        private readonly DriveIndex _index;
        private readonly int _bytesPerSector;
        private readonly int _count;
        private readonly bool[] _inUse;
        private readonly ushort[] _sequence;
        private readonly ulong[] _parentRef;
        private readonly byte[] _nameSpace; // 255 = no name yet.

        public RecordParser(string root, int count, int bytesPerSector)
        {
            _index = new DriveIndex(root, "MFT");
            _index.Allocate(count);
            _index.Names = new char[Math.Max(1 << 16, count * 16)];
            _bytesPerSector = bytesPerSector;
            _count = count;
            _inUse = new bool[count];
            _sequence = new ushort[count];
            _parentRef = new ulong[count];
            _nameSpace = new byte[count];
            Array.Fill(_nameSpace, (byte)255);
        }

        public void Parse(Span<byte> record, int number)
        {
            if (record.Length < 0x30 || BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x454C4946 /* "FILE" */ ||
                !ApplyFixup(record, _bytesPerSector))
                return;
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
            if ((flags & 0x01) == 0)
                return; // Free record.
            ulong baseRef = BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]);
            int target = baseRef == 0 ? number : (int)(baseRef & 0xFFFF_FFFF_FFFF);
            if (target < 0 || target >= _count)
                return;
            if (baseRef == 0)
            {
                _inUse[target] = true;
                _sequence[target] = BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..]);
                if ((flags & 0x02) != 0)
                    _index.Attributes[target] |= 0x10; // FILE_ATTRIBUTE_DIRECTORY
            }

            int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
            int a = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
            while (a + 16 <= used)
            {
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[a..]);
                if (type == 0xFFFFFFFF)
                    break;
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(a + 4)..]);
                if (length < 16 || a + length > used)
                    break;
                bool resident = record[a + 8] == 0;
                int attrNameLength = record[a + 9];
                var attr = record.Slice(a, length);
                switch (type)
                {
                    case 0x10 when resident: // $STANDARD_INFORMATION
                    {
                        int v = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
                        if (v + 0x24 <= attr.Length)
                        {
                            _index.Created[target] = BinaryPrimitives.ReadInt64LittleEndian(attr[v..]);
                            _index.Modified[target] = BinaryPrimitives.ReadInt64LittleEndian(attr[(v + 0x08)..]);
                            _index.Accessed[target] = BinaryPrimitives.ReadInt64LittleEndian(attr[(v + 0x18)..]);
                            _index.Attributes[target] |= BinaryPrimitives.ReadUInt32LittleEndian(attr[(v + 0x20)..]) & ~0x10u;
                        }
                        break;
                    }
                    case 0x30 when resident: // $FILE_NAME
                    {
                        int v = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
                        if (v + 0x42 > attr.Length)
                            break;
                        int chars = attr[v + 0x40];
                        byte nameSpace = attr[v + 0x41];
                        if (v + 0x42 + chars * 2 > attr.Length)
                            break;
                        // Keep the first long name; a DOS 8.3 name (namespace 2) only while there is no other.
                        byte had = _nameSpace[target];
                        if (had != 255 && (had != 2 || nameSpace == 2))
                            break;
                        var name = MemoryMarshal.Cast<byte, char>(attr.Slice(v + 0x42, chars * 2));
                        _index.NameStart[target] = _index.AddName(name);
                        _index.NameLength[target] = (ushort)chars;
                        _parentRef[target] = BinaryPrimitives.ReadUInt64LittleEndian(attr[v..]);
                        _nameSpace[target] = nameSpace;
                        break;
                    }
                    case 0x80 when attrNameLength == 0: // Unnamed $DATA = the file's content.
                        if (resident)
                            _index.Size[target] = BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]);
                        else if (BinaryPrimitives.ReadInt64LittleEndian(attr[0x10..]) == 0) // First segment (VCN 0) holds the real size.
                            _index.Size[target] = BinaryPrimitives.ReadInt64LittleEndian(attr[0x30..]);
                        break;
                }
                a += length;
            }
        }

        /// <summary>
        /// Parents become entry indexes; entries whose folder chain does not reach the root (metadata
        /// under $Extend, orphans of reused records) are dropped.
        /// </summary>
        public DriveIndex Finish()
        {
            var valid = new bool[_count];
            var state = new byte[_count]; // 0 = open, 1 = reaches the root, 2 = does not.
            var chain = new List<int>();
            int entries = 0;
            for (int i = 0; i < _count; i++)
            {
                if (state[i] == 0)
                    Resolve(i, state, chain);
                if (state[i] == 1)
                {
                    valid[i] = true;
                    entries++;
                }
            }
            return Pack(valid, entries);
        }

        /// <summary>
        /// Copies only the real entries into tight arrays (a drive's table often has more free
        /// records than files) and re-packs the names: about half the memory.
        /// </summary>
        private DriveIndex Pack(bool[] valid, int entries)
        {
            var source = _index;
            var packed = new DriveIndex(source.Root, source.Method);
            packed.Allocate(entries);
            packed.Record = new int[entries];
            var map = new int[_count];
            long nameChars = 0;
            for (int i = 0, n = 0; i < _count; i++)
            {
                map[i] = valid[i] ? n++ : -1;
                if (valid[i])
                    nameChars += source.NameLength[i];
            }
            packed.Names = new char[Math.Max(16, nameChars)];
            for (int i = 0; i < _count; i++)
            {
                int n = map[i];
                if (n < 0)
                    continue;
                int parent = source.Parent[i];
                packed.Parent[n] = parent < 0 ? -1 : map[parent];
                packed.NameStart[n] = packed.AddName(source.Name(i));
                packed.NameLength[n] = source.NameLength[i];
                packed.Size[n] = source.Size[i];
                packed.Created[n] = source.Created[i];
                packed.Modified[n] = source.Modified[i];
                packed.Accessed[n] = source.Accessed[i];
                packed.Attributes[n] = source.Attributes[i];
                packed.Record[n] = i; // The parser keeps entries at their MFT record number.
            }
            packed.EntryCount = entries;
            return packed;
        }

        private bool Candidate(int i) => i >= FirstUserRecord && _inUse[i] && _nameSpace[i] != 255;

        private void Resolve(int start, byte[] state, List<int> chain)
        {
            chain.Clear();
            int i = start;
            byte result = 2;
            while (true)
            {
                if (!Candidate(i))
                {
                    result = 2;
                    break;
                }
                if (state[i] != 0)
                {
                    result = state[i];
                    break;
                }
                chain.Add(i);
                ulong parentRef = _parentRef[i];
                int parent = (int)(parentRef & 0xFFFF_FFFF_FFFF);
                ushort parentSeq = (ushort)(parentRef >> 48);
                if (parent == RootRecord)
                {
                    _index.Parent[i] = -1;
                    result = 1;
                    break;
                }
                if (parent < 0 || parent >= _count || (parentSeq != 0 && _sequence[parent] != parentSeq) || chain.Count > 4096)
                {
                    result = 2;
                    break;
                }
                _index.Parent[i] = parent;
                i = parent;
            }
            foreach (int c in chain)
                state[c] = result;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize,
        byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
