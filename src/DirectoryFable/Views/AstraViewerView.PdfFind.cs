using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DirectoryFable.Viewer;

namespace DirectoryFable.Views;

/// <summary>
/// AstraViewer, text search in a PDF: the find bar (Ctrl+F), the hits of all pages (counted page by page in the
/// background), the marks on the pages in view, and the step to the next or previous hit (Enter / F3).
/// </summary>
public partial class AstraViewerView
{
    private readonly DispatcherTimer _findTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>The hits of each page (first and last character); null = that page is not searched yet.</summary>
    private IReadOnlyList<(int From, int To)>?[] _findHits = [];
    private string _findQuery = "";
    private int _findTotal;
    private (int Page, int Hit) _findCurrent = (-1, -1);
    private CancellationTokenSource? _findRun;
    private bool _findRunning;

    private void InitPdfFind()
    {
        // The search starts a moment after the last typed character.
        _findTimer.Tick += (_, _) =>
        {
            _findTimer.Stop();
            StartFind();
        };
    }

    private bool FindIsOpen => FindBar.Visibility == Visibility.Visible;

    /// <summary>Shows the find bar with the cursor in it. A short selection on one line is taken as the text to find.</summary>
    private void OpenFind()
    {
        if (_pdf is null)
            return;
        FindBar.Visibility = Visibility.Visible;
        string selected = SelectedPdfText().Trim();
        if (selected.Length is > 0 and <= 80 && !selected.Contains('\n'))
            FindBox.Text = selected;
        ShowFindCount();
        Dispatcher.BeginInvoke(() =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Ends the search: the bar and the marks go.</summary>
    private void CloseFind()
    {
        _findTimer.Stop();
        CancelFind();
        ClearFindMarks();
        _findQuery = "";
        _findHits = [];
        _findTotal = 0;
        _findCurrent = (-1, -1);
        if (!FindIsOpen)
            return;
        bool hadFocus = FindBar.IsKeyboardFocusWithin;
        FindBar.Visibility = Visibility.Collapsed;
        if (hadFocus)
            Focus();
    }

    private void CancelFind()
    {
        _findRun?.Cancel();
        _findRun = null;
        _findRunning = false;
    }

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (FindIsOpen)
            CloseFind();
        else
            OpenFind();
    }

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindStep(1);

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindStep(-1);

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _findTimer.Stop();
        _findTimer.Start();
    }

