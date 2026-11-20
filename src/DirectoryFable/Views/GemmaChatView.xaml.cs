using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DirectoryFable.Chat;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Views;

/// <summary>
/// GemmaChat, the seventh title bar view: chats with an AI through any OpenAI-compatible API (the provider, its
/// address, API key and model are set on the settings page; Chat/ChatStore keeps them and the chats in
/// data\gemmachat). The answer streams in; Stop ends it. Code blocks (```) get their own box with a copy button.
/// With a workspace folder (bar, or "Open in GemmaChat" in the explorer) the AI can work on its files through tools
/// (Chat/WorkspaceTools): DF runs each call, asks the user before every change (a diff card with Allow / Allow all in
/// this chat / Refuse; the setting "changes without asking" skips it, deleting always asks), and sends the results
/// back until the AI is done. Every change can be undone from its card.
/// </summary>
public partial class GemmaChatView : UserControl
{
    private const int MaxRounds = 30;

    private ChatConfig _config = new();
    private readonly ObservableCollection<ChatThread> _chats = [];
    private ChatThread? _chat;
    private bool _stickToBottom = true;
    private bool _loaded;

    /// <summary>
    /// What a chat shows and does while DirectoryFable runs: its message items, its running answer (each chat answers
    /// on its own, also while another chat is shown), its waiting approval card and its "allow all" choices.
    /// </summary>
    private sealed class ChatRun
    {
        public ObservableCollection<MessageItem> Items { get; } = [];
        public CancellationTokenSource? Cancel;
        public MessageItem? PendingCard;
        public bool AllowAll, AllowCommands;
        public bool IsAnswering => Cancel is not null;
    }

    private readonly Dictionary<ChatThread, ChatRun> _runs = [];

    private ChatRun RunOf(ChatThread chat)
    {
        if (!_runs.TryGetValue(chat, out var run))
            _runs[chat] = run = new ChatRun();
        return run;
    }

    /// <summary>The shown chat's items (what the message list shows).</summary>
    private ObservableCollection<MessageItem> _items => _chat is null ? [] : RunOf(_chat).Items;

    /// <summary>The shown chat has a running answer.</summary>
    private bool ShownIsAnswering => _chat is not null && _runs.TryGetValue(_chat, out var run) && run.IsAnswering;

    public GemmaChatView()
    {
        InitializeComponent();
        ChatList.ItemsSource = _chats;
        AttachmentStrip.ItemsSource = _attachments;
        _attachments.CollectionChanged += (_, _) =>
            AttachmentStrip.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.CloseRequested += () => ShowSettings(false);
        SettingsPage.Saved += config =>
        {
            _config = config;
            ShowProviders();
        };
        Loaded += (_, _) => IconFrames.Sharpen(EmptyIcon);
        var settings = AppSettings.Load();
        ApplyTheme(ChatTheme.ById(settings.GemmaChatTheme));
        ApplyTextSize(settings.GemmaChatFontSize);
    }

    /// <summary>The title text changed (another chat).</summary>
    public event Action? ActiveChanged;

    /// <summary>The chat to show when the view opens (from the session).</summary>
    public string? CurrentChatId { get; set; }

    public string TitleText => _chat is { Messages.Count: > 0 } chat ? $"GemmaChat: {chat.Title}" : "GemmaChat";

    /// <summary>The view is shown: the first time the files are read.</summary>
    public void OnShown()
    {
        EnsureLoaded();
        Dispatcher.BeginInvoke(FocusView, DispatcherPriority.Loaded);
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        _config = ChatStore.LoadConfig();
        foreach (var chat in ChatStore.LoadChats())
            _chats.Add(chat);
        ShowProviders();
        var start = _chats.FirstOrDefault(c => c.Id == CurrentChatId);
        if (start is not null)
            ChatList.SelectedItem = start;
        else
            OpenChat(null);
    }

    /// <summary>Explorer "Open in GemmaChat": a new chat that works in this folder.</summary>
    public void StartInFolder(string folder)
    {
        EnsureLoaded();
        OpenChat(null);
        SetWorkspace(folder);
    }

    public void FocusView()
    {
        if (!SettingsPage.IsVisible)
            InputBox.Focus();
    }

    /// <summary>DirectoryFable ends: the running answers stop (what came is kept).</summary>
    public void Shutdown()
    {
        foreach (var run in _runs.Values)
            run.Cancel?.Cancel();
    }

    // ---------- Providers ----------

    private bool _fillingProviders;

