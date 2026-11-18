using System.Reflection;

namespace DirectoryFable.Updates;

/// <summary>Where DirectoryFable looks for new versions: the releases of its GitHub repository.</summary>
internal static class UpdateSource
{
    public const string Repository = "KintaroModding/directoryfable";

    public const string ProjectUrl = "https://github.com/" + Repository;

    public const string ReleasesPageUrl = ProjectUrl + "/releases";

    /// <summary>GitHub's list of published releases, newest first.</summary>
    public const string ReleasesApiUrl = "https://api.github.com/repos/" + Repository + "/releases?per_page=30";

    /// <summary>The zip of a version on its release, as build.ps1 names it: "DirectoryFable_1.2.0.zip".</summary>
    public static string AssetName(string version) => $"DirectoryFable_{version}.zip";

    /// <summary>
    /// Another address for the release list, from <c>--update-feed=</c> (tests). Its links may use http when it
    /// is on this computer.
    /// </summary>
    public static string? FeedOverride { get; set; }

    public static string FeedUrl => FeedOverride ?? ReleasesApiUrl;

    /// <summary>This build's version, such as "1.2.0" (Version in DirectoryFable.csproj).</summary>
    public static string CurrentVersion { get; } =
        typeof(UpdateSource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>
    /// The app runs as the published single-file exe (build.ps1), so it can put a new exe in its place. A
    /// development build (an exe next to its dll) cannot: its updates come from the release page.
    /// </summary>
    public static bool CanUpdateItself
    {
        get
        {
#pragma warning disable IL3000 // Empty in a single-file app: that is exactly what is asked here.
            return Environment.ProcessPath is not null && string.IsNullOrEmpty(typeof(UpdateSource).Assembly.Location);
#pragma warning restore IL3000
        }
    }
}