    private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter or Key.F3:
                e.Handled = true;
                FindStep(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
                break;
            case Key.Escape:
                e.Handled = true;
                CloseFind();
                break;
        }
    }

    /// <summary>
    /// Searches all pages for the typed text, one page at a time (so pages still draw between them). The first hit
    /// on or after the page in view is shown as soon as it is found; the count grows while the rest is searched.
    /// </summary>
    private async void StartFind()
    {
        CancelFind();
        ClearFindMarks();
        string query = PdfSearchText.Normalize(FindBox.Text);
        _findQuery = query;
        _findTotal = 0;
        _findCurrent = (-1, -1);
        var found = new IReadOnlyList<(int From, int To)>?[_pages.Count];
        _findHits = found;
        if (_pdf is not { } pdf || query.Length == 0 || !FindIsOpen)
        {
            _findQuery = FindIsOpen ? query : "";
            ShowFindCount();
            return;
        }

        var run = new CancellationTokenSource();
        _findRun = run;
        _findRunning = true;
        int start = _currentPage;
        ShowFindCount();
        try
        {
            for (int i = 0; i < found.Length; i++)
            {
                var hits = await pdf.FindAsync(i, query, matchCase: false, run.Token);
                if (_findRun != run)
                    return;
                found[i] = hits;
                if (hits.Count == 0)
                    continue;
                _findTotal += hits.Count;
                if (_findCurrent.Page < 0 && i >= start)
                    await ShowFindHitAsync(i, 0);
                else
                    ShowFindMarks(_pages[i]);
                if (_findRun != run)
                    return;
                ShowFindCount();
            }
            _findRunning = false;
            // Nothing on or after the page in view: the first hit of the document.
            if (_findCurrent.Page < 0 && _findTotal > 0)
                await ShowFindHitAsync(Array.FindIndex(found, hits => hits is { Count: > 0 }), 0);
            if (_findRun == run)
                ShowFindCount();
        }
        catch (OperationCanceledException)
        {
            // A new text was typed, or the bar was closed.
        }
        catch (ObjectDisposedException)
        {
            // The PDF was closed meanwhile.
        }
        finally
        {
            if (_findRun == run)
            {
                _findRun = null;
                _findRunning = false;
            }
            run.Dispose();
        }
    }

    /// <summary>"3 of 17" (with "+" while pages are still searched), "Searching...", or "No results".</summary>
    private void ShowFindCount()
    {
        bool none = _findQuery.Length > 0 && !_findRunning && _findTotal == 0;
        FindCountText.Foreground = (Brush)FindResource(none ? "ErrorTextBrush" : "SecondaryTextBrush");
        if (_findQuery.Length == 0)
            FindCountText.Text = "";
        else if (none)
            FindCountText.Text = "No results";
        else if (_findTotal == 0)
            FindCountText.Text = "Searching...";
        else
        {
            int rank = 0;
            if (_findCurrent.Page >= 0)
            {
                for (int i = 0; i < _findCurrent.Page && i < _findHits.Length; i++)
                    rank += _findHits[i]?.Count ?? 0;
                rank += _findCurrent.Hit + 1;
            }
            FindCountText.Text = $"{rank} of {_findTotal}{(_findRunning ? "+" : "")}";
        }
        FindPreviousButton.IsEnabled = FindNextButton.IsEnabled = _findTotal > 0;
    }

    /// <summary>Goes to the next (1) or previous (-1) hit, around the ends of the document.</summary>
    private void FindStep(int direction)
    {
        if (_pdf is null)
            return;
        if (!FindIsOpen)
        {
            OpenFind();
            return;
        }
        // Enter right after typing: the search for the new text starts now and shows its first hit.
        if (PdfSearchText.Normalize(FindBox.Text) != _findQuery)
        {
            _findTimer.Stop();
            StartFind();
            return;
        }
        if (_findTotal == 0 || _findHits.Length == 0)
            return;

        var (page, hit) = _findCurrent;
        if (page < 0)
        {
            page = Math.Clamp(_currentPage, 0, _findHits.Length - 1);
            hit = direction > 0 ? -1 : 0;
        }
        hit += direction;
        if (hit < 0 || _findHits[page] is not { } here || hit >= here.Count)
        {
            // Another page with hits, in the direction of the step, around the end.
            int count = _findHits.Length, next = -1;
            for (int step = 1; step <= count && next < 0; step++)
            {
                int candidate = ((page + direction * step) % count + count) % count;
                if (_findHits[candidate] is { Count: > 0 })
                    next = candidate;
            }
            if (next < 0)
                return;
            page = next;
            hit = direction > 0 ? 0 : _findHits[next]!.Count - 1;
        }
        _ = ShowFindHitAsync(page, hit);
    }

    /// <summary>Makes a hit the current one: it gets the strong mark and comes into view.</summary>
    private async Task ShowFindHitAsync(int page, int hit)
    {
        if (_pdf is not { } pdf || page < 0 || page >= _findHits.Length || page >= _pages.Count ||
            _findHits[page] is not { } hits || hit < 0 || hit >= hits.Count)
            return;
        var before = _findCurrent;
        _findCurrent = (page, hit);
        ShowFindCount();
        if (before.Page >= 0 && before.Page != page && before.Page < _pages.Count)
            ShowFindMarks(_pages[before.Page]);

        var item = _pages[page];
        if (page != _currentPage || PageTop(page) is null)
            GoToPage(page);
        string query = _findQuery;
        var text = await item.LoadTextAsync(pdf);
        if (_pdf != pdf || _findQuery != query || _findCurrent != (page, hit))
            return;
        ShowFindMarks(item);
        // Fit shows the page whole. At another zoom the hit itself must come into view.
        if (!_pdfFit)
            BringHitIntoView(item, text.Rects(hits[hit].From, hits[hit].To));
    }

    /// <summary>Scrolls so that the hit (rectangles in the page's 0..1 square) is inside the view, away from its edges.</summary>
    private void BringHitIntoView(PdfPageItem item, IReadOnlyList<Rect> rects)
    {
        if (rects.Count == 0 || PdfScroller() is not { } scroller)
            return;
        PdfPages.UpdateLayout();
        if (PdfPages.ItemContainerGenerator.ContainerFromIndex(item.Index) is not FrameworkElement row)
            return;
        Point corner;
        try
        {
            corner = row.TransformToAncestor(scroller).Transform(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            return; // Not connected to the scroll viewer (being recycled).
        }
        // The page stands in the middle of its row, below the row's top margin.
        double left = corner.X + (row.ActualWidth - item.Width) / 2 + rects[0].X * item.Width;
        double top = corner.Y + PageMargin.Top + rects[0].Y * item.Height;
        double width = rects[0].Width * item.Width, height = rects[0].Height * item.Height;
        const double Edge = 40;
        if (top < Edge || top + height > scroller.ViewportHeight - Edge)
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + top - scroller.ViewportHeight / 3);
        if (left < Edge || left + width > scroller.ViewportWidth - Edge)
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + left - scroller.ViewportWidth / 3);
    }

    /// <summary>Puts the marks of a page's hits on it (when it is in view): all hits light, the current one strong.</summary>
    private async void ShowFindMarks(PdfPageItem page)
    {
        if (_pdf is not { } pdf || page.Index >= _findHits.Length || _findHits[page.Index] is not { Count: > 0 } hits)
        {
            ClearFindMarks(page);
            return;
        }
        if (!page.IsShown)
            return;
        string query = _findQuery;
        var text = await page.LoadTextAsync(pdf);
        if (_pdf != pdf || _findQuery != query || page.Index >= _findHits.Length || _findHits[page.Index] != hits)
            return;
        var all = new List<Rect>();
        IReadOnlyList<Rect> current = [];
        for (int i = 0; i < hits.Count; i++)
        {
            var rects = text.Rects(hits[i].From, hits[i].To);
            if (_findCurrent == (page.Index, i))
                current = rects;
            else
                all.AddRange(rects);
        }
        page.FindRects = all;
        page.CurrentFindRects = current;
    }

    private void ClearFindMarks()
    {
        foreach (var page in _pages)
            ClearFindMarks(page);
    }

    private static void ClearFindMarks(PdfPageItem page)
    {
        if (page.FindRects.Count > 0)
            page.FindRects = [];
        if (page.CurrentFindRects.Count > 0)
            page.CurrentFindRects = [];
    }
}
