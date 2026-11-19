using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DirectoryFable.Infrastructure;
using DirectoryFable.Vault;
using Microsoft.Win32;

namespace DirectoryFable.Views;

/// <summary>
/// KeeSeek, the sixth title bar view: passwords in an encrypted vault (<see cref="VaultFile"/>, data\keeseek).
/// Locked it shows one card: the master password (the first time: a new one, twice). Open: categories on the left
/// ("All entries" first), the chosen category's entries as cards; a click on a user name or password copies it
/// (<see cref="SecureClipboard"/>: out of the clipboard history, cleared after the set seconds), a click on a web
/// address opens it. Typing anywhere searches all entries. Every change is saved at once. It locks by itself after
/// the set minutes without use, or - with the timer off - when KeeSeek is left; always when Windows locks and when
/// DirectoryFable ends.
/// </summary>
public partial class KeeSeekView : UserControl
{
    private VaultFile? _file;
    private VaultData? _data;
    private string? _categoryId; // null = all entries.
    private VaultEntry? _editing;
    private bool _editingNew;

    private int _lockMinutes = 10;
    private int _clipboardSeconds = 20;
    private DateTime _lastUse = DateTime.UtcNow;
    private readonly DispatcherTimer _lockTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private string _copiedWhat = "";
    private bool _busy;

