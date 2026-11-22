using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace DirectoryFable.Shell;

/// <summary>
/// WinRAR's console unpacker (UnRAR.exe): unpacks RAR archives when WinRAR is installed (else 7-Zip does it). Found
/// like 7-Zip: the settings folder ("winRarFolder"), else automatically (registry, then Program Files).
/// </summary>
public static class WinRar
{
    static WinRar() => Configure("");

    /// <summary>UnRAR.exe (or Rar.exe) of WinRAR, or null when WinRAR was not found.</summary>
    public static string? UnRarPath { get; private set; }

    /// <summary>True when the WinRAR folder setting was set but has no UnRAR.exe (the automatic one is used then).</summary>
    public static bool ConfiguredFolderInvalid { get; private set; }

    /// <summary>Settings "winRarFolder": a folder with UnRAR.exe; empty = find it automatically. Called at start and when the setting changes.</summary>
    public static void Configure(string? folder)
    {
        string? exe = null;
        ConfiguredFolderInvalid = false;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            exe = InFolder(Environment.ExpandEnvironmentVariables(folder.Trim()));
            if (exe is null)
                ConfiguredFolderInvalid = true;
        }
        UnRarPath = exe ?? FindAutomatically();
    }

    private static string? InFolder(string folder)
    {
        foreach (string name in new[] { "UnRAR.exe", "Rar.exe" })
        {
            string candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string? FindAutomatically()
    {
        // WinRAR writes the path of WinRAR.exe as "exe64" / "exe32"; App Paths has it too.
        foreach (var (key, value) in new[]
                 {
                     (@"HKEY_LOCAL_MACHINE\SOFTWARE\WinRAR", "exe64"),
                     (@"HKEY_LOCAL_MACHINE\SOFTWARE\WinRAR", "exe32"),
                     (@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe", ""),
                 })
        {
            if (Registry.GetValue(key, value, null) is string exe && Path.GetDirectoryName(exe.Trim('"')) is { } folder &&
                InFolder(folder) is { } found)
                return found;
        }
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            if (InFolder(Path.Combine(Environment.GetFolderPath(root), "WinRAR")) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Unpacks a whole RAR archive into <paramref name="target"/> with its folders ("x"); no password is asked
    /// ("-p-": a protected archive fails with a message). Throws an IOException with WinRAR's reason on an error.
    /// </summary>
    public static void ExtractAll(string archive, string target)
    {
        if (UnRarPath is not { } exe)
            throw new InvalidOperationException("WinRAR is not installed.");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // -y: yes to all, -o+: overwrite, -idq: quiet. The target ends with "\" (a folder).
        foreach (string argument in new[] { "x", "-y", "-o+", "-p-", "-idq", "--", archive, target.TrimEnd('\\') + "\\" })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("WinRAR did not start.");
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(30)))
        {
            process.Kill();
            throw new IOException("WinRAR took too long.");
        }
        if (process.ExitCode != 0)
        {
            string reason = process.ExitCode switch
            {
                3 => "the archive is damaged (CRC error)",
                10 => "no files to unpack",
                11 => "it needs a password",
                _ => $"WinRAR error {process.ExitCode}",
            };
            string detail = (error.Result + " " + output).Trim();
            throw new IOException(detail.Length > 0 ? $"{reason}: {detail.ReplaceLineEndings(" ")}" : reason);
        }
    }
}
