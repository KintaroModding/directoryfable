using System.IO;
using DirectoryFable.Models;

namespace DirectoryFable.Infrastructure;

public static class PathHelper
{
    /// <summary>
    /// Turns user input into a clean absolute folder path:
    /// expands %VARS%, strips quotes, "C:" becomes "C:\", trailing "\" removed except on roots.
    /// Relative input ("..", "sub\dir") is resolved against <paramref name="basePath"/>.
    /// A bare server ("\\SERVER") stays as it is (it lists the server's shares).
    /// </summary>
    public static string Normalize(string input, string? basePath = null)
    {
        string path = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
        if (IsThisPc(path))
            return ThisPc;

        // "\\SERVER" is not a folder; Path.GetFullPath rejects it ("should be \\server\share").
        if (IsServerPath(path))
            return path.TrimEnd('\\');

        // "C:" alone would resolve to the current directory of drive C; we want the root.
        if (path.Length == 2 && path[1] == ':')
            path += "\\";

        path = !Path.IsPathFullyQualified(path) && !string.IsNullOrEmpty(basePath) && !IsServerPath(basePath)
            ? Path.GetFullPath(path, basePath)
            : Path.GetFullPath(path);

        string? root = Path.GetPathRoot(path);
        if (root is not null && path.Length > root.Length)
            path = path.TrimEnd('\\');

        return path;
    }

    /// <summary>
    /// "This PC": the drives (Views/DrivesView). It is no folder, so it has its own "path"; typing "This PC",
    /// "shell:MyComputerFolder" or Windows' id for it in the address bar opens it too.
    /// </summary>
    public const string ThisPc = "This PC";

    public static bool IsThisPc(string path) =>
        path.Equals(ThisPc, StringComparison.OrdinalIgnoreCase)
        || path.Equals("shell:MyComputerFolder", StringComparison.OrdinalIgnoreCase)
        || path.Equals("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", StringComparison.OrdinalIgnoreCase);

    /// <summary>"C:\" (a drive's root folder).</summary>
    public static bool IsDriveRoot(string path) =>
        path.Length is 2 or 3 && path[1] == ':' && char.IsAsciiLetter(path[0]) && (path.Length == 2 || path[2] == '\\');

    public static bool IsSamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Last part of a path: "C:\a\b" → "b", "\\server\share" → "share", "\\SERVER" → "\\SERVER",
    /// "C:\" → "C:". (Path.GetFileName returns "" for a share root.)
    /// </summary>
    public static string GetName(string path)
    {
        if (IsThisPc(path))
            return ThisPc;
        string trimmed = path.TrimEnd('\\');
        if (IsServerPath(trimmed))
            return trimmed;
        int slash = trimmed.LastIndexOf('\\');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>True for "\\SERVER" (a network computer, not a share or folder).</summary>
    public static bool IsServerPath(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        string rest = path[2..].TrimEnd('\\');
        return rest.Length > 0 && !rest.Contains('\\');
    }

    /// <summary>
    /// Parent folder, or null at This PC or a server. A drive root goes up to This PC (as in Explorer); a share
    /// root ("\\server\share") goes up to its server ("\\server").
    /// </summary>
    public static string? GetParent(string path)
    {
        if (string.IsNullOrEmpty(path) || IsServerPath(path) || IsThisPc(path))
            return null;
        if (IsDriveRoot(path))
            return ThisPc;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && IsSamePath(Path.GetPathRoot(path) ?? "", path))
            return path.TrimEnd('\\')[..path.TrimEnd('\\').LastIndexOf('\\')];
        return Path.GetDirectoryName(path);
    }

    /// <summary>"T:\work\docs" → "(T:) docs", "C:\" → "(C:)", UNC "\\server\share\x" → "x", "\\SERVER" stays.</summary>
    public static string GetTabTitle(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        if (IsThisPc(path))
            return ThisPc;
        if (IsServerPath(path))
            return path;

        string? root = Path.GetPathRoot(path);
        string name = Path.GetFileName(path.TrimEnd('\\'));

        // Share root "\\server\share": the share name (GetFileName returns "" there).
        if (string.IsNullOrEmpty(name) && path.StartsWith(@"\\", StringComparison.Ordinal))
            return path.TrimEnd('\\')[(path.TrimEnd('\\').LastIndexOf('\\') + 1)..];
        bool isDrive = root is { Length: >= 2 } && root[1] == ':';

        if (!isDrive)
            return string.IsNullOrEmpty(name) ? path : name;

        string drive = $"({root![..2].ToUpperInvariant()})";
        return path.Length <= root.Length ? drive : $"{drive} {name}";
    }

    /// <summary>"D:\Work\Docs" → [D:] [Work] [Docs]. UNC: [\\server] [share] [folder] ...</summary>
    public static List<BreadcrumbSegment> GetSegments(string path)
    {
        var list = new List<BreadcrumbSegment>();
        if (IsThisPc(path))
        {
            list.Add(new BreadcrumbSegment(ThisPc, ThisPc));
            return list;
        }
        if (IsServerPath(path))
        {
            list.Add(new BreadcrumbSegment(path.TrimEnd('\\'), path.TrimEnd('\\')));
            return list;
        }

        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return list;

        string current;
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // Server first, then the share: both clickable.
            string server = root.TrimEnd('\\')[..root.TrimEnd('\\').LastIndexOf('\\')];
            string share = root.TrimEnd('\\')[(server.Length + 1)..];
            list.Add(new BreadcrumbSegment(server, server));
            list.Add(new BreadcrumbSegment(share, root.TrimEnd('\\')));
            current = root;
        }
        else
        {
            list.Add(new BreadcrumbSegment(root.TrimEnd('\\'), root));
            current = root;
        }

        foreach (string part in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            list.Add(new BreadcrumbSegment(part, current));
        }

        return list;
    }
}
