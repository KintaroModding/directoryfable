using System.Windows;

namespace DirectoryFable;

/// <summary>
/// KeeSeek, the sixth title bar view: passwords (Views/KeeSeekView). It opens locked; leaving it locks it when its
/// timer is off; closing the window always locks it.
/// </summary>
public partial class MainWindow
{
    private bool KeeSeekShown => KeeSeekPage.Visibility == Visibility.Visible;

    private void InitKeeSeek()
    {
        KeeSeekPage.ActiveChanged += UpdateTitle;
    }

    private void KeeSeekTab_Click(object sender, RoutedEventArgs e) => ShowView(MainView.Vault);
}
