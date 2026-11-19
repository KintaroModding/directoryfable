using System.IO;
using System.Collections.ObjectModel;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.ViewModels;

/// <summary>One side of the window (left or right): a strip of tabs, one of them visible.</summary>
public sealed class PaneViewModel : ObservableObject
{
    private TabViewModel? _selectedTab;
    private bool _isActive;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab is not null)
                _selectedTab.PropertyChanged -= SelectedTab_PropertyChanged;
            if (SetField(ref _selectedTab, value))
            {
                UpdateTabFlags();
                CurrentFolderChanged?.Invoke(this, EventArgs.Empty);
            }
            if (_selectedTab is not null)
                _selectedTab.PropertyChanged += SelectedTab_PropertyChanged;
        }
    }

    /// <summary>The visible tab changed or navigated: its folder is different now (the folder tree follows).</summary>
    public event EventHandler? CurrentFolderChanged;

    public string? CurrentFolder => SelectedTab?.CurrentPath;

    private void SelectedTab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.CurrentPath))
            CurrentFolderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when this pane has the keyboard (the other pane is inactive).</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetField(ref _isActive, value))
                UpdateTabFlags();
        }
    }

    /// <summary>Opens a new tab. By default it goes right after the current tab and becomes visible.</summary>
    /// <param name="configure">Sets up the tab (e.g. restored sort order) before its list is created.</param>
    public async Task<TabViewModel> AddTabAsync(string path, bool select = true, int? index = null,
        Action<TabViewModel>? configure = null)
    {
        var tab = new TabViewModel();
        // A new tab looks like the current one of this pane (columns); configure may override.
        tab.ColumnLayout = CopyColumns(SelectedTab?.ColumnLayout);
        configure?.Invoke(tab);
        int at = index ?? (SelectedTab is null ? Tabs.Count : Tabs.IndexOf(SelectedTab) + 1);
        Tabs.Insert(Math.Clamp(at, 0, Tabs.Count), tab);

        if (select || SelectedTab is null)
            SelectedTab = tab;
        else
            UpdateTabFlags();

        await tab.NavigateAsync(path);
        return tab;
    }

    // ---------- Session ----------

    public PaneState CaptureState() => new()
    {
        Tabs = [.. Tabs.Where(t => t.CurrentPath.Length > 0).Select(CaptureTab)],
        SelectedIndex = SelectedTab is null ? 0 : Math.Max(0, Tabs.IndexOf(SelectedTab)),
    };

    private static TabState CaptureTab(TabViewModel t) => new()
    {
        Path = t.CurrentPath,
        SortColumn = t.SortColumn.ToString(),
        SortDescending = t.SortDescending,
        GroupByType = t.GroupByType,
        Columns = CopyColumns(t.ColumnLayout),
        CollapsedGroups = t.CollapsedGroups.Count > 0 ? [.. t.CollapsedGroups] : null,
        Space = t.SpaceMode,
        Tiles = t.TileMode,
    };

    /// <summary>Sort order, columns and closed groups of a saved tab (before its list is created).</summary>
    private static void ApplyTab(TabViewModel t, TabState saved)
    {
        t.SortColumn = Enum.TryParse<SortColumn>(saved.SortColumn, out var column) ? column : SortColumn.Name;
        t.SortDescending = saved.SortDescending;
        t.GroupByType = saved.GroupByType;
        t.ColumnLayout = CopyColumns(saved.Columns);
        foreach (string group in saved.CollapsedGroups ?? [])
            t.CollapsedGroups.Add(group);
        t.SpaceMode = saved.Space;
        t.TileMode = saved.Tiles;
    }

    // ---------- Closed tabs (Ctrl+Shift+T) ----------

    /// <summary>A tab was closed: its state and former position, so it can be reopened.</summary>
    public event Action<PaneViewModel, TabState, int>? TabClosed;

    private void NoteClosed(TabViewModel tab, int index)
    {
        if (tab.CurrentPath.Length > 0)
            TabClosed?.Invoke(this, CaptureTab(tab), index);
    }

    /// <summary>Reopens a closed tab at its old position (a folder that is gone: its nearest parent).</summary>
    public async Task ReopenTabAsync(TabState saved, int index)
    {
        var tab = await AddTabAsync(saved.Path, select: true, index: index, configure: t => ApplyTab(t, saved));
        if (tab.CurrentPath.Length == 0 && await Task.Run(() => NearestExisting(saved.Path)) is { } existing)
            await tab.NavigateAsync(existing);
    }

    /// <summary>
    /// Reopens saved tabs. A folder that is gone (or an offline share) opens its nearest
    /// existing parent instead; with nothing usable, <paramref name="fallback"/>.
    /// </summary>
    public async Task RestoreAsync(PaneState? state, string fallback)
    {
        var tabs = state?.Tabs.Where(t => !string.IsNullOrWhiteSpace(t.Path)).ToList() ?? [];
        if (tabs.Count == 0)
        {
            await AddTabAsync(fallback);
            return;
        }

        var opened = new List<TabViewModel>();
        foreach (var saved in tabs)
        {
            var tab = await AddTabAsync(saved.Path, select: opened.Count == 0, index: opened.Count,
                configure: t => ApplyTab(t, saved));
            if (tab.CurrentPath.Length == 0)
            {
                string? existing = await Task.Run(() => NearestExisting(saved.Path));
                await tab.NavigateAsync(existing ?? fallback);
            }
            opened.Add(tab);
        }
        SelectedTab = opened[Math.Clamp(state!.SelectedIndex, 0, opened.Count - 1)];
    }

    private static List<ColumnState>? CopyColumns(List<ColumnState>? columns) =>
        columns?.Select(c => new ColumnState { Key = c.Key, Width = c.Width }).ToList();

    private static string? NearestExisting(string path)
    {
        for (string? p = PathHelper.GetParent(path); p is not null; p = PathHelper.GetParent(p))
        {
            if (Directory.Exists(p) || PathHelper.IsThisPc(p)) // A drive that is gone: This PC.
                return p;
        }
        return null;
    }

    /// <summary>New tab with the same folder as the current one (Ctrl+T, "+" button).</summary>
    public Task<TabViewModel> DuplicateTabAsync(TabViewModel? source = null)
    {
        source ??= SelectedTab;
        string path = source?.CurrentPath is { Length: > 0 } p
            ? p
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        int index = source is null ? Tabs.Count : Tabs.IndexOf(source) + 1;
        return AddTabAsync(path, select: true, index);
    }

    /// <summary>Closes a tab. The last tab of a pane cannot be closed.</summary>
    public void CloseTab(TabViewModel tab)
    {
        int index = Tabs.IndexOf(tab);
        if (index < 0 || Tabs.Count <= 1)
            return;

        NoteClosed(tab, index);
        Tabs.RemoveAt(index);
        tab.Dispose();
        if (SelectedTab == tab)
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    public void CloseOtherTabs(TabViewModel keep)
    {
        foreach (var tab in Tabs.Where(t => t != keep).ToList())
        {
            NoteClosed(tab, Tabs.IndexOf(tab));
            Tabs.Remove(tab);
            tab.Dispose();
        }
        SelectedTab = keep;
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: next or previous tab, wrapping around.</summary>
    public void SelectNextTab(int direction)
    {
        if (Tabs.Count < 2 || SelectedTab is null)
            return;
        int index = Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(index + direction + Tabs.Count) % Tabs.Count];
    }

    public void MoveTab(TabViewModel tab, int newIndex)
    {
        int oldIndex = Tabs.IndexOf(tab);
        newIndex = Math.Clamp(newIndex, 0, Tabs.Count - 1);
        if (oldIndex >= 0 && oldIndex != newIndex)
            Tabs.Move(oldIndex, newIndex);
    }

    private void UpdateTabFlags()
    {
        foreach (var tab in Tabs)
        {
            tab.IsSelected = tab == SelectedTab;
            tab.IsActive = IsActive && tab == SelectedTab;
        }
    }
}
