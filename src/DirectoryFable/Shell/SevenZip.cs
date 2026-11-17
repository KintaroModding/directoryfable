using System.Diagnostics;
using System.IO;
using System.Text;
using DirectoryFable.Infrastructure;
using Microsoft.Win32;

namespace DirectoryFable.Shell;

/// <summary>
/// 7-Zip's "Add to archive..." window (quick action "7z"): starts 7zG.exe with
/// <c>-ad</c> (show the dialog), so format, compression and the archive name are chosen there.
/// </summary>
public static class SevenZip
{
    static SevenZip() => Configure("");

    /// <summary>7zG.exe (the window program), or null when 7-Zip was not found.</summary>
    public static string? GuiPath { get; private set; }

    /// <summary>7z.exe (console) next to 7zG.exe: lists and unpacks 7z / RAR archives (<see cref="ZipBrowser"/>), or null.</summary>
    public static string? ConsolePath { get; private set; }

    /// <summary>True when the 7-Zip folder setting was set but has no 7zG.exe (the automatic one is used then).</summary>
    public static bool ConfiguredFolderInvalid { get; private set; }

    /// <summary>
    /// Settings "sevenZipFolder": a folder with 7zG.exe / 7z.exe; empty = find it automatically
    /// (registry, then Program Files). Called at start and when the setting changes.
    /// </summary>
    public static void Configure(string? folder)
    {
        string? gui = null;
        ConfiguredFolderInvalid = false;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            string candidate = Path.Combine(Environment.ExpandEnvironmentVariables(folder.Trim()), "7zG.exe");
            if (File.Exists(candidate))
                gui = candidate;
            else
                ConfiguredFolderInvalid = true;
        }
        gui ??= FindAutomatically();
        GuiPath = gui;
        string? console = gui is null ? null : Path.Combine(Path.GetDirectoryName(gui)!, "7z.exe");
        ConsolePath = console is not null && File.Exists(console) ? console : null;
    }

    private static string? FindAutomatically()
    {
        foreach (string value in new[] { "Path64", "Path" })
        {
            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\7-Zip", value, null) is string folder &&
                File.Exists(Path.Combine(folder, "7zG.exe")))
                return Path.Combine(folder, "7zG.exe");
        }
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7zG.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>
    /// Opens the window for <paramref name="paths"/> (all in <paramref name="folder"/>). The
    /// suggested name is the item's name (one item) or the folder's name (several), in that
    /// folder; <c>-saa</c> adds the extension of the format chosen in the window. The names go
    /// through a UTF-8 list file, so long selections and any characters work.
    /// </summary>
    public static void AddToArchive(string folder, IReadOnlyList<string> paths)
    {
        if (GuiPath is null || paths.Count == 0)
            return;

        var names = paths.Select(p => Path.GetFileName(p.TrimEnd('\\'))).ToList();
        string baseName = paths.Count == 1
            ? (Directory.Exists(paths[0]) ? names[0] : Path.GetFileNameWithoutExtension(names[0]))
            : PathHelper.GetName(folder);
        if (baseName.Length == 0 || baseName.Contains(':') || baseName.StartsWith(@"\\", StringComparison.Ordinal))
            baseName = "Archive"; // Drive root or server: no usable folder name.

        string listFolder = Path.Combine(Path.GetTempPath(), "DirectoryFable", "7z");
        Directory.CreateDirectory(listFolder);
        string listFile = Path.Combine(listFolder, $"list-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(listFile, names, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var info = new ProcessStartInfo(GuiPath)
        {
            WorkingDirectory = folder, // The list holds plain names, so the archive has no folder paths.
            UseShellExecute = false,
        };
        // No "--": it would also switch off the @list file. The archive is a full path, so a name
        // that starts with "-" cannot be read as a switch.
        foreach (string arg in new[] { "a", "-ad", "-saa", "-scsUTF-8", Path.Combine(folder, baseName), "@" + listFile })
            info.ArgumentList.Add(arg);

        var process = Process.Start(info);
        // The list file is only needed until 7-Zip is done.
        _ = Task.Run(async () =>
        {
            try
            {
                if (process is not null)
                    await process.WaitForExitAsync();
            }
            finally
            {
                try
                {
                    File.Delete(listFile);
                }
                catch (IOException)
                {
                }
            }
        });
    }
}
