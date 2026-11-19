using System.Windows;

namespace DirectoryFable;

/// <summary>
/// GemmaChat, the seventh title bar view: chats with an AI through any OpenAI-compatible API (Views/GemmaChatView).
/// Its files are read when it is first shown.
/// </summary>
public partial class MainWindow
{
    private bool GemmaChatShown => GemmaChatPage.Visibility == Visibility.Visible;

    private void InitGemmaChat()
    {
        GemmaChatPage.ActiveChanged += UpdateTitle;
        // Explorer: "Open in GemmaChat" on a folder - a new chat that works in it.
        Views.FilePane.OpenInChat = folder =>
        {
            ShowView(MainView.Chat);
            GemmaChatPage.StartInFolder(folder);
        };
        // Explorer: "Send to GemmaChat" on files - attached to the next message of the shown chat.
        Views.FilePane.SendToChat = files =>
        {
            ShowView(MainView.Chat);
            GemmaChatPage.AttachFiles(files);
        };
    }

    private void GemmaChatTab_Click(object sender, RoutedEventArgs e) => ShowView(MainView.Chat);
}
