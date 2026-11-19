using System.IO;

namespace DirectoryFable.Infrastructure;

/// <summary>Which shell to start: the quick bar's Terminal and GemmaChat's commands use the same PowerShell.</summary>
public static class Shells
{
    /// <summary>
    /// PowerShell 7 (pwsh) when installed, else Windows PowerShell. The MSI installs it in Program Files; the
    /// Store / winget package only gives the app alias in the user's WindowsApps folder.
    /// </summary>
    public static (string Exe, string Name) PowerShell()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string pwsh in new[]
                 {
                     Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"),
                     Path.Combine(localAppData, "Microsoft", "WindowsApps", "pwsh.exe"),
                 })
        {
            if (File.Exists(pwsh))
                return (pwsh, "PowerShell 7");
        }
        return ("powershell.exe", "Windows PowerShell");
    }
}