    public KeeSeekView()
    {
        InitializeComponent();
        ApplySettings(AppSettings.Load());
        _lockTimer.Tick += (_, _) => CheckAutoLock();
        _lockTimer.Start();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        SecureClipboard.Countdown += OnClipboardCountdown;
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
                Dispatcher.BeginInvoke(Lock); // Windows locks: KeeSeek too.
        };
        SettingsPage.CloseRequested += () => ShowSettings(false);
        SettingsPage.SettingsChanged += ApplySettings;
        SettingsPage.ChangePassword = ChangeMasterPasswordAsync;
        Loaded += (_, _) => IconFrames.Sharpen(LockIcon); // The icon's 64 px picture, not the 16 px one enlarged.
        ShowLocked();
    }

    /// <summary>The title text changed (locked / open).</summary>
    public event Action? ActiveChanged;

    public bool IsUnlocked => _file is not null;

    /// <summary>
    /// The chosen category's id (null = All entries); kept while locked and in the session, so KeeSeek opens on it
    /// again. A category that is gone falls back to All entries.
    /// </summary>
    public string? CategoryId
    {
        get => _categoryId;
        set => _categoryId = value;
    }

    public string TitleText => IsUnlocked ? "KeeSeek" : "KeeSeek (locked)";

    private string VaultPath => VaultFile.DefaultPath;

    public void ApplySettings(AppSettings settings)
    {
        _lockMinutes = Math.Clamp(settings.KeeSeekLockMinutes, 0, 60);
        _clipboardSeconds = Math.Clamp(settings.KeeSeekClipboardSeconds, 5, 120);
        _cardsView = settings.KeeSeekCards;
        ShowViewButtons();
        ApplyRowHeight(Math.Clamp(settings.KeeSeekRowHeight, MinRowHeight, MaxRowHeight));
    }

    public const int MinRowHeight = 16;
    public const int MaxRowHeight = 88;

    /// <summary>
    /// The rows' height (DIP, 16-88): the content gets what the frame (margins, border) and the padding leave; the
    /// avatar follows; below 48 the category line under the title goes (it would not fit). Below 28 the rows are
    /// compact, like a classic list: no gaps, no avatar, the text one size smaller.
    /// </summary>
    private void ApplyRowHeight(int height)
    {
        bool compact = height < 28;
        double frame = (compact ? 0 : 3) + 2; // Margins 1 + 2 (none when compact), border 1 + 1.
        double content = compact ? height - frame : Math.Max(20, height - 19);
        double padding = Math.Max(0, (height - frame - content) / 2);
        double avatar = Math.Clamp(content - 3, 18, 40);
        Resources["RowMargin"] = compact ? new Thickness(0) : new Thickness(0, 1, 0, 2);
        Resources["RowContentHeight"] = content;
        Resources["RowAvatarSize"] = avatar;
        Resources["RowAvatarCorner"] = new CornerRadius(avatar / 2);
        Resources["RowAvatarVisibility"] = compact ? Visibility.Collapsed : Visibility.Visible;
        Resources["RowAvatarColumn"] = new GridLength(compact ? 8 : 44);
        Resources["RowPadding"] = new Thickness(12, padding, 12, padding);
        Resources["RowSubtitleVisibility"] = content >= 29 ? Visibility.Visible : Visibility.Collapsed;
        Resources["RowTitleSize"] = compact ? 12.0 : 14.0;
        Resources["RowFontSize"] = compact ? 12.0 : 13.0;

        // A divider: as high as an entry (user: "dividers normal size is better"), only its title.
        Resources["DividerContentHeight"] = content;
        Resources["DividerPadding"] = new Thickness(12, padding, 12, padding);
        Resources["DividerTitleSize"] = compact ? 12.0 : 14.0;
    }

    // ---------- Rows or cards ----------

    private bool _cardsView;

    private void ShowViewButtons()
    {
        RowsViewButton.IsChecked = !_cardsView;
        CardsViewButton.IsChecked = _cardsView;
    }

    private void RowsView_Click(object sender, RoutedEventArgs e) => SetCardsView(false);

    private void CardsView_Click(object sender, RoutedEventArgs e) => SetCardsView(true);

    private void SetCardsView(bool cards)
    {
        bool changed = cards != _cardsView;
        _cardsView = cards;
        ShowViewButtons(); // Also when the shown one was clicked: it stays checked.
        if (!changed)
            return;
        ShowCards();
        try
        {
            AppSettings.Update(s => s.KeeSeekCards = cards);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"Cannot save the setting: {ex.Message}");
        }
    }

    private EntryCard? SelectedRow => RowsList.SelectedItem as EntryCard;

    /// <summary>Rows: Enter edits; Ctrl+B copies the user name, Ctrl+C the password, Ctrl+U opens the web address.</summary>
    private void RowsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (SelectedRow is not { } row)
            return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Alt && e.Key == Key.System && e.SystemKey is Key.Up or Key.Down)
        {
            e.Handled = true;
            if (SearchBox.Text.Trim().Length == 0)
                MoveEntry(row.Entry, e.SystemKey == Key.Up ? -1 : 1);
            return;
        }
        switch (e.Key)
        {
            case Key.Enter when modifiers == ModifierKeys.None:
                OpenEditor(row.Entry);
                break;
            case Key.B when modifiers == ModifierKeys.Control && row.HasUser:
                Copy(row.Entry.UserName, $"User name of \"{row.Title}\"");
                break;
            case Key.C when modifiers == ModifierKeys.Control && row.HasPassword:
                Copy(row.Entry.Password, $"Password of \"{row.Title}\"");
                break;
            case Key.U when modifiers == ModifierKeys.Control && row.HasUrl:
                OpenUrl(row);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---------- Shown / hidden (main window) ----------

    /// <summary>The view is shown: the master password box or the search gets the focus.</summary>
    public void OnShown()
    {
        _lastUse = DateTime.UtcNow;
        Dispatcher.BeginInvoke(FocusView, DispatcherPriority.Loaded);
    }

    /// <summary>Another view is shown: with the timer off, KeeSeek locks now.</summary>
    public void OnHidden()
    {
        if (_lockMinutes == 0)
            Lock();
    }

    public void FocusView()
    {
        if (SettingsPage.IsVisible)
            return;
        if (!IsUnlocked)
            MasterBox.Focus();
        else if (EditorLayer.IsVisible)
            TitleBox.Focus();
        else
            Focus();
    }

    /// <summary>DirectoryFable ends: the clipboard and the key go.</summary>
    public void Shutdown() => Lock();

    private void View_Used(object sender, RoutedEventArgs e) => _lastUse = DateTime.UtcNow;

    private void CheckAutoLock()
    {
        if (IsUnlocked && _lockMinutes > 0 && DateTime.UtcNow - _lastUse > TimeSpan.FromMinutes(_lockMinutes))
            Lock();
    }

    // ---------- Locked / unlocking ----------

    private bool CreateMode => !VaultFile.Exists(VaultPath);

    private void ShowLocked()
    {
        MainArea.Visibility = Visibility.Collapsed;
        EditorLayer.Visibility = Visibility.Collapsed;
        LockPanel.Visibility = Visibility.Visible;
        NewEntryButton.IsEnabled = ImportButton.IsEnabled = LockButton.IsEnabled = false;
        SearchArea.Visibility = Visibility.Hidden;
        BarText.Text = "";
        bool create = CreateMode;
        LockTitle.Text = create ? "Create your vault" : "Locked";
        LockHint.Text = create
            ? "Choose a master password. It is the only key to your passwords: if you forget it, nothing can bring them back. " +
              "Use a long one (a sentence works well) that you use nowhere else."
            : "Enter your master password to open your passwords.";
        RepeatRow.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
        UnlockButton.Content = create ? "Create vault" : "Unlock";
        LockFile.Text = create
            ? $"The vault will be saved encrypted (AES-256) in {VaultPath}. Nothing leaves this PC."
            : $"Vault: {VaultPath}";
        LockError.Visibility = Visibility.Collapsed;
        MasterBox.Clear();
        RepeatBox.Clear();
        MasterStrength.Text = "";
    }

    private void MasterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        if (sender == MasterBox && CreateMode && RepeatBox.Password.Length == 0)
            RepeatBox.Focus();
        else
            _ = UnlockAsync();
    }

    private void MasterBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!CreateMode)
            return;
        double bits = PasswordGenerator.Bits(MasterBox.Password);
        MasterStrength.Text = MasterBox.Password.Length == 0 ? "" : $"Strength: {PasswordGenerator.Rating(bits)}";
    }

    private void Unlock_Click(object sender, RoutedEventArgs e) => _ = UnlockAsync();

    private async Task UnlockAsync()
    {
        if (_busy)
            return;
        string password = MasterBox.Password;
        bool create = CreateMode;
        if (password.Length == 0)
        {
            ShowLockError("Enter the master password.");
            return;
        }
        if (create && password != RepeatBox.Password)
        {
            ShowLockError("The two passwords are not the same.");
            return;
        }

        _busy = true;
        UnlockButton.IsEnabled = false;
        string label = (string)UnlockButton.Content;
        UnlockButton.Content = create ? "Creating..." : "Opening...";
        LockError.Visibility = Visibility.Collapsed;
        try
        {
            if (create)
            {
                (_file, _data) = await VaultFile.CreateAsync(VaultPath, password);
            }
            else if (await VaultFile.OpenAsync(VaultPath, password) is { } opened)
            {
                (_file, _data) = opened;
            }
            else
            {
                ShowLockError("Wrong master password.");
                MasterBox.SelectAll();
                MasterBox.Focus();
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowLockError(ex.Message);
            return;
        }
        finally
        {
            _busy = false;
            UnlockButton.IsEnabled = true;
            UnlockButton.Content = label;
        }
        MasterBox.Clear();
        RepeatBox.Clear();
        _lastUse = DateTime.UtcNow;
        ShowOpen();
        ActiveChanged?.Invoke();
    }

    private void ShowLockError(string text)
    {
        LockError.Text = text;
        LockError.Visibility = Visibility.Visible;
    }

    /// <summary>Locks: the key is wiped, the entries leave memory and the view, KeeSeek's copy leaves the clipboard.</summary>
    public void Lock()
    {
        SecureClipboard.ClearIfOurs();
        if (_file is null)
            return;
        _file.Close();
        _file = null;
        _data = null;
        _editing = null;
        Cards.ItemsSource = null;
        RowsList.ItemsSource = null;
        CategoryList.ItemsSource = null;
        CategoryBox.ItemsSource = null;
        SearchBox.Clear();
        PasswordEdit.Clear();
        PasswordPlain.Clear();
        ShowSettings(false);
        ShowLocked();
        ActiveChanged?.Invoke();
        if (IsVisible)
            Dispatcher.BeginInvoke(FocusView, DispatcherPriority.Loaded);
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => Lock();

    // ---------- Open: categories and cards ----------

    private void ShowOpen()
    {
        LockPanel.Visibility = Visibility.Collapsed;
        MainArea.Visibility = Visibility.Visible;
        NewEntryButton.IsEnabled = ImportButton.IsEnabled = LockButton.IsEnabled = true;
        SearchArea.Visibility = Visibility.Visible;
        ShowCategories();
        ShowCards();
        Dispatcher.BeginInvoke(FocusView, DispatcherPriority.Loaded);
    }

    /// <summary>A row of the category list (Id null: all entries).</summary>
    public sealed record CategoryRow(string? Id, string Name, int Count, string Icon);

    private void ShowCategories()
    {
        if (_data is null)
            return;
        // Dividers are no entries: not counted.
        var rows = new List<CategoryRow> { new(null, "All entries", _data.Entries.Count(e => !e.IsDivider), CategoryIcons.AllEntries) };
        rows.AddRange(_data.Categories.Select(c => new CategoryRow(c.Id, c.Name, _data.Entries.Count(e => e.CategoryId == c.Id && !e.IsDivider),
            CategoryIcons.For(c.Icon, c.Name))));
        if (_categoryId is not null && rows.All(r => r.Id != _categoryId))
            _categoryId = null;
        _fillingCategories = true;
        try
        {
            CategoryList.ItemsSource = rows;
            CategoryList.SelectedItem = rows.First(r => r.Id == _categoryId);
        }
        finally
        {
            _fillingCategories = false;
        }
        BarText.Text = $"{_data.Entries.Count(e => !e.IsDivider):N0} entries · {Path.GetFileName(VaultPath)}";
    }

    private bool _fillingCategories;

    /// <summary>A click on a category shows its entries (and ends a search).</summary>
    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingCategories || CategoryList.SelectedItem is not CategoryRow row)
            return;
        _categoryId = row.Id;
        if (SearchBox.Text.Length > 0)
            SearchBox.Clear(); // Picking a category ends the search.
        else
            ShowCards();
    }

    private string CategoryName(string id) => _data?.Categories.FirstOrDefault(c => c.Id == id)?.Name ?? "";

    private void ShowCards()
    {
        if (_data is null)
            return;
        string query = SearchBox.Text.Trim();
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        IEnumerable<VaultEntry> entries;
        bool showCategory;
        if (query.Length > 0)
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            entries = _data.Entries.Where(e => !e.IsDivider && words.All(w => Matches(e, w)));
            HeaderText.Text = $"Search: {query}";
            showCategory = true;
        }
        else if (_categoryId is null)
        {
            entries = _data.Entries;
            HeaderText.Text = "All entries";
            showCategory = true;
        }
        else
        {
            entries = _data.Entries.Where(e => e.CategoryId == _categoryId);
            HeaderText.Text = CategoryName(_categoryId);
            showCategory = false;
        }
        // The user's order: categories as in the list, entries as placed (the vault keeps them in that order).
        var categoryOrder = _data.Categories.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var cards = entries
            .Select((e, i) => (Entry: e, Index: i))
            .OrderBy(x => categoryOrder.GetValueOrDefault(x.Entry.CategoryId, int.MaxValue))
            .ThenBy(x => x.Index)
            .Select(x => new EntryCard(x.Entry, CategoryName(x.Entry.CategoryId), showCategory))
            .ToList();
        Cards.ItemsSource = _cardsView ? cards : null;
        RowsList.ItemsSource = _cardsView ? null : cards;
        CardsScroller.Visibility = _cardsView ? Visibility.Visible : Visibility.Collapsed;
        RowsPanel.Visibility = _cardsView || cards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        int count = cards.Count(c => c.IsEntry);
        HeaderCount.Text = count == 1 ? "1 entry" : $"{count:N0} entries";
        EmptyText.Text = query.Length > 0 ? "Nothing found."
            : _data.Entries.Count == 0 ? "No entries yet.\nAdd one with New entry (Ctrl+N), or bring your passwords in with Import CSV."
            : "No entries in this category yet.\nAdd one with New entry (Ctrl+N).";
        EmptyText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool Matches(VaultEntry e, string word) =>
        e.Title.Contains(word, StringComparison.CurrentCultureIgnoreCase)
        || e.UserName.Contains(word, StringComparison.CurrentCultureIgnoreCase)
        || e.Url.Contains(word, StringComparison.CurrentCultureIgnoreCase)
        || e.Notes.Contains(word, StringComparison.CurrentCultureIgnoreCase)
        || CategoryName(e.CategoryId).Contains(word, StringComparison.CurrentCultureIgnoreCase);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ShowCards();

    /// <summary>Saves the vault; a failure is shown (the change stays in memory, the next save tries again).</summary>
    private bool Save()
    {
        if (_file is null || _data is null)
            return false;
        try
        {
            _file.Save(_data);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"Cannot save the vault: {ex.Message}", sticky: true);
            return false;
        }
    }

    // ---------- Cards ----------

    /// <summary>One entry as a card.</summary>
    public sealed class EntryCard(VaultEntry entry, string category, bool showCategory) : INotifyPropertyChanged
    {
        private static readonly Brush[] Avatars =
        [
            Frozen("#2F6FB0"), Frozen("#7A4FB0"), Frozen("#B0504F"), Frozen("#3F8F5A"),
            Frozen("#B07A2F"), Frozen("#2F8F92"), Frozen("#A0477F"), Frozen("#5F6F80"),
        ];

        private bool _revealed;

        public VaultEntry Entry { get; } = entry;
        public string Title => Entry.Title;
        public string Initial => Entry.Title.Length > 0 ? char.ToUpperInvariant(Entry.Title.TrimStart()[0]).ToString() : "?";
        public Brush Avatar => Avatars[(int)((uint)StableHash(Entry.Title) % Avatars.Length)];
        public string Category { get; } = category;
        public bool ShowCategory { get; } = showCategory;
        public string UserName => Entry.UserName;
        public bool IsDivider => Entry.IsDivider;
        public bool IsEntry => !Entry.IsDivider;
        public bool HasUser => !IsDivider && Entry.UserName.Length > 0;
        public bool HasPassword => !IsDivider && Entry.Password.Length > 0;

        /// <summary>The entry's color, or null.</summary>
        public Color? HighlightColor => TryColor(Entry.Color);

        /// <summary>The row's background: a light tint of the color (a divider: a strong one); transparent without color.</summary>
        public Brush RowBrush => HighlightColor is { } c
            ? Frozen(Color.FromArgb(IsDivider ? (byte)0xB0 : (byte)0x30, c.R, c.G, c.B))
            : Brushes.Transparent;

        /// <summary>The thin stripe on the left of a colored entry (not on a divider).</summary>
        public Brush StripeBrush => HighlightColor is { } c && !IsDivider ? Frozen(c) : Brushes.Transparent;

        /// <summary>A card's frame: the color (a colored entry that is no divider).</summary>
        public Brush? CardBorder => HighlightColor is { } c ? Frozen(c) : null;
        public bool HasColorFrame => HighlightColor is not null && !IsDivider;

        /// <summary>A divider without a color is shown in a quiet gray.</summary>
        public Brush DividerBrush => HighlightColor is { } c ? Frozen(Color.FromArgb(0xB0, c.R, c.G, c.B)) : Frozen(Color.FromArgb(0xFF, 0x30, 0x30, 0x30));

        public static Color? TryColor(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            try
            {
                return (Color)ColorConverter.ConvertFromString(text.Trim());
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        public string PasswordText => _revealed ? Entry.Password : new string('•', 12);
        public string RevealIcon => _revealed ? "" : "";
        public string Url => Entry.Url;
        public bool HasUrl => !IsDivider && Entry.Url.Length > 0;
        public string NotesPreview => Entry.Notes.ReplaceLineEndings(" ").Trim();
        public bool HasNotes => !IsDivider && NotesPreview.Length > 0;
        public string ChangedShort => Entry.Modified.ToLocalTime().ToString("d");
        public string ChangedText => $"Changed {Entry.Modified.ToLocalTime():d}" +
            (Entry.PasswordChanged < Entry.Modified.AddMinutes(-1) ? $" · password from {Entry.PasswordChanged.ToLocalTime():d}" : "");

        public bool Revealed
        {
            get => _revealed;
            set
            {
                _revealed = value;
                OnPropertyChanged(nameof(PasswordText));
                OnPropertyChanged(nameof(RevealIcon));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static Brush Frozen(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private static int StableHash(string text)
        {
            int hash = 17;
            foreach (char c in text.ToUpperInvariant())
                hash = hash * 31 + c;
            return hash;
        }
    }

    private static EntryCard? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as EntryCard;

    private void CopyUser_Click(object sender, MouseButtonEventArgs e)
    {
        if (CardOf(sender) is { } card)
            Copy(card.Entry.UserName, $"User name of \"{card.Title}\"");
        e.Handled = true;
    }

    private void CopyPassword_Click(object sender, MouseButtonEventArgs e)
    {
        if (CardOf(sender) is { } card)
            Copy(card.Entry.Password, $"Password of \"{card.Title}\"");
        e.Handled = true;
    }

    private void Copy(string text, string what)
    {
        if (!SecureClipboard.Copy(text, _clipboardSeconds))
        {
            ShowToast("The clipboard is busy (another app holds it). Try again.");
            return;
        }
        _copiedWhat = what;
    }

    private void OnClipboardCountdown(int secondsLeft)
    {
        if (secondsLeft > 0 && _copiedWhat.Length > 0)
        {
            ShowToast($"{_copiedWhat} copied · cleared from the clipboard in {secondsLeft} s", sticky: true);
        }
        else if (_copiedWhat.Length > 0)
        {
            _copiedWhat = "";
            ShowToast("Clipboard cleared");
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card)
            card.Revealed = !card.Revealed;
    }

    private void OpenUrl_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (CardOf(sender) is { } card)
            OpenUrl(card);
    }

    private void OpenUrl(EntryCard card)
    {
        string url = card.Entry.Url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            ShowToast("Only web addresses (http, https) open from here.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowToast($"Cannot open the address: {ex.Message}");
        }
    }

    private void EditCard_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card)
            OpenEditor(card.Entry);
    }

    /// <summary>A double-click on the card (not on a field that copies) edits it.</summary>
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && e.OriginalSource is not TextBlock { Cursor: not null } && CardOf(sender) is { } card)
        {
            e.Handled = true;
            OpenEditor(card.Entry);
        }
    }

    // ---------- Editor: color and divider ----------

    /// <summary>The ready colors of the editor's color choice (they read well on the dark background).</summary>
    private static readonly (string Hex, string Name)[] Palette =
    [
        ("#E05252", "Red"), ("#E8833A", "Orange"), ("#E0B43A", "Yellow"), ("#4CAF50", "Green"),
        ("#2BB3A3", "Teal"), ("#3A8EE0", "Blue"), ("#6C63E0", "Indigo"), ("#B05CD6", "Purple"),
        ("#D65C9E", "Pink"), ("#8D6E63", "Brown"), ("#7F8C8D", "Gray"), ("#C8C8C8", "Silver"),
    ];

    private string? _editorColor;
    private bool _settingColor;

    private void BuildColorSwatches()
    {
        Button Swatch(string? hex, string name, UIElement content)
        {
            var button = new Button
            {
                Style = (Style)FindResource("SubtleButton"),
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(0),
                ToolTip = name,
                Tag = hex,
                Content = content,
            };
            button.Click += (_, _) => SetEditorColor(hex);
            return button;
        }
        ColorSwatches.Children.Add(Swatch(null, "No color", new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        }));
        foreach (var (hex, name) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            ColorSwatches.Children.Add(Swatch(hex, name, new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(color),
            }));
        }
    }

    /// <summary>The editor's color: the swatches show the chosen one with a frame, the box its code, the preview it.</summary>
    private void SetEditorColor(string? hex)
    {
        _editorColor = EntryCard.TryColor(hex) is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null;
        _settingColor = true;
        ColorBox.Text = _editorColor ?? "";
        _settingColor = false;
        ShowEditorColor();
    }

    private void ShowEditorColor()
    {
        ColorPreview.Background = EntryCard.TryColor(_editorColor) is { } c ? new SolidColorBrush(c) : Brushes.Transparent;
        foreach (var button in ColorSwatches.Children.OfType<Button>())
        {
            bool chosen = string.Equals(button.Tag as string, _editorColor, StringComparison.OrdinalIgnoreCase);
            button.BorderBrush = chosen ? (Brush)FindResource("GroupChevronBrush") : Brushes.Transparent;
            button.BorderThickness = new Thickness(chosen ? 2 : 0);
        }
    }

    /// <summary>An own color typed as #RRGGBB (or a name such as "orange"); empty = no color.</summary>
    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingColor)
            return;
        string text = ColorBox.Text.Trim();
        if (text.Length > 0 && !text.StartsWith('#') && text.Length == 6 && text.All(Uri.IsHexDigit))
            text = "#" + text;
        if (text.Length == 0)
            _editorColor = null;
        else if (EntryCard.TryColor(text) is { } c)
            _editorColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        ShowEditorColor();
    }

    private void DividerBox_Click(object sender, RoutedEventArgs e) => ShowDividerFields();

    /// <summary>A divider has only a title (and its color): the other fields go while it is checked.</summary>
    private void ShowDividerFields() => EntryFields.Visibility = DividerBox.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;

    // ---------- Editor ----------

    private void NewEntry_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void OpenEditor(VaultEntry? entry)
    {
        if (_data is null)
            return;
        if (_data.Categories.Count == 0)
            _data.Categories.Add(new VaultCategory { Name = "General" });
        _editing = entry;
        _editingNew = entry is null;
        EditorTitle.Text = entry is null ? "New entry" : "Edit entry";
        TitleBox.Text = entry?.Title ?? "";
        UserBox.Text = entry?.UserName ?? "";
        SetEditorPassword(entry?.Password ?? "");
        UrlBox.Text = entry?.Url ?? "";
        NotesBox.Text = entry?.Notes ?? "";
        if (ColorSwatches.Children.Count == 0)
            BuildColorSwatches();
        SetEditorColor(entry?.Color);
        DividerBox.IsChecked = entry?.IsDivider == true;
        ShowDividerFields();
        CategoryBox.ItemsSource = _data.Categories;
        string categoryId = entry?.CategoryId ?? _categoryId ?? _data.Categories[0].Id;
        CategoryBox.SelectedItem = _data.Categories.FirstOrDefault(c => c.Id == categoryId) ?? _data.Categories[0];
        DeleteEntryButton.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
        EditorError.Visibility = Visibility.Collapsed;
        ShowPlainPassword(false);
        GeneratorButton.IsChecked = false;
        GeneratorPanel.Visibility = Visibility.Collapsed;
        ShowGeneratorLength();
        EditorLayer.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => TitleBox.Focus(), DispatcherPriority.Loaded);
    }

    private void CloseEditor()
    {
        EditorLayer.Visibility = Visibility.Collapsed;
        _editing = null;
        PasswordEdit.Clear();
        PasswordPlain.Clear();
        Focus();
    }

    private string EditorPassword => PasswordPlain.IsVisible ? PasswordPlain.Text : PasswordEdit.Password;

    private bool _syncingPassword;

    private void SetEditorPassword(string password)
    {
        _syncingPassword = true;
        PasswordEdit.Password = password;
        PasswordPlain.Text = password;
        _syncingPassword = false;
        ShowStrength(password);
    }

    private void PasswordEdit_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword)
            return;
        _syncingPassword = true;
        PasswordPlain.Text = PasswordEdit.Password;
        _syncingPassword = false;
        ShowStrength(PasswordEdit.Password);
    }

    private void PasswordPlain_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPassword)
            return;
        _syncingPassword = true;
        PasswordEdit.Password = PasswordPlain.Text;
        _syncingPassword = false;
        ShowStrength(PasswordPlain.Text);
    }

    private void ShowStrength(string password)
    {
        double bits = PasswordGenerator.Bits(password);
        StrengthBar.Value = Math.Min(bits, StrengthBar.Maximum);
        StrengthText.Text = password.Length == 0 ? "" : $"{PasswordGenerator.Rating(bits)} · {password.Length} characters";
    }

    private void EditorReveal_Click(object sender, RoutedEventArgs e) => ShowPlainPassword(!PasswordPlain.IsVisible);

    private void ShowPlainPassword(bool plain)
    {
        PasswordPlain.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
        PasswordEdit.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
        EditorRevealIcon.Text = plain ? "" : "";
    }

    private void GeneratorButton_Click(object sender, RoutedEventArgs e)
    {
        bool open = GeneratorButton.IsChecked == true;
        GeneratorPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            Generate();
            ShowPlainPassword(true); // A new password is shown, so it can be read.
        }
    }

    private void GenLength_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GenLengthText is null)
            return;
        ShowGeneratorLength();
        if (GeneratorPanel.IsVisible)
            Generate();
    }

    private void GenKind_Click(object sender, RoutedEventArgs e)
    {
        if (GeneratorPanel.IsVisible)
            Generate();
    }

    private void Generate_Click(object sender, RoutedEventArgs e) => Generate();

    private void ShowGeneratorLength() => GenLengthText.Text = $"{(int)GenLength.Value} characters";

    private void Generate() => SetEditorPassword(PasswordGenerator.Generate((int)GenLength.Value,
        GenUpper.IsChecked == true, GenLower.IsChecked == true, GenDigits.IsChecked == true, GenSymbols.IsChecked == true));

    private void EditorCancel_Click(object sender, RoutedEventArgs e) => CloseEditor();

    private void EditorSave_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null)
            return;
        string title = TitleBox.Text.Trim();
        if (title.Length == 0)
            title = UrlBox.Text.Trim().Length > 0 ? UrlBox.Text.Trim() : UserBox.Text.Trim();
        if (title.Length == 0)
        {
            EditorError.Text = "Give the entry a title.";
            EditorError.Visibility = Visibility.Visible;
            TitleBox.Focus();
            return;
        }
        var entry = _editing ?? new VaultEntry();
        string password = EditorPassword;
        var now = DateTime.UtcNow;
        if (_editingNew || entry.Password != password)
            entry.PasswordChanged = now;
        entry.Title = title;
        entry.UserName = UserBox.Text;
        entry.Password = password;
        entry.Url = UrlBox.Text.Trim();
        entry.Notes = NotesBox.Text;
        entry.Color = _editorColor;
        entry.IsDivider = DividerBox.IsChecked == true;
        entry.CategoryId = (CategoryBox.SelectedItem as VaultCategory)?.Id ?? _data.Categories[0].Id;
        entry.Modified = now;
        if (_editingNew)
            _data.Entries.Add(entry);
        bool saved = Save();
        CloseEditor();
        ShowCategories();
        ShowCards();
        if (saved)
            ShowToast($"\"{title}\" saved");
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null || _editing is not { } entry)
            return;
        var answer = MessageDialog.Show(Window.GetWindow(this), $"Delete the entry \"{entry.Title}\"?\n\nIt cannot be brought back.",
            "KeeSeek", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;
        _data.Entries.Remove(entry);
        Save();
        CloseEditor();
        ShowCategories();
        ShowCards();
        ShowToast($"\"{entry.Title}\" deleted");
    }

    // ---------- Categories ----------

    private void NewCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null || InputDialog.Ask(Window.GetWindow(this), "Name of the new category:", "") is not { } name)
            return;
        name = name.Trim();
        if (name.Length == 0)
            return;
        var category = new VaultCategory { Name = name };
        _data.Categories.Add(category);
        Save();
        _categoryId = category.Id;
        SearchBox.Clear();
        ShowCategories();
        ShowCards();
    }

    private void CategoryList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_data is null || ItemsControl.ContainerFromElement(CategoryList, (DependencyObject)e.OriginalSource) is not ListBoxItem
            { DataContext: CategoryRow { Id: { } id } row } container)
            return;
        e.Handled = true;
        var category = _data.Categories.First(c => c.Id == id);
        int index = _data.Categories.IndexOf(category);
        var menu = new ContextMenu();
        void Add(string header, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Rename...", true, () =>
        {
            if (InputDialog.Ask(Window.GetWindow(this), "New name of the category:", category.Name) is { } name && name.Trim().Length > 0)
            {
                category.Name = name.Trim();
                Save();
                ShowCategories();
                ShowCards();
            }
        });
        Add("Icon...", true, () => PickIcon(category, container));
        Add("Sort entries A-Z", row.Count > 1, () => SortCategory(category));
        Add("Move up", index > 0, () => MoveCategory(category, -1));
        Add("Move down", index < _data.Categories.Count - 1, () => MoveCategory(category, 1));
        menu.Items.Add(new Separator());
        Add("Delete...", _data.Categories.Count > 1, () => DeleteCategory(category, row.Count));
        menu.IsOpen = true;
    }

    /// <summary>A small panel next to the category with all icons; "Automatic" = the one that fits its name.</summary>
    private void PickIcon(VaultCategory category, UIElement target)
    {
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = target,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Right,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        void Choose(string? glyph)
        {
            popup.IsOpen = false;
            category.Icon = glyph;
            Save();
            ShowCategories();
        }
        var grid = new WrapPanel { Width = 8 * 40 };
        string current = CategoryIcons.For(category.Icon, category.Name);
        foreach (var (glyph, name) in CategoryIcons.All)
        {
            var button = new Button
            {
                Style = (Style)FindResource("SubtleButton"),
                Width = 36,
                Height = 36,
                Margin = new Thickness(2),
                ToolTip = name,
                Content = new TextBlock
                {
                    Text = glyph,
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 17,
                    Foreground = glyph == current ? (Brush)FindResource("GroupChevronBrush") : (Brush)FindResource("ListTextBrush"),
                },
            };
            button.Click += (_, _) => Choose(glyph);
            grid.Children.Add(button);
        }
        var automatic = new Button
        {
            Content = $"Automatic ({CategoryIcons.Name(CategoryIcons.For(null, category.Name))})",
            Margin = new Thickness(2, 8, 2, 0),
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        automatic.Click += (_, _) => Choose(null);
        popup.Child = new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(6, 0, 0, 0),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"Icon for \"{category.Name}\"", Margin = new Thickness(2, 0, 0, 8),
                        Foreground = (Brush)FindResource("SecondaryTextBrush") },
                    grid,
                    automatic,
                },
            },
        };
        popup.IsOpen = true;
    }

    // ---------- Category order: drag and drop, Alt+Up / Alt+Down ----------

    private Point? _categoryDragStart;
    private CategoryRow? _categoryDragRow;

    private void CategoryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _categoryDragStart = null;
        if (ItemsControl.ContainerFromElement(CategoryList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: CategoryRow { Id: not null } row })
        {
            _categoryDragStart = e.GetPosition(CategoryList);
            _categoryDragRow = row;
        }
    }

    private void CategoryList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_categoryDragStart is not { } start || _categoryDragRow is not { } row || e.LeftButton != MouseButtonState.Pressed)
            return;
        var now = e.GetPosition(CategoryList);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _categoryDragStart = null;
        DragDrop.DoDragDrop(CategoryList, new DataObject(typeof(CategoryRow), row), DragDropEffects.Move);
        DropLine.Visibility = Visibility.Collapsed;
    }

    /// <summary>Where a dragged category goes (0 = first), and the y of the line that shows it; null = not over the list.</summary>
    private int? DropIndex(DragEventArgs e, out double lineY)
    {
        lineY = 0;
        if (_data is null || !e.Data.GetDataPresent(typeof(CategoryRow)))
            return null;
        double y = e.GetPosition(CategoryList).Y;
        double bottom = 0;
        for (int i = 1; i < CategoryList.Items.Count; i++) // 0 is "All entries": nothing goes above it.
        {
            if (CategoryList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item)
                continue;
            double top = item.TranslatePoint(new Point(0, 0), CategoryList).Y;
            if (y < top + item.ActualHeight / 2)
            {
                lineY = top;
                return i - 1;
            }
            bottom = top + item.ActualHeight;
        }
        lineY = bottom;
        return _data.Categories.Count;
    }

    private void CategoryList_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetDataPresent(typeof(EntryCard)))
        {
            ShowCategoryDropBox(e);
            return;
        }
        if (DropIndex(e, out double lineY) is null)
        {
            e.Effects = DragDropEffects.None;
            DropLine.Visibility = Visibility.Collapsed;
            return;
        }
        e.Effects = DragDropEffects.Move;
        DropLine.Margin = new Thickness(10, Math.Max(0, lineY - 1), 10, 0);
        DropLine.Visibility = Visibility.Visible;
    }

    private void CategoryList_DragLeave(object sender, DragEventArgs e)
    {
        DropLine.Visibility = Visibility.Collapsed;
        CategoryDropBox.Visibility = Visibility.Collapsed;
    }

    private void CategoryList_Drop(object sender, DragEventArgs e)
    {
        DropLine.Visibility = Visibility.Collapsed;
        CategoryDropBox.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(typeof(EntryCard)) is EntryCard dragged)
        {
            e.Handled = true;
            if (CategoryUnder(e) is { } target && target.Id != dragged.Entry.CategoryId)
                MoveEntryToCategory(dragged.Entry, target);
            return;
        }
        if (_data is null || DropIndex(e, out _) is not int index || e.Data.GetData(typeof(CategoryRow)) is not CategoryRow { Id: { } id })
            return;
        e.Handled = true;
        var category = _data.Categories.FirstOrDefault(c => c.Id == id);
        if (category is null)
            return;
        int from = _data.Categories.IndexOf(category);
        if (index > from)
            index--; // The category's own place goes away first.
        if (index == from)
            return;
        _data.Categories.RemoveAt(from);
        _data.Categories.Insert(Math.Clamp(index, 0, _data.Categories.Count), category);
        Save();
        ShowCategories();
    }

    private void CategoryList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt || e.Key != Key.System || e.SystemKey is not (Key.Up or Key.Down)
            || _data is null || CategoryList.SelectedItem is not CategoryRow { Id: { } id })
            return;
        e.Handled = true;
        var category = _data.Categories.First(c => c.Id == id);
        MoveCategory(category, e.SystemKey == Key.Up ? -1 : 1);
        if (CategoryList.ItemContainerGenerator.ContainerFromItem(CategoryList.SelectedItem) is ListBoxItem item)
            item.Focus();
    }

    // ---------- Entry order: drag a row (or Alt+Up / Alt+Down); drop it on a category to move it there ----------

    private Point? _rowDragStart;
    private EntryCard? _rowDragCard;

    private void RowsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rowDragStart = null;
        // Not from the eye or the edit button.
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != RowsList; d = VisualTreeHelper.GetParent(d))
            if (d is Button)
                return;
        if (ItemsControl.ContainerFromElement(RowsList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: EntryCard card })
        {
            _rowDragStart = e.GetPosition(RowsList);
            _rowDragCard = card;
        }
    }

    private void RowsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_rowDragStart is not { } start || _rowDragCard is not { } card || e.LeftButton != MouseButtonState.Pressed)
            return;
        var now = e.GetPosition(RowsList);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _rowDragStart = null;
        DragDrop.DoDragDrop(RowsList, new DataObject(typeof(EntryCard), card), DragDropEffects.Move);
        RowDropLine.Visibility = Visibility.Collapsed;
        CategoryDropBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>The row a dragged entry goes before (or after the last one), in its own category; null = no place here.</summary>
    private (EntryCard Target, bool After, double LineY)? RowDropPlace(DragEventArgs e)
    {
        if (e.Data.GetData(typeof(EntryCard)) is not EntryCard dragged || SearchBox.Text.Trim().Length > 0)
            return null; // Search results are no place to set the order.
        double y = e.GetPosition(RowsList).Y;
        double offset = RowsList.TranslatePoint(new Point(0, 0), (UIElement)RowDropLine.Parent).Y;
        (EntryCard Card, double Top, double Bottom)? last = null;
        for (int i = 0; i < RowsList.Items.Count; i++)
        {
            if (RowsList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem { DataContext: EntryCard card } item)
                continue;
            double top = item.TranslatePoint(new Point(0, 0), RowsList).Y;
            if (top + item.ActualHeight < 0 || top > RowsList.ActualHeight)
                continue;
            if (y < top + item.ActualHeight / 2)
                return card.Entry.CategoryId == dragged.Entry.CategoryId ? (card, false, top + offset) : null;
            last = (card, top, top + item.ActualHeight);
        }
        return last is { } l && l.Card.Entry.CategoryId == dragged.Entry.CategoryId ? (l.Card, true, l.Bottom + offset) : null;
    }

    private void RowsList_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (RowDropPlace(e) is not { } place)
        {
            e.Effects = DragDropEffects.None;
            RowDropLine.Visibility = Visibility.Collapsed;
            return;
        }
        e.Effects = DragDropEffects.Move;
        RowDropLine.Margin = new Thickness(12, Math.Max(0, place.LineY - 1), 12, 0);
        RowDropLine.Visibility = Visibility.Visible;
    }

    private void RowsList_DragLeave(object sender, DragEventArgs e) => RowDropLine.Visibility = Visibility.Collapsed;

    private void RowsList_Drop(object sender, DragEventArgs e)
    {
        RowDropLine.Visibility = Visibility.Collapsed;
        if (_data is null || RowDropPlace(e) is not { } place || e.Data.GetData(typeof(EntryCard)) is not EntryCard dragged)
            return;
        e.Handled = true;
        if (place.Target.Entry == dragged.Entry)
            return;
        _data.Entries.Remove(dragged.Entry);
        int index = _data.Entries.IndexOf(place.Target.Entry) + (place.After ? 1 : 0);
        _data.Entries.Insert(index, dragged.Entry);
        Save();
        ShowCards();
        SelectRow(dragged.Entry);
    }

    /// <summary>Alt+Up / Alt+Down: the entry changes place with the one before / after it in its category.</summary>
    private void MoveEntry(VaultEntry entry, int by)
    {
        if (_data is null)
            return;
        var same = _data.Entries.Where(x => x.CategoryId == entry.CategoryId).ToList();
        int at = same.IndexOf(entry), to = at + by;
        if (to < 0 || to >= same.Count)
            return;
        int a = _data.Entries.IndexOf(entry), b = _data.Entries.IndexOf(same[to]);
        (_data.Entries[a], _data.Entries[b]) = (_data.Entries[b], _data.Entries[a]);
        Save();
        ShowCards();
        SelectRow(entry);
    }

    private void SelectRow(VaultEntry entry)
    {
        var card = RowsList.Items.OfType<EntryCard>().FirstOrDefault(c => c.Entry == entry);
        if (card is null)
            return;
        RowsList.SelectedItem = card;
        RowsList.ScrollIntoView(card);
        Dispatcher.BeginInvoke(() => (RowsList.ItemContainerGenerator.ContainerFromItem(card) as ListBoxItem)?.Focus(),
            DispatcherPriority.Loaded);
    }

    /// <summary>The category (not "All entries") under the mouse while an entry is dragged, with the frame on it.</summary>
    private VaultCategory? CategoryUnder(DragEventArgs e)
    {
        if (_data is null)
            return null;
        double y = e.GetPosition(CategoryList).Y;
        for (int i = 1; i < CategoryList.Items.Count; i++)
        {
            if (CategoryList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem { DataContext: CategoryRow { Id: { } id } } item)
                continue;
            double top = item.TranslatePoint(new Point(0, 0), CategoryList).Y;
            if (y >= top && y < top + item.ActualHeight)
            {
                CategoryDropBox.Margin = new Thickness(6, top, 6, 0);
                CategoryDropBox.Height = item.ActualHeight;
                return _data.Categories.FirstOrDefault(c => c.Id == id);
            }
        }
        return null;
    }

    private void ShowCategoryDropBox(DragEventArgs e)
    {
        DropLine.Visibility = Visibility.Collapsed;
        var dragged = e.Data.GetData(typeof(EntryCard)) as EntryCard;
        if (CategoryUnder(e) is { } target && target.Id != dragged?.Entry.CategoryId)
        {
            e.Effects = DragDropEffects.Move;
            CategoryDropBox.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            CategoryDropBox.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The entry moves to another category (at its end).</summary>
    private void MoveEntryToCategory(VaultEntry entry, VaultCategory target)
    {
        if (_data is null)
            return;
        entry.CategoryId = target.Id;
        _data.Entries.Remove(entry);
        _data.Entries.Add(entry);
        Save();
        ShowCategories();
        ShowCards();
        ShowToast($"\"{entry.Title}\" moved to \"{target.Name}\"");
    }

    /// <summary>Sorts a category's entries A-Z once (by title, numbers in order); the user can rearrange them again.</summary>
    private void SortCategory(VaultCategory category)
    {
        if (_data is null)
            return;
        var slots = _data.Entries.Select((e, i) => (e, i)).Where(x => x.e.CategoryId == category.Id).Select(x => x.i).ToList();
        var sorted = slots.Select(i => _data.Entries[i])
            .OrderBy(e => e.Title, Comparer<string>.Create((a, b) => FileItemComparer.NaturalCompare(a, b))).ToList();
        for (int k = 0; k < slots.Count; k++)
            _data.Entries[slots[k]] = sorted[k];
        Save();
        ShowCards();
    }

    private void MoveCategory(VaultCategory category, int by)
    {
        if (_data is null)
            return;
        int index = _data.Categories.IndexOf(category);
        _data.Categories.RemoveAt(index);
        _data.Categories.Insert(Math.Clamp(index + by, 0, _data.Categories.Count), category);
        Save();
        ShowCategories();
    }

    /// <summary>Deletes a category; its entries move to the first other category.</summary>
    private void DeleteCategory(VaultCategory category, int count)
    {
        if (_data is null)
            return;
        var target = _data.Categories.First(c => c != category);
        string text = count == 0
            ? $"Delete the category \"{category.Name}\"?"
            : $"Delete the category \"{category.Name}\"?\n\nIts {count} {(count == 1 ? "entry moves" : "entries move")} to \"{target.Name}\".";
        if (MessageDialog.Show(Window.GetWindow(this), text, "KeeSeek", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        foreach (var entry in _data.Entries.Where(e => e.CategoryId == category.Id))
            entry.CategoryId = target.Id;
        _data.Categories.Remove(category);
        Save();
        if (_categoryId == category.Id)
            _categoryId = target.Id;
        ShowCategories();
        ShowCards();
    }

    // ---------- Import ----------

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null)
            return;
        var dialog = new OpenFileDialog
        {
            Title = "Import passwords from a CSV file",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        string path = dialog.FileName;
        List<CsvImport.Row> rows;
        try
        {
            rows = CsvImport.Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageDialog.Show(Window.GetWindow(this), $"Cannot read the file:\n\n{ex.Message}", "KeeSeek", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int added = 0, skipped = 0;
        var categories = new HashSet<string>();
        foreach (var (categoryName, entry) in rows)
        {
            var category = _data.Categories.FirstOrDefault(c => c.Name.Equals(categoryName, StringComparison.CurrentCultureIgnoreCase));
            if (category is null)
            {
                category = new VaultCategory { Name = categoryName };
                _data.Categories.Add(category);
            }
            // An exact copy (all content the same, notes too: in the vault already, or twice in the file): left out,
            // nothing is lost. Rows that differ in any field - also only in their notes - are all kept.
            if (_data.Entries.Any(x => x.CategoryId == category.Id && x.Title == entry.Title && x.UserName == entry.UserName
                                       && x.Password == entry.Password && x.Url == entry.Url
                                       && x.Notes.ReplaceLineEndings() == entry.Notes.ReplaceLineEndings()))
            {
                skipped++;
                continue;
            }
            entry.CategoryId = category.Id;
            _data.Entries.Add(entry);
            categories.Add(category.Id);
            added++;
        }
        bool saved = Save();
        _categoryId = null;
        SearchBox.Clear();
        ShowCategories();
        ShowCards();
        if (!saved)
            return;

        string summary = $"{added:N0} {(added == 1 ? "entry" : "entries")} imported into {categories.Count} " +
                         $"{(categories.Count == 1 ? "category" : "categories")}" +
                         (skipped > 0 ? $".\n{skipped:N0} exact {(skipped == 1 ? "copy was" : "copies were")} left out " +
                                        "(the same entry, notes included, twice in the file or already in the vault)." : ".");
        var answer = MessageDialog.Show(Window.GetWindow(this),
            $"{summary}\n\nThe CSV file holds your passwords as plain text. Delete it now?\n\n{path}\n\n" +
            "It is deleted permanently (not to the Recycle Bin).",
            "KeeSeek import", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
            return;
        try
        {
            File.Delete(path);
            ShowToast("The CSV file was deleted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(Window.GetWindow(this), $"Cannot delete the file:\n\n{ex.Message}", "KeeSeek", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Settings ----------

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsButton.IsChecked == true);

    private void ShowSettings(bool open)
    {
        SettingsButton.IsChecked = open;
        if (open)
        {
            SettingsPage.Open(VaultPath, IsUnlocked);
            SettingsPage.Visibility = Visibility.Visible;
        }
        else if (SettingsPage.Visibility == Visibility.Visible)
        {
            SettingsPage.Visibility = Visibility.Collapsed;
            FocusView();
        }
    }

    /// <summary>Settings page: a new master password; null = done, else the reason.</summary>
    private async Task<string?> ChangeMasterPasswordAsync(string current, string next)
    {
        if (_file is null || _data is null)
            return "Unlock KeeSeek first.";
        if (!await _file.IsPasswordAsync(current))
            return "The current master password is not right.";
        try
        {
            await _file.ChangePasswordAsync(next, _data);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Cannot save the vault: {ex.Message}";
        }
    }

    // ---------- Keys, typing to search, toast ----------

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _lastUse = DateTime.UtcNow;
        if (SettingsPage.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ShowSettings(false);
            }
            return;
        }
        var modifiers = Keyboard.Modifiers;
        if (EditorLayer.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseEditor();
            }
            else if (e.Key == Key.S && modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                EditorSave_Click(this, new RoutedEventArgs());
            }
            return;
        }
        if (!IsUnlocked)
            return;
        switch (e.Key)
        {
            case Key.N when modifiers == ModifierKeys.Control:
                e.Handled = true;
                OpenEditor(null);
                break;
            case Key.L when modifiers == ModifierKeys.Control:
                e.Handled = true;
                Lock();
                break;
            case Key.F when modifiers == ModifierKeys.Control:
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.Escape when SearchBox.Text.Length > 0:
                e.Handled = true;
                SearchBox.Clear();
                Focus();
                break;
        }
    }

    /// <summary>Typing outside a text box goes into the search.</summary>
    private void View_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!IsUnlocked || EditorLayer.IsVisible || SettingsPage.IsVisible || Keyboard.FocusedElement is TextBox or PasswordBox
            || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
            return;
        e.Handled = true;
        SearchBox.Focus();
        SearchBox.Text += e.Text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void ShowToast(string text, bool sticky = false)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        if (!sticky)
            _toastTimer.Start();
    }
}
