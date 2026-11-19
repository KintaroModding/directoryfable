using System.Windows.Threading;
using DirectoryFable.Infrastructure;
using DirectoryFable.Models;
using DirectoryFable.ViewModels;

namespace DirectoryFable;

/// <summary>
/// "Compare" in the action bar: while it is on, the two panes' shown folders are compared by name (not their
/// subfolders) and every row gets a mark: "only here" (missing on the other side), "newer" / "older" (the same
/// name, another change time; 2 seconds and less count as the same, as FAT drives store times that coarsely) or
/// "differs" (the same time, another size, or a file against a folder). Folders are compared by their name only.
/// Turning it on also selects, in each pane, what is only there or newer there (what one would copy over). The
/// marks follow changes: they are counted again every second while it is on.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan SameTime = TimeSpan.FromSeconds(2);
    private readonly DispatcherTimer _compareTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TabViewModel? _comparedLeft, _comparedRight;
    private string _compareStatus = "";

    private void InitCompare()
    {
        _compareTimer.Tick += (_, _) => CompareNow(select: false);
        Actions.CompareToggled += on =>
        {
            if (on)
            {
                _compareStatus = "";
                CompareNow(select: true);
                _compareTimer.Start();
            }
            else
            {
                _compareTimer.Stop();
                ClearMarks(_comparedLeft);
                ClearMarks(_comparedRight);
                _comparedLeft = _comparedRight = null;
                foreach (var tab in new[] { _left.SelectedTab, _right.SelectedTab })
                    tab?.ShowStatus("");
            }
        };
    }

    private void CompareNow(bool select)
    {
        var left = _left.SelectedTab;
        var right = _right.SelectedTab;
        // Other tabs than last time: their marks go.
        if (!ReferenceEquals(left, _comparedLeft))
            ClearMarks(_comparedLeft);
        if (!ReferenceEquals(right, _comparedRight))
            ClearMarks(_comparedRight);
        _comparedLeft = left;
        _comparedRight = right;
        if (left is null || right is null)
            return;

        bool usable = IsFolder(left.CurrentPath) && IsFolder(right.CurrentPath);
        if (!usable || PathHelper.IsSamePath(left.CurrentPath, right.CurrentPath))
        {
            ClearMarks(left);
            ClearMarks(right);
            Report(left, right, usable ? "Compare: both panes show the same folder." : "Compare needs a folder in both panes.");
            return;
        }

        var leftCounts = Mark(left.Items, right.Items);
        var rightCounts = Mark(right.Items, left.Items);
        string key = $"{left.CurrentPath}|{right.CurrentPath}|{leftCounts}|{rightCounts}";
        if (key != _compareStatus)
        {
            _compareStatus = key;
            left.ShowStatus(Summary(leftCounts));
            right.ShowStatus(Summary(rightCounts));
        }
        if (select)
        {
            LeftPane.SelectItems([.. left.Items.Where(i => i.CompareMark is "only" or "newer")]);
            RightPane.SelectItems([.. right.Items.Where(i => i.CompareMark is "only" or "newer")]);
        }
    }

    private void Report(TabViewModel left, TabViewModel right, string text)
    {
        if (text == _compareStatus)
            return;
        _compareStatus = text;
        left.ShowStatus(text);
        right.ShowStatus(text);
    }

    private static bool IsFolder(string path) =>
        path.Length > 0 && !PathHelper.IsThisPc(path) && !PathHelper.IsServerPath(path);

    private readonly record struct Counts(int Only, int Newer, int Older, int Differs, int Same);

    private static string Summary(Counts c) =>
        $"Compare: {c.Only:N0} only here, {c.Newer:N0} newer, {c.Older:N0} older, {c.Differs:N0} differ, {c.Same:N0} the same";

    /// <summary>Marks the items of one pane against the other pane's items.</summary>
    private static Counts Mark(IEnumerable<FileItem> mine, IEnumerable<FileItem> theirs)
    {
        var others = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in theirs)
            others.TryAdd(item.Name, item);
        int only = 0, newer = 0, older = 0, differs = 0, same = 0;
        foreach (var item in mine)
        {
            string mark;
            if (!others.TryGetValue(item.Name, out var other))
                mark = "only";
            else if (item.IsDirectory || other.IsDirectory)
                mark = item.IsDirectory == other.IsDirectory ? "" : "differs";
            else
            {
                var difference = item.DateModified - other.DateModified;
                mark = difference > SameTime ? "newer"
                    : difference < -SameTime ? "older"
                    : item.Size == other.Size ? "" : "differs";
            }
            item.CompareMark = mark;
            switch (mark)
            {
                case "only": only++; break;
                case "newer": newer++; break;
                case "older": older++; break;
                case "differs": differs++; break;
                default: same++; break;
            }
        }
        return new Counts(only, newer, older, differs, same);
    }

    private static void ClearMarks(TabViewModel? tab)
    {
        if (tab is null)
            return;
        foreach (var item in tab.Items)
            item.CompareMark = "";
    }
}
