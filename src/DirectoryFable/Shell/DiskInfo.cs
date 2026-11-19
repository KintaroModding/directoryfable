using System.Management;

namespace DirectoryFable.Shell;

/// <summary>What Windows knows about the physical disk behind a drive letter.</summary>
public sealed record DiskDetails(
    string Model,
    string MediaType,      // "SSD", "HDD", "SCM" or "" (not reported, e.g. USB sticks and virtual disks)
    string Bus,            // "NVMe", "SATA", "USB", ... or ""
    string Health,         // "Healthy", "Warning", "Unhealthy" or "" (unknown)
    int? Temperature,      // °C, when reported (needs administrator rights)
    int? Wear)             // % of the rated life used (SSDs), when reported
{
    /// <summary>"SSD · NVMe", "HDD · SATA", "USB", "Virtual disk", ...</summary>
    public string KindText => Bus switch
    {
        "Virtual" => "Virtual disk",
        "Storage Spaces" => "Storage Spaces",
        _ => string.Join(" · ", new[] { MediaType, Bus }.Where(t => t.Length > 0)),
    };

    /// <summary>"Healthy · 57 °C · 100 % life left" (only what is known), or "".</summary>
    public string HealthText => Health.Length == 0 ? "" : string.Join(" · ", new[]
    {
        Health,
        Temperature is > 0 and var t ? $"{t} °C" : "",
        Wear is >= 0 and <= 100 and var w ? $"{100 - w} % life left" : "",
    }.Where(t => t.Length > 0));
}

/// <summary>
/// Disk type and disk health for This PC, from Windows' storage management (WMI namespace
/// root\Microsoft\Windows\Storage, the data behind Windows' own "Drive health"): MSFT_Partition maps a drive letter
/// to its disk number, MSFT_PhysicalDisk gives model, media type, bus and health status, and its
/// MSFT_StorageReliabilityCounter the temperature and wear (administrator rights). Read in the background, at
/// most every few minutes (health changes slowly; a WMI query takes a moment); This PC asks again every few
/// seconds while it is shown. Network drives and drives Windows does not manage (some card readers) get nothing.
/// </summary>
public static class DiskInfo
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private static readonly Lock Gate = new();
    private static Dictionary<char, DiskDetails> _byLetter = [];
    private static DateTime _readAt = DateTime.MinValue;
    private static Task? _reading;

    /// <summary>The details of a drive ("C:\" or "C"), or null when not known (yet); starts a read when due.</summary>
    public static DiskDetails? For(string root)
    {
        if (root.Length == 0)
            return null;
        char letter = char.ToUpperInvariant(root[0]);
        lock (Gate)
        {
            bool due = DateTime.UtcNow - _readAt > MaxAge;
            bool unknown = !_byLetter.ContainsKey(letter) && DateTime.UtcNow - _readAt > TimeSpan.FromSeconds(30);
            if ((due || unknown) && _reading is null)
                _reading = Task.Run(Read);
            return _byLetter.GetValueOrDefault(letter);
        }
    }

    private static void Read()
    {
        var result = new Dictionary<char, DiskDetails>();
        // All fields of the disk (its key ObjectId too): the reliability counter is found through the disk's path.
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            var disks = new Dictionary<uint, DiskDetails>();
            using (var searcher = new ManagementObjectSearcher(scope,
                       new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk")))
            using (var found = searcher.Get())
            {
                foreach (ManagementObject disk in found)
                {
                    using (disk)
                    {
                        if (!uint.TryParse(disk["DeviceId"]?.ToString(), out uint number))
                            continue;
                        var (temperature, wear) = Reliability(disk);
                        disks[number] = new DiskDetails(
                            Model: disk["FriendlyName"]?.ToString()?.Trim() ?? "",
                            MediaType: Convert.ToUInt16(disk["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" },
                            Bus: BusName(Convert.ToUInt16(disk["BusType"] ?? 0)),
                            Health: Convert.ToUInt16(disk["HealthStatus"] ?? 5) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "" },
                            Temperature: temperature,
                            Wear: wear);
                    }
                }
            }
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition")))
            using (var found = searcher.Get())
            {
                foreach (ManagementObject partition in found)
                {
                    using (partition)
                    {
                        char letter = partition["DriveLetter"] is char c ? char.ToUpperInvariant(c) : '\0';
                        if (letter is >= 'A' and <= 'Z' && partition["DiskNumber"] is uint number && disks.TryGetValue(number, out var details))
                            result[letter] = details;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                                       or InvalidCastException or FormatException or OverflowException or InvalidOperationException)
        {
            // No storage management (or no rights): the tiles stay as they are.
        }
        finally
        {
            lock (Gate)
            {
                if (result.Count > 0 || _byLetter.Count == 0)
                    _byLetter = result; // A failed read keeps what was known.
                _readAt = DateTime.UtcNow;
                _reading = null; // Also after an unexpected error: the next read can start.
            }
        }
    }

    /// <summary>Temperature and wear from the disk's reliability counter (null when not reported or no rights).</summary>
    private static (int? Temperature, int? Wear) Reliability(ManagementObject disk)
    {
        try
        {
            foreach (ManagementObject counter in disk.GetRelated("MSFT_StorageReliabilityCounter"))
            {
                using (counter)
                {
                    int? temperature = counter["Temperature"] is { } t && Convert.ToInt32(t) is > 0 and < 150 and var tv ? tv : null;
                    int? wear = counter["Wear"] is { } w && Convert.ToInt32(w) is >= 0 and <= 100 and var wv ? wv : null;
                    return (temperature, wear);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                                       or InvalidCastException or FormatException or OverflowException or InvalidOperationException)
        {
            // Not reported.
        }
        return (null, null);
    }

    private static string BusName(ushort bus) => bus switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "FireWire",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "",
    };
}