    private void ShowProviders()
    {
        _fillingProviders = true;
        ProviderBox.ItemsSource = null;
        ProviderBox.ItemsSource = _config.Providers;
        ProviderBox.SelectedItem = _config.Selected;
        _fillingProviders = false;
        ShowModelAndEffort();
        ShowProviderState();
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingProviders || ProviderBox.SelectedItem is not ChatProvider provider)
            return;
        _config.SelectedProviderId = provider.Id;
        SaveConfig();
        ShowModelAndEffort();
        ShowProviderState();
    }

    /// <summary>The bar's model and effort follow the chosen provider.</summary>
    private void ShowModelAndEffort()
    {
        _fillingProviders = true;
        var p = _config.Selected;
        ShowModels(p);
        // Claude also has "Extra high" and "Max"; OpenAI-compatible providers know low / medium / high.
        foreach (var level in EffortBox.Items.OfType<ComboBoxItem>().Where(i => (string)i.Tag is "xhigh" or "max"))
            level.Visibility = p?.IsClaude == true ? Visibility.Visible : Visibility.Collapsed;
        EffortBox.ToolTip = p?.IsClaude == true
            ? "How much Claude thinks (output_config.effort). Default: not sent - the model's own default."
            : "How hard a reasoning model thinks (reasoning_effort). Default: not sent - the provider decides. Models that do not support it may ignore it or report an error.";
        EffortBox.SelectedItem = EffortBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (p?.Effort ?? "")) ?? EffortBox.Items[0];
        ModelBarBox.IsEnabled = EffortBox.IsEnabled = p is not null;
        _fillingProviders = false;
    }

    /// <summary>The first time the list opens, the provider is asked for its models.</summary>
    private async void ModelBarBox_DropDownOpened(object sender, EventArgs e)
    {
        if (_config.Selected is not { } p || p.KnownModels.Count > 0 || (p.BaseUrl.Trim().Length == 0 && !p.IsClaude))
            return;
        BarText.Text = "Asking the provider for its models...";
        try
        {
            var models = await ChatClient.ListModelsAsync(p, CancellationToken.None);
            p.KnownModels = models;
            SaveConfig();
            ShowModels(p);
            BarText.Text = $"{models.Count} {(models.Count == 1 ? "model" : "models")}";
        }
        catch (Exception ex) when (ex is ChatApiException or HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            BarText.Text = $"No model list: {ex.Message}";
        }
    }

    private void ModelBarBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingProviders && ModelBarBox.SelectedItem is string model)
            SetModel(model);
    }

    /// <summary>
    /// The bar's model list: the provider's models, with the chosen model first when the provider does not list it
    /// (a name typed on the settings page); the chosen one is selected.
    /// </summary>
    private void ShowModels(ChatProvider? p)
    {
        bool filling = _fillingProviders;
        _fillingProviders = true;
        var models = p is null ? [] : new List<string>(p.KnownModels);
        if (p is { Model: { Length: > 0 } current } && !models.Contains(current.Trim()))
            models.Insert(0, current.Trim());
        ModelBarBox.ItemsSource = models;
        ModelBarBox.SelectedItem = p?.Model.Trim();
        _fillingProviders = filling;
    }

    /// <summary>A model chosen in the bar: used from the next message on, kept for the provider.</summary>
    private void SetModel(string model)
    {
        model = model.Trim();
        if (_fillingProviders || _config.Selected is not { } p || model.Length == 0 || model == p.Model)
            return;
        p.Model = model;
        SaveConfig();
        ShowProviderState();
    }

    private void EffortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingProviders || _config.Selected is not { } p || EffortBox.SelectedItem is not ComboBoxItem { Tag: string effort })
            return;
        p.Effort = effort;
        SaveConfig();
    }

    /// <summary>The bar's text: the chat's token use (when the provider reports it); the empty page says what is missing.</summary>
    private void ShowProviderState()
    {
        var p = _config.Selected;
        string? missing = p is null ? "Add a provider on the settings page."
            : p.BaseUrl.Trim().Length == 0 && !p.IsClaude ? $"Enter the address of \"{p.Name}\" on the settings page."
            : p.IsClaude && p.ApiKey.Trim().Length == 0 ? $"Enter the API key of \"{p.Name}\" on the settings page."
            : p.Model.Trim().Length == 0 ? $"Choose a model for \"{p.Name}\" (in the bar or on the settings page)."
            : null;
        BarText.Text = _chat is { ContextTokens: > 0 } c
            ? $"Context {c.ContextTokens:N0} tokens · last answer {c.AnswerTokens:N0}"
            : "";
        EmptyText.Text = missing ?? (_chat?.Workspace is { } folder
            ? $"Ask anything - the AI can read and change the files in\n{folder}\n(you approve every change)."
            : "Ask anything. Your chats are saved in DirectoryFable's data folder.\nTo let the AI work on files, choose a folder in the bar.");
    }

    private void SaveConfig()
    {
        try
        {
            ChatStore.SaveConfig(_config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BarText.Text = $"Cannot save the settings: {ex.Message}";
        }
    }

    // ---------- Workspace ----------

    private void Workspace_Click(object sender, RoutedEventArgs e)
    {
        if (ShownIsAnswering || _chat is null)
            return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "The folder the AI may work in",
            InitialDirectory = _chat.Workspace ?? "",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            SetWorkspace(dialog.FolderName);
    }

    private void CloseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (!ShownIsAnswering)
            SetWorkspace(null);
    }

    private void SetWorkspace(string? folder)
    {
        if (_chat is null)
            return;
        _chat.Workspace = folder;
        var run = RunOf(_chat);
        run.AllowAll = run.AllowCommands = false;
        if (_chat.Messages.Count > 0)
            SaveChat(_chat);
        RefreshList(); // The list's folder mark.
        ShowWorkspace();
        ShowProviderState();
        FocusView();
    }

    private void ShowWorkspace()
    {
        string? folder = _chat?.Workspace;
        WorkspaceText.Text = folder is null ? "Work in a folder..." : $"Folder: {folder}";
        WorkspaceButton.ToolTip = folder is null
            ? "Let the AI read and change the files of a folder (you approve every change)"
            : $"The AI may read and change the files in {folder}. Click to choose another folder.";
        CloseWorkspaceButton.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Chats ----------

    private void NewChat_Click(object sender, RoutedEventArgs e) => OpenChat(null);

    /// <summary>Shows a chat (null = a new, empty one, saved with its first message).</summary>
    private void OpenChat(ChatThread? chat)
    {
        // The chat that is left: without a running answer its items and "allow all" choices go (they are made
        // again from its messages); with one, it goes on in the background and keeps them.
        if (_chat is { } left && _runs.TryGetValue(left, out var leftRun) && !leftRun.IsAnswering)
            _runs.Remove(left);
        _chat = chat ?? new ChatThread();
        CurrentChatId = chat?.Id;
        if (chat is null)
            ChatList.SelectedItem = null;
        var run = RunOf(_chat);
        if (!run.IsAnswering)
        {
            run.Items.Clear();
            foreach (var item in ItemsOf(_chat))
                run.Items.Add(item);
        }
        Messages.ItemsSource = run.Items;
        ShowSending(run.IsAnswering);
        ShowEmpty();
        ShowWorkspace();
        ShowProviderState();
        _stickToBottom = true;
        Dispatcher.BeginInvoke(() => MessagesScroller.ScrollToEnd(), DispatcherPriority.Loaded);
        ActiveChanged?.Invoke();
        FocusView();
    }

    /// <summary>What a saved chat shows: its texts, tool steps and change cards (an assistant turn with only tool calls shows nothing).</summary>
    private static IEnumerable<MessageItem> ItemsOf(ChatThread chat)
    {
        foreach (var message in chat.Messages)
        {
            if (message.Role == "tool")
                yield return message.Change is { } change ? MessageItem.ForChange(message, change) : MessageItem.ForActivity(message);
            else if (message.Content.Length > 0 || message.Role == "user")
                yield return new MessageItem(message);
        }
    }

    private void ShowEmpty() => EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChatList.SelectedItem is ChatThread chat && chat != _chat)
            OpenChat(chat);
    }

    private void ChatList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ChatList, (DependencyObject)e.OriginalSource) is not ListBoxItem { DataContext: ChatThread chat })
            return;
        e.Handled = true;
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Rename..." };
        rename.Click += (_, _) =>
        {
            if (InputDialog.Ask(Window.GetWindow(this), "Name of the chat:", chat.Title) is { } name && name.Trim().Length > 0)
            {
                chat.Title = name.Trim();
                SaveChat(chat);
                RefreshList();
                ActiveChanged?.Invoke();
            }
        };
        var delete = new MenuItem { Header = "Delete..." };
        delete.Click += (_, _) =>
        {
            if (_runs.TryGetValue(chat, out var running) && running.IsAnswering)
            {
                BarText.Text = $"\"{chat.Title}\" is answering: stop it first (open it, then Stop).";
                return;
            }
            if (MessageDialog.Show(Window.GetWindow(this), $"Delete the chat \"{chat.Title}\"?\n\nIt cannot be brought back (also the Undo copies of its file changes go).",
                    "GemmaChat", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            try
            {
                ChatStore.DeleteChat(chat);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                BarText.Text = $"Cannot delete the chat: {ex.Message}";
                return;
            }
            _chats.Remove(chat);
            _runs.Remove(chat);
            if (chat == _chat)
                OpenChat(null);
        };
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private void RefreshList() => ChatList.Items.Refresh();

    private void SaveChat(ChatThread chat)
    {
        try
        {
            ChatStore.SaveChat(chat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BarText.Text = $"Cannot save the chat: {ex.Message}";
        }
    }

    // ---------- Sending: answer, tools, answer again ----------

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) =>
        InputHint.Visibility = InputBox.Text.Length == 0 && _attachments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Enter sends, Shift+Enter makes a new line.</summary>
    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = SendAsync();
        }
        else if ((e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) || (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            // A screenshot or copied files in the clipboard: added to the message; text pastes as usual.
            if (PasteAttachments())
                e.Handled = true;
        }
    }

    // ---------- Pictures and files for the next message ----------

    /// <summary>PendingImage and PendingFile items, in the order they were added.</summary>
    private readonly ObservableCollection<object> _attachments = [];
    private const int MaxAttachments = 10;

    /// <summary>
    /// Copied files in the clipboard, or a picture (not text): added to the message; false = paste as text.
    /// </summary>
    private bool PasteAttachments()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                AttachFiles(Clipboard.GetFileDropList().Cast<string>().ToList());
                return true;
            }
            if (Clipboard.ContainsText())
                return false;
        }
        catch (COMException)
        {
            return false;
        }
        if (ChatImages.FromClipboard() is not { } image)
            return false;
        AddAttachment(image);
        return true;
    }

    /// <summary>
    /// Adds files to the next message: pictures as pictures, text files and PDFs as files (the explorer's "Send to
    /// GemmaChat", the paperclip, a drop, Ctrl+V of copied files). What cannot be attached is named in the bar.
    /// </summary>
    public void AttachFiles(IReadOnlyList<string> paths)
    {
        EnsureLoaded();
        var problems = new List<string>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                problems.Add($"{Path.GetFileName(path.TrimEnd('\\'))}: a folder (use \"Work in a folder\" for it).");
            }
            else if (ChatImages.IsPicture(path))
            {
                if (ChatImages.FromFile(path) is { } image)
                    AddAttachment(image);
                else
                    problems.Add($"{Path.GetFileName(path)}: not a picture that can be read.");
            }
            else if (ChatFiles.FromFile(path, out string? problem) is { } file)
            {
                AddAttachment(file);
            }
            else if (problem is not null)
            {
                problems.Add(problem);
            }
        }
        if (problems.Count > 0)
            BarText.Text = problems.Count == 1 ? problems[0] : $"{problems.Count} files could not be attached. {problems[0]}";
        if (!SettingsPage.IsVisible)
            InputBox.Focus();
    }

    private void AddAttachment(object attachment)
    {
        if (_attachments.Count >= MaxAttachments)
        {
            BarText.Text = $"At most {MaxAttachments} pictures and files per message.";
            return;
        }
        _attachments.Add(attachment);
        InputHint.Visibility = Visibility.Collapsed;
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is { } attachment)
            _attachments.Remove(attachment);
        InputHint.Visibility = InputBox.Text.Length == 0 && _attachments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach files to the message",
            Multiselect = true,
            Filter = "Text files, PDF files and pictures|*.*",
            InitialDirectory = _chat?.Workspace ?? "",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            AttachFiles(dialog.FileNames);
    }

    private void InputArea_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void InputArea_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;
        e.Handled = true;
        AttachFiles(files);
    }

    /// <summary>An attached file in a message: its copy opens with its Windows program.</summary>
    private void FileChip_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatFileChip { Path: var path } || !File.Exists(path))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            BarText.Text = $"Cannot open the file: {ex.Message}";
        }
    }

    /// <summary>A picture in a message: opened with Windows' picture program.</summary>
    private void Picture_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatPicture { Path: var path } || !File.Exists(path))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            BarText.Text = $"Cannot open the picture: {ex.Message}";
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_chat is not null && _runs.TryGetValue(_chat, out var run) && run.Cancel is { } cancel)
            cancel.Cancel(); // "Stop".
        else
            _ = SendAsync();
    }

    private async Task SendAsync()
    {
        string text = InputBox.Text.Trim();
        if (ShownIsAnswering || (text.Length == 0 && _attachments.Count == 0) || _chat is null)
            return;
        if (_config.Selected is not { } provider || provider.Model.Trim().Length == 0
            || (provider.IsClaude ? provider.ApiKey.Trim().Length == 0 : provider.BaseUrl.Trim().Length == 0))
        {
            ShowProviderState();
            EmptyPanel.Visibility = Visibility.Visible;
            ShowSettings(true);
            return;
        }

        var chat = _chat;
        var question = new ChatMessage { Role = "user", Content = text };
        if (_attachments.Count > 0)
        {
            try
            {
                var images = _attachments.OfType<PendingImage>().ToList();
                var files = _attachments.OfType<PendingFile>().ToList();
                if (images.Count > 0)
                    question.Images = [.. images.Select(a => ChatStore.SaveImage(chat.Id, a.Bytes, a.Extension))];
                if (files.Count > 0)
                    question.Files = [.. files.Select(f => new ChatFile { Name = f.Name, Kind = f.Kind, Stored = ChatStore.SaveFile(chat.Id, f.Bytes, f.Extension) })];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                BarText.Text = $"Cannot save the attachments: {ex.Message}";
                return;
            }
            _attachments.Clear();
        }
        chat.Messages.Add(question);
        if (chat.Messages.Count(m => m.Role == "user") == 1)
            chat.Title = TitleFrom(text.Length > 0 ? text : question.Files is [var first, ..] ? first.Name : "Picture");
        chat.Modified = DateTime.Now;
        if (!_chats.Contains(chat))
            _chats.Insert(0, chat);
        else if (_chats.IndexOf(chat) > 0)
            _chats.Move(_chats.IndexOf(chat), 0);
        ChatList.SelectedItem = chat;
        CurrentChatId = chat.Id;
        RefreshList();
        SaveChat(chat);
        var run = RunOf(chat);
        run.Items.Add(new MessageItem(question));
        InputBox.Clear();
        ShowEmpty();
        ActiveChanged?.Invoke();
        _stickToBottom = true;

        var cancel = run.Cancel = new CancellationTokenSource();
        chat.Activity = "Answering...";
        RefreshList();
        ShowSending(true);
        try
        {
            var tools = chat.Workspace is { } root && Directory.Exists(root)
                ? new WorkspaceTools(root, chat.Id, request => ApproveAsync(chat, run, request), DeleteToRecycleBinAsync) { Token = cancel.Token }
                : null;
            var parts = new List<string>();
            if (_config.SystemPrompt.Trim().Length > 0)
                parts.Add(_config.SystemPrompt.Trim());
            parts.Add(AboutTheModel(provider));
            if (tools is not null)
                parts.Add(WorkspaceTools.Instructions(chat.Workspace!));
            string system = string.Join("\n\n", parts);
            for (int round = 0; round < MaxRounds; round++)
            {
                var answer = await StreamAnswerAsync(provider, system, chat, run, tools is null ? null : WorkspaceTools.Definitions, cancel.Token);
                if (answer.ToolCalls is not { Count: > 0 } calls || tools is null || answer.IsError)
                    break;
                foreach (var call in calls)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    run.PendingCard = null;
                    var outcome = await tools.RunAsync(call);
                    var result = new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = call.Id,
                        Content = outcome.Content,
                        Summary = outcome.Summary,
                        Change = outcome.Change,
                    };
                    chat.Messages.Add(result);
                    if (outcome.Change is { } change && run.PendingCard is { } card)
                        card.Finish(result, change); // The card that asked shows the decision (and Undo).
                    else if (outcome.Change is { } made)
                        run.Items.Add(MessageItem.ForChange(result, made));
                    else
                        run.Items.Add(MessageItem.ForActivity(result));
                    run.PendingCard = null;
                    SaveChat(chat);
                    ScrollDownSoon(chat);
                }
                if (round == MaxRounds - 1)
                    run.Items.Add(new MessageItem(new ChatMessage { Role = "assistant", Content = $"(stopped after {MaxRounds} rounds of tool use)", IsError = true }));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped: the last answer got "(stopped)" in StreamAnswerAsync; a waiting approval was refused.
        }
        finally
        {
            run.Cancel = null;
            chat.Activity = "";
            if (chat == _chat)
                ShowSending(false);
            if (run.PendingCard is { IsPending: true } waiting)
                waiting.Decide(false, "stopped");
            run.PendingCard = null;
            cancel.Dispose();
        }
        chat.Modified = DateTime.Now;
        SaveChat(chat);
        RefreshList();
        if (chat == _chat)
        {
            ScrollDownSoon(chat);
            FocusView();
        }
        else
        {
            _runs.Remove(chat); // Answered in the background: made again from its messages when it is opened.
        }
    }

    /// <summary>
    /// The line that tells the AI which model and effort it runs with: the API does not tell the model, so it could
    /// not answer when asked. (Changing either in a chat changes the instructions: Claude then leaves its earlier
    /// thinking of the chat out, as it does for a model change anyway.)
    /// </summary>
    private static string AboutTheModel(ChatProvider provider) =>
        $"You run in GemmaChat, the AI chat of the file manager DirectoryFable, as the model \"{provider.Model.Trim()}\" " +
        $"of the provider \"{provider.Name.Trim()}\", with the effort setting \"{EffortName(provider.Effort)}\".";

    /// <summary>An effort value as the bar shows it.</summary>
    public static string EffortName(string? effort) => (effort ?? "").Trim() switch
    {
        "" => "Default",
        "xhigh" => "Extra high",
        var other => char.ToUpperInvariant(other[0]) + other[1..],
    };

    /// <summary>One streamed answer of the AI, shown as it comes; added to the chat (with its tool calls, if any).</summary>
    private async Task<ChatMessage> StreamAnswerAsync(ChatProvider provider, string system, ChatThread chat, ChatRun run,
        System.Text.Json.JsonElement? tools, CancellationToken token)
    {
        var answer = new ChatMessage { Role = "assistant", Model = provider.Model.Trim(), Effort = provider.Effort.Trim() };
        var item = new MessageItem(answer) { Status = "Waiting for the answer..." };
        run.Items.Add(item);
        ScrollDownSoon(chat);
        var refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        refresh.Tick += (_, _) =>
        {
            item.Update(answer.Content);
            ScrollDownSoon(chat);
        };
        refresh.Start();
        bool stopped = false;
        try
        {
            await foreach (var piece in ChatClient.StreamAsync(provider, system, chat.Messages.ToList(), tools, token))
            {
                if (piece.Text is { } part)
                {
                    answer.Content += part;
                    item.Status = null;
                }
                if (piece.ToolCalls is { } calls)
                    answer.ToolCalls = [.. calls];
                if (piece.Blocks is { } blocks)
                    answer.Blocks = [.. blocks];
                if (piece.Model is { } model)
                    answer.Model = model;
                if (piece.Usage is { } usage)
                {
                    chat.ContextTokens = usage.Prompt + usage.Completion; // What the next request starts from.
                    chat.AnswerTokens = usage.Completion;
                    if (chat == _chat)
                        ShowProviderState();
                }
                if (piece.Reasoning && answer.Content.Length == 0)
                    item.Status = "Thinking...";
                if (piece.Notice is { } notice)
                {
                    answer.Content += (answer.Content.Length > 0 ? "\n\n" : "") + notice.Text;
                    if (notice.Refused)
                    {
                        // A declined turn is shown, but not sent again (nor its tools run).
                        answer.IsError = true;
                        answer.ToolCalls = null;
                        answer.Blocks = null;
                    }
                }
            }
            if (answer.Content.Length == 0 && answer.ToolCalls is null && !answer.IsError)
            {
                answer.Content = "(The answer was empty.)";
                answer.IsError = true;
            }
        }
        catch (OperationCanceledException)
        {
            answer.Content += answer.Content.Length > 0 ? "\n\n(stopped)" : "(stopped)";
            answer.ToolCalls = null;
            answer.Blocks = null; // An unfinished turn goes back as its text only.
            stopped = true;
        }
        catch (ChatApiException ex)
        {
            answer.IsError = true;
            answer.Content = ex.Status switch
            {
                401 or 403 => $"The provider refused the API key ({ex.Status}): {ex.Message}\n\nCheck the key on the settings page.",
                404 => $"Not found ({ex.Status}): {ex.Message}\n\nCheck the address and the model name on the settings page.",
                400 when tools is not null => $"The provider refused the request ({ex.Status}): {ex.Message}\n\n" +
                                              "If the model cannot use tools, choose another model, or close the folder (x in the bar).",
                0 => $"The provider reported an error: {ex.Message}",
                _ => $"The provider answered with an error ({ex.Status}): {ex.Message}",
            };
        }
        catch (HttpRequestException ex)
        {
            answer.IsError = true;
            answer.Content = $"Cannot reach {(provider.BaseUrl.Trim().Length > 0 ? provider.BaseUrl : provider.Name)}: {ex.Message}";
        }
        finally
        {
            refresh.Stop();
        }
        answer.Time = DateTime.Now;
        item.Status = null;
        item.Update(answer.Content);
        item.Refresh();
        if (answer.Content.Length == 0)
            run.Items.Remove(item); // Only tool calls: the steps show what happens.
        chat.Messages.Add(answer);
        SaveChat(chat);
        if (stopped)
            throw new OperationCanceledException(token);
        return answer;
    }

    // ---------- Approvals ----------

    /// <summary>
    /// Shows a change card and waits for the user's decision. Without asking (setting, or "Allow all in this chat")
    /// the card only reports it; deleting always asks; commands ask unless "Allow all commands in this chat" (the
    /// setting does not cover them: a command can do anything).
    /// </summary>
    private Task<(bool Allowed, string Decision)> ApproveAsync(ChatThread chat, ChatRun run, ChangeRequest request)
    {
        var card = MessageItem.ForRequest(request);
        run.PendingCard = card;
        run.Items.Add(card);
        ScrollDownSoon(chat);
        bool automatic = request.Kind switch
        {
            "delete" => false,
            "command" => run.AllowCommands,
            _ => _config.ChangesWithoutAsking || run.AllowAll,
        };
        if (automatic)
        {
            card.Decide(true, "allowed automatically");
            return Task.FromResult((true, "allowed automatically"));
        }
        // A stopped answer refuses the waiting change.
        run.Cancel?.Token.Register(() => Dispatcher.BeginInvoke(() => card.Decide(false, "stopped")));
        // The list shows that this chat waits (also while another chat is shown).
        chat.Activity = "Waits for your decision";
        RefreshList();
        _ = card.Decision.Task.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (run.IsAnswering)
                chat.Activity = "Answering...";
            RefreshList();
        }), TaskScheduler.Default);
        return card.Decision.Task;
    }

    private static MessageItem? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as MessageItem;

    private void Allow_Click(object sender, RoutedEventArgs e) => CardOf(sender)?.Decide(true, "allowed");

    private void Refuse_Click(object sender, RoutedEventArgs e) => CardOf(sender)?.Decide(false, "refused");

    private void AllowAll_Click(object sender, RoutedEventArgs e)
    {
        if (_chat is null)
            return;
        var run = RunOf(_chat);
        if (CardOf(sender) is { RequestKind: "command" } command)
        {
            run.AllowCommands = true;
            command.Decide(true, "allowed (all commands in this chat)");
            return;
        }
        run.AllowAll = true;
        CardOf(sender)?.Decide(true, "allowed (all in this chat)");
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { Change: { } change } card || _chat is null)
            return;
        try
        {
            WorkspaceTools.Undo(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BarText.Text = $"Cannot undo: {ex.Message}";
            return;
        }
        card.RefreshChange();
        // The AI learns it with the next message.
        _chat.Messages.Add(new ChatMessage { Role = "user", Content = $"(The user undid the change to {Path.GetFileName(change.Path)}: the file is as it was before.)" });
        SaveChat(_chat);
        BarText.Text = $"Undone: {change.Path}";
    }

    /// <summary>Deleting goes through DirectoryFable's own file operations: to the Recycle Bin, with Ctrl+Z in the explorer.</summary>
    private async Task<bool> DeleteToRecycleBinAsync(string path)
    {
        var owner = new WindowInteropHelper(Window.GetWindow(this)).Handle;
        return await Shell.FileOperations.DeleteAsync(owner, [path], permanent: false) && !File.Exists(path);
    }

    private static string TitleFrom(string text)
    {
        string line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > 50 ? line[..50].TrimEnd() + "..." : line;
    }

    private void ShowSending(bool sending)
    {
        SendText.Text = sending ? "Stop" : "Send";
        SendIcon.Text = sending ? "" : "";
        SendButton.ToolTip = sending ? "Stop the answer (Esc)" : "Send (Enter)";
        WorkspaceButton.IsEnabled = CloseWorkspaceButton.IsEnabled = !sending;
    }

    // ---------- Scrolling, copying, keys ----------

    private void MessagesScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Scrolled up by the user: the view stays there; back at the bottom: it follows the answer again.
        if (e.ExtentHeightChange == 0)
            _stickToBottom = MessagesScroller.VerticalOffset >= MessagesScroller.ScrollableHeight - 4;
    }

    /// <summary>Follows the answer down, when it is in the shown chat.</summary>
    private void ScrollDownSoon(ChatThread? chat = null)
    {
        if (_stickToBottom && (chat is null || chat == _chat))
            Dispatcher.BeginInvoke(() => MessagesScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatSegment segment)
            CopyText(segment.Text);
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MessageItem item)
            CopyText(item.Message.Content);
    }

    private void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            BarText.Text = "Copied to the clipboard";
        }
        catch (COMException)
        {
            BarText.Text = "The clipboard is busy; try again.";
        }
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SettingsPage.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ShowSettings(false);
            }
            return;
        }
        if (e.Key == Key.Escape && _chat is not null && _runs.TryGetValue(_chat, out var run) && run.Cancel is { } cancel)
        {
            e.Handled = true;
            cancel.Cancel();
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenChat(null);
        }
    }

    // ---------- Theme menu ----------

    private ChatTheme _theme = ChatTheme.Default;

    /// <summary>"Default", "Soft", "Midnight", "Warm": the look of the conversation. Kept in settings.json.</summary>
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ThemeButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = (double)FindResource("UiFontSize"),
        };
        foreach (var theme in ChatTheme.All)
        {
            var item = new MenuItem { Header = theme.Name, IsCheckable = true, IsChecked = theme == _theme };
            item.Click += (_, _) => SetTheme(theme);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void PreviousTheme_Click(object sender, RoutedEventArgs e) => StepTheme(-1);

    private void NextTheme_Click(object sender, RoutedEventArgs e) => StepTheme(1);

    /// <summary>The theme before / after the current one, in the menu's order; wraps at both ends.</summary>
    private void StepTheme(int step)
    {
        var all = ChatTheme.All;
        int at = Math.Max(0, all.ToList().IndexOf(_theme));
        SetTheme(all[(at + step + all.Count) % all.Count]);
    }

    private void SetTheme(ChatTheme theme)
    {
        if (theme == _theme)
            return;
        ApplyTheme(theme);
        try
        {
            AppSettings.Update(s => s.GemmaChatTheme = theme.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BarText.Text = $"The theme could not be saved: {ex.Message}";
        }
    }

    /// <summary>Replaces the view's themed brushes (DynamicResource in the XAML), so every message follows at once.</summary>
    private void ApplyTheme(ChatTheme theme)
    {
        static Brush Solid(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        _theme = theme;
        Resources["ChatBackBrush"] = Solid(theme.Background);
        Resources["SideBrush"] = Solid(theme.Side);
        Resources["BubbleBrush"] = Solid(theme.Bubble);
        Resources["UserBubbleBrush"] = Solid(theme.UserBubble);
        Resources["CodeBrush"] = Solid(theme.Code);
        Resources["ChatTextBrush"] = Solid(theme.Text);
        ThemeText.Text = theme.Name;
    }

    // ---------- Text size ----------

    private const double MinTextSize = 11, MaxTextSize = 20, DefaultTextSize = 13;

    private double _textSize = DefaultTextSize;

    private void SmallerText_Click(object sender, RoutedEventArgs e) => SetTextSize(_textSize - 1);

    private void LargerText_Click(object sender, RoutedEventArgs e) => SetTextSize(_textSize + 1);

    private void ResetTextSize_Click(object sender, RoutedEventArgs e) => SetTextSize(DefaultTextSize);

    private void SetTextSize(double size)
    {
        size = Math.Clamp(Math.Round(size), MinTextSize, MaxTextSize);
        if (size == _textSize)
            return;
        ApplyTextSize(size);
        try
        {
            AppSettings.Update(s => s.GemmaChatFontSize = size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BarText.Text = $"The text size could not be saved: {ex.Message}";
        }
    }

    /// <summary>Replaces the view's size resources (DynamicResource in the XAML): every message follows at once.</summary>
    private void ApplyTextSize(double size)
    {
        size = double.IsFinite(size) ? Math.Clamp(Math.Round(size), MinTextSize, MaxTextSize) : DefaultTextSize;
        _textSize = size;
        Resources["ChatFontSize"] = size;
        Resources["ChatCodeFontSize"] = size - 0.5;
        TextSizeText.Text = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---------- Settings ----------

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsButton.IsChecked == true);

    private void ShowSettings(bool open)
    {
        SettingsButton.IsChecked = open;
        if (open)
        {
            SettingsPage.Open(ChatStore.LoadConfig());
            SettingsPage.Visibility = Visibility.Visible;
        }
        else if (SettingsPage.Visibility == Visibility.Visible)
        {
            if (!SettingsPage.SaveIfChanged())
            {
                SettingsButton.IsChecked = true; // Not saved: the page stays, its message says why.
                return;
            }
            SettingsPage.Visibility = Visibility.Collapsed;
            FocusView();
        }
    }

    // ---------- Message items ----------

    public enum ItemKind { Text, Activity, Change }

    /// <summary>A row of the diff: its text and colors.</summary>
    public sealed record DiffRow(string Text, Brush Background, Brush Foreground);

    /// <summary>A message on screen: text with code blocks, a tool step, or a file change card.</summary>
    public sealed class MessageItem : INotifyPropertyChanged
    {
        private static readonly Brush AddedBack = Frozen(0x1E, 0x3A, 0x24), RemovedBack = Frozen(0x45, 0x1E, 0x1E);
        private static readonly Brush AddedText = Frozen(0xB5, 0xE8, 0xB0), RemovedText = Frozen(0xF0, 0xB0, 0xB0);
        private static readonly Brush PlainText = Frozen(0xC8, 0xC8, 0xC8), GapText = Frozen(0x80, 0x80, 0x80);

        private string? _status;
        private string _shown = "";

        public MessageItem(ChatMessage message)
        {
            Message = message;
            Update(message.Content);
            foreach (string name in message.Images ?? [])
            {
                if (ChatImages.Load(name, 480) is { } image)
                    Pictures.Add(new ChatPicture(image, ChatStore.ImagePath(name)));
            }
        }

        /// <summary>The message's pictures (decoded small; a click opens the file).</summary>
        public List<ChatPicture> Pictures { get; } = [];
        public bool HasPictures => Pictures.Count > 0;

        /// <summary>The message's attached files (name; a click opens the copy kept with the chat).</summary>
        public List<ChatFileChip> FileChips => [.. (Message.Files ?? []).Select(f => new ChatFileChip(f.Name, ChatStore.FilePath(f.Stored)))];
        public bool HasFiles => Message.Files is { Count: > 0 };

        public static MessageItem ForActivity(ChatMessage message) => new(message) { Kind = ItemKind.Activity };

        public static MessageItem ForChange(ChatMessage message, ChatChange change)
        {
            var item = new MessageItem(message) { Kind = ItemKind.Change, Change = change, ChangeTitle = TitleOf(change.Kind, change.Path) };
            item.SetDiff(change.Diff);
            return item;
        }

        /// <summary>A card that asks: Allow / Allow all / Refuse.</summary>
        public static MessageItem ForRequest(ChangeRequest request)
        {
            var item = new MessageItem(new ChatMessage { Role = "tool" })
            {
                Kind = ItemKind.Change,
                ChangeTitle = TitleOf(request.Kind, request.RelativePath),
                Pending = true,
                RequestKind = request.Kind,
            };
            item.SetDiff(request.Diff);
            return item;
        }

        private static string TitleOf(string kind, string path) => kind switch
        {
            "create" => $"Create {path}",
            "delete" => $"Delete {path}",
            "command" => "Run a command",
            _ => $"Change {path}",
        };

        public ItemKind Kind { get; init; }
        public ChatMessage Message { get; private set; }
        public bool IsUser => Message.Role == "user";
        public bool IsError => Message.IsError;
        public ObservableCollection<ChatSegment> Segments { get; } = [];
        /// <summary>The time; for an answer also its model and effort ("19.11.2026 17:30 · claude-opus-5-5 · High").</summary>
        public string TimeText => Message is { Role: "assistant", Model: { Length: > 0 } model }
            ? $"{Message.Time:g} · {model} · {EffortName(Message.Effort)}"
            : Message.Time.ToString("g");
        public string? Summary => Message.Summary;

        // ----- Change cards -----

        public ChatChange? Change { get; private set; }
        public string ChangeTitle { get; private set; } = "";
        public string? RequestKind { get; init; }
        public List<DiffRow> DiffRows { get; private set; } = [];
        private bool Pending { get; set; }
        public bool IsPending => Pending;
        public bool CanAllowAll => Pending && RequestKind != "delete";
        public string AllowText => RequestKind switch { "delete" => "Delete", "command" => "Run", _ => "Allow" };
        public string AllowAllText => RequestKind == "command" ? "Allow all commands in this chat" : "Allow all in this chat";
        public TaskCompletionSource<(bool Allowed, string Decision)> Decision { get; } = new();
        private string _decision = "";

        public string ChangeIcon => (Change?.Kind ?? RequestKind) switch { "create" => "", "delete" => "", "command" => "", _ => "" };

        public bool CanUndo => Change is { Undone: false, Decision: not "refused" and not "stopped" } c && (c.Backup is not null || c.Kind == "create")
                               && !Pending;

        public string DecisionText => Pending
                ? RequestKind == "command"
                    ? $"The AI wants to run this command in its folder, with {(IsAdministrator ? "administrator" : "your")} rights. Read it first."
                    : "The AI wants to make this change."
            : Change?.Undone == true ? $"{Capital(Change.Decision)} - undone"
            : Capital(Change?.Decision ?? _decision);

        public Brush FrameBrush => Pending ? (Brush)Application.Current.FindResource("GroupChevronBrush") : (Brush)Application.Current.FindResource("LineBrush");

        private static string Capital(string text) => text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];

        public void Decide(bool allowed, string decision)
        {
            if (!Pending)
                return;
            Pending = false;
            _decision = decision;
            Decision.TrySetResult((allowed, decision));
            RefreshChange();
        }

        /// <summary>The tool ran: the card now belongs to its message and shows the result (Undo when it changed something).</summary>
        public void Finish(ChatMessage message, ChatChange change)
        {
            Message = message;
            Change = change;
            if (change.Diff != _diffText)
            {
                SetDiff(change.Diff); // A command's card: the command, then what it printed.
                OnPropertyChanged(nameof(DiffRows));
            }
            RefreshChange();
        }

        public void RefreshChange()
        {
            foreach (string name in new[] { nameof(IsPending), nameof(CanAllowAll), nameof(CanUndo), nameof(DecisionText), nameof(FrameBrush), nameof(ChangeIcon) })
                OnPropertyChanged(name);
        }

        private string _diffText = "";

        /// <summary>DirectoryFable runs as administrator: a command card says so.</summary>
        private static readonly bool IsAdministrator =
            new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        private void SetDiff(string diff)
        {
            _diffText = diff;
            DiffRows = [.. diff.Split('\n').Select(row => row.Length == 0 ? new DiffRow(" ", Brushes.Transparent, PlainText) : row[0] switch
            {
                '+' => new DiffRow("+ " + row[1..], AddedBack, AddedText),
                '-' => new DiffRow("- " + row[1..], RemovedBack, RemovedText),
                '…' => new DiffRow("  " + row, Brushes.Transparent, GapText),
                _ => new DiffRow("  " + row[1..], Brushes.Transparent, PlainText),
            })];
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // ----- Text -----

        public string? Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStatus));
            }
        }

        public bool HasStatus => !string.IsNullOrEmpty(_status);

        /// <summary>New text: the pieces are made again (only when it changed).</summary>
        public void Update(string text)
        {
            if (text == _shown)
                return;
            _shown = text;
            var parts = ChatSegment.Split(text, rich: !IsUser);
            for (int i = 0; i < parts.Count; i++)
            {
                if (i < Segments.Count && Segments[i].IsCode == parts[i].IsCode && Segments[i].Language == parts[i].Language)
                    Segments[i].Text = parts[i].Text; // The same piece grown: no new controls (smooth streaming).
                else if (i < Segments.Count)
                    Segments[i] = parts[i];
                else
                    Segments.Add(parts[i]);
            }
            while (Segments.Count > parts.Count)
                Segments.RemoveAt(Segments.Count - 1);
        }

        /// <summary>The error mark and the time after the answer ended.</summary>
        public void Refresh()
        {
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(TimeText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A piece of a message: plain text, Markdown text (the AI's answers), or a code block (``` fences) with its language.</summary>
public sealed class ChatSegment : INotifyPropertyChanged
{
    private string _text = "";

    public bool IsCode { get; init; }
    public bool IsRich { get; init; }
    public string Language { get; init; } = "";

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
                return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Text and ``` code blocks in order; an open block (still streaming) runs to the end. Rich: the text parts are
    /// shown as Markdown (the user's own messages stay plain).
    /// </summary>
    public static List<ChatSegment> Split(string text, bool rich = false)
    {
        var result = new List<ChatSegment>();
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var current = new List<string>();
        bool code = false;
        string language = "";
        void Flush()
        {
            string body = string.Join("\n", current);
            if (!code)
                body = body.Trim('\n');
            if (body.Length > 0 || code)
                result.Add(new ChatSegment { IsCode = code, IsRich = rich && !code, Language = code ? (language.Length > 0 ? language : "code") : "", Text = body.TrimEnd('\n') });
            current.Clear();
        }
        foreach (string line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                if (!code)
                    language = line.TrimStart()[3..].Trim();
                code = !code;
                continue;
            }
            current.Add(line);
        }
        Flush();
        return result;
    }
}

/// <summary>Chooses the template of a message piece: text, Markdown text or code.</summary>
public sealed class ChatSegmentSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }
    public DataTemplate? Rich { get; set; }
    public DataTemplate? Code { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        ChatSegment { IsCode: true } => Code,
        ChatSegment { IsRich: true } => Rich ?? Text,
        _ => Text,
    };
}

/// <summary>Chooses the template of a chat row: a message, a tool step, or a file change card.</summary>
public sealed class ChatItemSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }
    public DataTemplate? Activity { get; set; }
    public DataTemplate? Change { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        GemmaChatView.MessageItem { Kind: GemmaChatView.ItemKind.Change } => Change,
        GemmaChatView.MessageItem { Kind: GemmaChatView.ItemKind.Activity } => Activity,
        _ => Text,
    };
}
