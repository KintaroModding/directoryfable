using System.Windows;

namespace DirectoryFable;

/// <summary>
/// FlashSync, the eighth title bar view: backup jobs that copy ticked folders into a destination folder
/// (Views/FlashSyncView). Its jobs are read when it is first shown; a running job goes on while another view shows.
/// </summary>
public partial class MainWindow
{
    private bool FlashSyncShown => FlashSyncPage.Visibility == Visibility.Visible;

    private void InitFlashSync()
    {
        FlashSyncPage.ActiveChanged += UpdateTitle;
        FlashSyncPage.ActiveChanged += ShowFlashSyncRunning;
        // "Show destination": the explorer goes into the folder.
        FlashSyncPage.ShowFolderRequested += async folder =>
        {
            ShowView(MainView.Explorer);
            if (ActivePane.SelectedTab is { } tab)
                await tab.NavigateAsync(folder);
            ActivePaneView.FocusList();
        };
        // "Log": the run's log file opens in Notepad#.
        FlashSyncPage.OpenInNotepadRequested += async file =>
        {
            ShowNotepad(true);
            await NotepadPage.OpenFilesAsync([file]);
        };
    }

    /// <summary>While a job runs, the tab's icon (circular arrows) turns, so the run shows from every view.</summary>
    private void ShowFlashSyncRunning()
    {
        if (FlashSyncTabIcon.RenderTransform is not System.Windows.Media.RotateTransform turn)
            return;
        if (FlashSyncPage.IsRunning)
        {
            if (turn.HasAnimatedProperties)
                return;
            turn.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                });
            FlashSyncTab.ToolTip = "FlashSync (backup jobs): a job is running";
        }
        else
        {
            turn.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            turn.Angle = 0;
            FlashSyncTab.ToolTip = "FlashSync (backup jobs)";
        }
    }

    private void FlashSyncTab_Click(object sender, RoutedEventArgs e) => ShowView(MainView.Sync);
}
