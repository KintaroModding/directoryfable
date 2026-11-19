using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DirectoryFable.Chat;

namespace DirectoryFable.Views;

/// <summary>
/// GemmaChat's settings page: the providers (name, address, API key, model with "Load models"), the standing
/// instructions, and where the files are. Changes are kept on a working copy; Save (or Close) writes gemmachat.json.
/// </summary>
public partial class GemmaChatSettingsView : UserControl
{
    private ChatConfig _config = new();
    private bool _filling;
    private bool _dirty;

    public GemmaChatSettingsView()
    {
        InitializeComponent();
    }

    public event Action? CloseRequested;

    /// <summary>The settings were saved (the chat view takes them over).</summary>
    public event Action<ChatConfig>? Saved;

    public void Open(ChatConfig config)
    {
        _config = config;
        _dirty = false;
        _beforeSwitch.Clear();
        ProvidersList.ItemsSource = _config.Providers;
        ProvidersList.SelectedItem = _config.Selected;
        SystemPromptBox.Text = _config.SystemPrompt;
        NoAskBox.IsChecked = _config.ChangesWithoutAsking;
        FolderBox.Text = ChatStore.Folder;
        SaveStatus.Text = "Claude (Anthropic API) and any provider with an OpenAI-compatible chat API: OpenAI (GPT), DeepSeek, a local Ollama or LM Studio, and many more.";
        ShowProvider();
    }

    private ChatProvider? Current => ProvidersList.SelectedItem as ChatProvider;

    private void ProvidersList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowProvider();

    private void ShowProvider()
    {
        _filling = true;
        try
        {
            var p = Current;
            ProviderFields.IsEnabled = p is not null;
            RemoveButton.IsEnabled = p is not null;
            NameBox.Text = p?.Name ?? "";
            KindBox.SelectedIndex = p?.IsClaude == true ? 1 : 0;
            ShowKindHelp(p);
            UrlBox.Text = p?.BaseUrl ?? "";
            KeyBox.Password = p?.ApiKey ?? "";
            KeyPlain.Text = p?.ApiKey ?? "";
            ModelBox.ItemsSource = null;
            ModelBox.Text = p?.Model ?? "";
            ModelStatus.Text = "";
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>Name, address and model go into the provider at once (the list shows the new name).</summary>
    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling || Current is not { } p)
            return;
        p.BaseUrl = UrlBox.Text.Trim();
        p.Model = ModelBox.Text.Trim();
        if (sender == NameBox && p.Name != NameBox.Text)
        {
            p.Name = NameBox.Text;
            ProvidersList.Items.Refresh();
        }
        _dirty = true;
    }

    /// <summary>A provider's address and model before its API type was switched on this page: switching back restores them.</summary>
    private readonly Dictionary<string, (string Url, string Model)> _beforeSwitch = [];

