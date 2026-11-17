namespace DirectoryFable.Models;

/// <summary>One clickable part of the address bar path. The last one shows a "›" after it.</summary>
public sealed record BreadcrumbSegment(string Name, string Path, bool IsLast = false);
