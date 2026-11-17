using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DirectoryFable.Models;
using DirectoryFable.ViewModels;

namespace DirectoryFable.Views;

/// <summary>Favorites bar: click opens in the active pane, files can be dropped on a favorite.</summary>
public partial class FavoritesBar : UserControl
{
    public FavoritesBar()
    {
        InitializeComponent();
        FavItems.Loaded += (_, _) => BindOverflowButton();
    }

    /// <summary>Open a folder in the active pane; bool = in a new tab.</summary>
    public event Action<string, bool>? OpenRequested;

    /// <summary>Current folder of the active pane (for "Add current folder").</summary>
    public Func<string?>? CurrentFolder { get; set; }

    private FavoritesViewModel? ViewModel => DataContext as FavoritesViewModel;

    // ---------- Open ----------

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Favorite favorite })
            OpenRequested?.Invoke(favorite.FullPath, false);
    }

    private void Favorite_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Middle-click: open in a new tab of the active pane.
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: Favorite favorite })
        {
            e.Handled = true;
            OpenRequested?.Invoke(favorite.FullPath, true);
        }
    }

    // ---------- Context menus ----------

    private static Favorite? MenuFavorite(object sender) => (sender as FrameworkElement)?.DataContext as Favorite;

    private void Menu_Open(object sender, RoutedEventArgs e)
    {
        if (MenuFavorite(sender) is { } f)
            OpenRequested?.Invoke(f.FullPath, false);
    }

    private void Menu_OpenNewTab(object sender, RoutedEventArgs e)
    {
        if (MenuFavorite(sender) is { } f)
            OpenRequested?.Invoke(f.FullPath, true);
    }

    private void Menu_Rename(object sender, RoutedEventArgs e)
    {
        if (MenuFavorite(sender) is { } f && InputDialog.Ask(Window.GetWindow(this), "Favorite name:", f.Name) is { } name)
            ViewModel?.Rename(f, name);
    }

    private void Menu_Remove(object sender, RoutedEventArgs e)
    {
        if (MenuFavorite(sender) is { } f)
            ViewModel?.Remove(f);
    }

    /// <summary>Right-click on empty bar space: add the active pane's folder.</summary>
    private void Bar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // A right-click on a favorite (or the » button) opens that button's own menu.
        // This handler sees the click first while it bubbles up, so it must stay out.
        if (e.Handled || IsInsideButton(e.OriginalSource) || CurrentFolder?.Invoke() is not { Length: > 0 } folder)
            return;
        e.Handled = true;

        string name = Favorite.NameFor(folder);
        bool exists = ViewModel?.Contains(folder) == true;
        var add = new MenuItem
        {
            Header = exists ? $"\"{name}\" is already a favorite" : $"Add \"{name}\" to favorites",
            IsEnabled = !exists,
        };
        add.Click += (_, _) => ViewModel?.Add(folder);
        new ContextMenu { Items = { add }, PlacementTarget = this, Placement = PlacementMode.MousePoint, IsOpen = true };
    }

    private bool IsInsideButton(object source)
    {
        for (var d = source as DependencyObject; d is not null && d != this; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ButtonBase)
                return true;
        }
        return false;
    }

    // ---------- Drag to reorder ----------

    private Favorite? _dragFavorite;
    private Point _dragStart;
    private bool _reordering;
    private DragGhost? _ghost;

    private void Favorite_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Favorite favorite })
        {
            _dragFavorite = favorite;
            _dragStart = e.GetPosition(FavItems);
            _reordering = false;
        }
    }

    private void Bar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFavorite is null || e.LeftButton != MouseButtonState.Pressed || ViewModel is not { } vm)
            return;

        Point pos = e.GetPosition(FavItems);
        if (!_reordering)
        {
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance)
                return;
            // Taking the mouse from the button also cancels its click.
            _reordering = true;
            CaptureMouse();

            // A picture of the favorite follows the mouse; the original spot is faded.
            if (FavItems.ItemContainerGenerator.ContainerFromItem(_dragFavorite) is FrameworkElement source)
                _ghost = new DragGhost(source, FavItems, grabPoint: FavItems.TranslatePoint(_dragStart, source));
            _dragFavorite.IsDragging = true;
        }
        _ghost?.Move(pos);

        // Move the dragged favorite into the slot under the mouse (live, like tabs).
        for (int i = 0; i < vm.Items.Count; i++)
        {
            if (FavItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container ||
                OverflowPanel.GetIsOverflowed(container))
                continue;
            double left = container.TranslatePoint(new Point(0, 0), FavItems).X;
            if (pos.X >= left && pos.X < left + container.ActualWidth)
            {
                vm.Move(_dragFavorite, i, save: false);
                break;
            }
        }
    }

    private void Bar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_reordering)
        {
            e.Handled = true;
            EndReorder();
        }
        _dragFavorite = null;
    }

    private void Bar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // LostMouseCapture bubbles: when the bar takes the mouse from the favorite button,
        // the *button's* loss arrives here too. Only the bar's own loss ends the drag.
        if (_reordering && ReferenceEquals(e.OriginalSource, this))
            EndReorder();
    }

    private void EndReorder()
    {
        _reordering = false;
        _ghost?.Close();
        _ghost = null;
        if (_dragFavorite is not null)
            _dragFavorite.IsDragging = false;
        _dragFavorite = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        ViewModel?.Save();
    }

    // ---------- Overflow "»" ----------

    private void BindOverflowButton()
    {
        if (FindChild<OverflowPanel>(FavItems) is not { } panel)
            return;
        OverflowButton.SetBinding(VisibilityProperty, new Binding(nameof(OverflowPanel.HasOverflow))
        {
            Source = panel,
            Converter = (IValueConverter)FindResource("BoolToVisibility"),
        });
    }

    private void Overflow_Click(object sender, RoutedEventArgs e)
    {
        // The button sits at the right window edge: align the menu's right edge with it,
        // so the menu opens inside the window instead of past the edge.
        var menu = new ContextMenu
        {
            PlacementTarget = OverflowButton,
            // The button uses the small icon font; the menu must not inherit it.
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = (double)FindResource("UiFontSize"),
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, target, _) =>
                [new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height), PopupPrimaryAxis.Horizontal)],
        };
        foreach (var favorite in ViewModel?.Items ?? [])
        {
            if (FavItems.ItemContainerGenerator.ContainerFromItem(favorite) is not UIElement container ||
                !OverflowPanel.GetIsOverflowed(container))
                continue;
            var item = new MenuItem { Header = favorite.Name, Icon = new Image { Source = favorite.Icon, Width = 13, Height = 13 } };
            item.Click += (_, _) => OpenRequested?.Invoke(favorite.FullPath, false);
            menu.Items.Add(item);
        }
        menu.IsOpen = menu.Items.Count > 0;
    }

    // ---------- Drag and drop ----------

    /// <summary>Folders dropped on empty bar space become favorites.</summary>
    private void Bar_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = FileDrop.GetPaths(e.Data) is { } paths && paths.All(Directory.Exists)
            ? DragDropEffects.Link
            : DragDropEffects.None;
    }

    private void Bar_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (FileDrop.GetPaths(e.Data) is not { } paths || ViewModel is null)
            return;
        foreach (string folder in paths.Where(Directory.Exists))
            ViewModel.Add(folder);
        e.Effects = DragDropEffects.None; // Nothing was moved or copied.
    }

    /// <summary>Files dropped on a favorite are copied/moved into its folder.</summary>
    private void Favorite_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: Favorite favorite } button || FileDrop.GetPaths(e.Data) is not { } paths)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = FileDrop.ChooseEffect(e, paths, favorite.FullPath);
        button.Background = (Brush)FindResource("SubtleHoverBrush");
    }

    private void Favorite_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button)
            button.ClearValue(BackgroundProperty);
    }

    private void Favorite_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: Favorite favorite } button)
            return;
        button.ClearValue(BackgroundProperty);
        if (FileDrop.GetPaths(e.Data) is { } paths)
            FileDrop.Execute(e, paths, favorite.FullPath, this);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindChild<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }
}