    /// <summary>
    /// Another API type: Claude's address and model come in (unless the address is already an Anthropic one, a proxy
    /// for example); back to OpenAI-compatible, the earlier ones return (or Claude's are cleared).
    /// </summary>
    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Current is not { } p || KindBox.SelectedItem is not ComboBoxItem { Tag: string kind } || kind == p.Kind)
            return;
        string url, model;
        if (_beforeSwitch.Remove(p.Id, out var earlier))
        {
            (url, model) = earlier;
        }
        else
        {
            _beforeSwitch[p.Id] = (p.BaseUrl, p.Model);
            var claude = ChatConfig.ClaudePreset();
            bool toClaude = kind == ChatProvider.ClaudeKind;
            url = toClaude
                ? (p.BaseUrl.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ? p.BaseUrl : claude.BaseUrl)
                : (p.BaseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase) ? "https://" : p.BaseUrl);
            model = toClaude
                ? (p.Model.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ? p.Model : claude.Model)
                : (p.Model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? "" : p.Model);
        }
        p.Kind = kind;
        p.KnownModels = []; // Another API: its own models.
        p.BaseUrl = url;
        p.Model = model;
        _filling = true;
        UrlBox.Text = url;
        ModelBox.ItemsSource = null;
        ModelBox.Text = model;
        _filling = false;
        ShowKindHelp(p);
        _dirty = true;
    }

    /// <summary>The address and key help follow the API type.</summary>
    private void ShowKindHelp(ChatProvider? p)
    {
        bool claude = p?.IsClaude == true;
        UrlHelp.Text = claude
            ? "https://api.anthropic.com (Claude's API). The key comes from the Claude Console (platform.claude.com); API use is billed there, apart from a Claude subscription."
            : "For example https://api.openai.com/v1 (OpenAI), https://api.deepseek.com, http://localhost:11434/v1 (Ollama), http://localhost:1234/v1 (LM Studio). \"/chat/completions\" is added.";
        KeyLabel.Text = claude ? "API key" : "API key (empty for local servers)";
    }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_filling || Current is not { } p)
            return;
        p.ApiKey = KeyBox.Password.Trim();
        _filling = true;
        KeyPlain.Text = KeyBox.Password;
        _filling = false;
        _dirty = true;
    }

    private void KeyPlain_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling || Current is not { } p)
            return;
        p.ApiKey = KeyPlain.Text.Trim();
        _filling = true;
        KeyBox.Password = KeyPlain.Text;
        _filling = false;
        _dirty = true;
    }

    private void ShowKey_Click(object sender, RoutedEventArgs e)
    {
        bool show = ShowKeyButton.IsChecked == true;
        KeyPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        KeyBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>"Add" opens a menu: the ready-made providers, or an empty OpenAI-compatible one.</summary>
    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var preset in ChatConfig.Presets)
        {
            var item = new MenuItem { Header = preset.Title };
            item.Click += (_, _) => AddProvider(preset.Create());
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var other = new MenuItem { Header = "Other (OpenAI-compatible)..." };
        other.Click += (_, _) => AddProvider(new ChatProvider { Name = "New provider", BaseUrl = "https://" });
        menu.Items.Add(other);
        menu.IsOpen = true;
    }

    private void AddProvider(ChatProvider provider)
    {
        _config.Providers.Add(provider);
        ProvidersList.Items.Refresh();
        ProvidersList.SelectedItem = provider;
        _dirty = true;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void RemoveProvider_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p)
            return;
        if (MessageDialog.Show(Window.GetWindow(this), $"Remove the provider \"{p.Name}\" (with its API key)?", "GemmaChat",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _config.Providers.Remove(p);
        if (_config.SelectedProviderId == p.Id)
            _config.SelectedProviderId = _config.Providers.FirstOrDefault()?.Id;
        ProvidersList.Items.Refresh();
        ProvidersList.SelectedItem = _config.Providers.FirstOrDefault();
        _dirty = true;
    }

    private async void LoadModels_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p)
            return;
        LoadModelsButton.IsEnabled = false;
        ModelStatus.Foreground = (Brush)FindResource("SecondaryTextBrush");
        ModelStatus.Text = "Asking the provider...";
        try
        {
            var models = await ChatClient.ListModelsAsync(p, CancellationToken.None);
            p.KnownModels = models;
            _dirty = true;
            string typed = ModelBox.Text;
            ModelBox.ItemsSource = models;
            ModelBox.Text = typed.Length > 0 ? typed : models.FirstOrDefault() ?? "";
            ModelStatus.Text = models.Count == 0 ? "Connected, but the provider lists no models: type the model's name."
                : $"Connected: {models.Count} {(models.Count == 1 ? "model" : "models")} (open the list to choose).";
            if (models.Count > 0)
                ModelBox.IsDropDownOpen = true;
        }
        catch (Exception ex) when (ex is ChatApiException or HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            ModelStatus.Foreground = (Brush)FindResource("ErrorBrush");
            ModelStatus.Text = ex is ChatApiException { Status: 401 or 403 }
                ? $"The provider refused the API key: {ex.Message}"
                : $"No connection: {ex.Message}";
        }
        finally
        {
            LoadModelsButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private bool Save()
    {
        _config.SystemPrompt = SystemPromptBox.Text;
        _config.ChangesWithoutAsking = NoAskBox.IsChecked == true;
        try
        {
            ChatStore.SaveConfig(_config);
            _dirty = false;
            SaveStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
            Saved?.Invoke(_config);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveStatus.Text = $"Cannot save: {ex.Message}";
            return false;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ChatStore.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ChatStore.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SaveStatus.Text = $"Cannot open the folder: {ex.Message}";
        }
    }

    /// <summary>Saves what changed (also the instructions); false when it could not be saved.</summary>
    public bool SaveIfChanged() =>
        !(_dirty || SystemPromptBox.Text != _config.SystemPrompt || (NoAskBox.IsChecked == true) != _config.ChangesWithoutAsking) || Save();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (SaveIfChanged())
            CloseRequested?.Invoke();
    }
}
