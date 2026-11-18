using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DirectoryFable.Models;
using DirectoryFable.Search;

namespace DirectoryFable.Views;

/// <summary>
/// Quick filter of a file pane: typing in the list (or in the filter box at the right end of the quick
/// bar) leaves only the names that match: words in any order, * and ? as wildcards, "quoted text"
/// (the same rules as SonnetSearch's names). The keyboard stays in the list, so arrows, Enter and the
/// file keys work on the matches. Backspace takes back a character, Esc shows all again; another
/// folder starts without a filter.
/// </summary>
public partial class FilePane
{
    /// <summary>Typing in the list: the characters go to the filter.</summary>
    private void FileList_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_viewModel is null || e.OriginalSource is TextBoxBase || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
            return;
        if (_viewModel.Filter.Length == 0 && string.IsNullOrWhiteSpace(e.Text))
            return; // A space alone starts no filter.
        e.Handled = true;
        _viewModel.Filter += e.Text;
    }

    private void FilterEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ClearFilter();
            FocusList();
        }
        else if (e.Key is Key.Enter or Key.Down)
        {
            e.Handled = true;
            FocusList();
        }
    }

    private void FilterClear_Click(object sender, RoutedEventArgs e)
    {
        ClearFilter();
        FocusList();
    }

    /// <summary>Esc: no filter. False when there was none.</summary>
    private bool ClearFilter()
    {
        if (_viewModel is not { Filter.Length: > 0 } tab)
            return false;
        tab.Filter = "";
        return true;
    }

    /// <summary>The filter text changed or the list was rebuilt: only the matching rows stay.</summary>
    private void ApplyFilter()
    {
        string text = _viewModel?.Filter ?? "";
        bool hadFocus = FileList.IsKeyboardFocusWithin;
        if (_view is not null)
        {
            if (text.Trim().Length == 0)
            {
                if (_view.Filter is not null)
                    _view.Filter = null;
            }
            else
            {
                var query = new SearchQuery { Text = text };
                query.Compile();
                _view.Filter = o => o is FileItem item && query.MatchesName(item.Name);
            }
        }
        UpdateFilterBox();
        // The row with the focus may be gone: the focus goes to a row that is still there.
        if (hadFocus)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (Keyboard.FocusedElement is not ListViewItem { DataContext: FileItem item } || !FileList.Items.Contains(item))
                    FocusList();
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Hint, count ("12 / 345"), clear button and the "nothing matches" line.</summary>
    private void UpdateFilterBox()
    {
        string text = _viewModel?.Filter ?? "";
        bool active = text.Length > 0;
        bool listShown = FileList.Visibility == Visibility.Visible;
        FilterBox.IsEnabled = listShown;
        FilterBox.Opacity = listShown ? 1 : 0.4;
        FilterHint.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        FilterClear.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        FilterIcon.Foreground = (Brush)FindResource(active ? "GroupChevronBrush" : "SecondaryTextBrush");
        FilterEdit.Padding = new Thickness(20, 0, active ? 74 : 6, 0);
        int shown = _view?.Count ?? 0;
        int all = _viewModel?.Items.Count ?? 0;
        FilterCount.Text = active ? $"{shown:N0} / {all:N0}" : "";
        bool nothing = active && listShown && shown == 0 && all > 0;
        FilterEmptyText.Text = nothing ? $"No names match \"{text}\".\nBackspace or Esc shows more again." : "";
        FilterEmptyText.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;
    }
}
