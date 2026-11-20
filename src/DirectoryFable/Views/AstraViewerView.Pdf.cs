using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DirectoryFable.Infrastructure;
using DirectoryFable.Viewer;

namespace DirectoryFable.Views;

/// <summary>
/// AstraViewer for PDF files: the pages one below the other, drawn by PDFium only while in view. Text is selected by
/// dragging over it (double-click: a word, triple-click: a line, Ctrl+A: the page), Ctrl+C copies it. A PDF with a
/// password asks for it on the stage.
/// Fit (the start): every page by its own size as large as the window allows, so each page shows whole; the wheel,
/// Up / Down and Page Up / Page Down then go a whole page back or on. Zoomed (+, -, Ctrl+wheel at the mouse, 100 % =
/// the paper's real size), the wheel scrolls. The bar shows the page in view. Left / Right still go to the previous /
/// next file of the folder, as for images. On the right, the page rail: all pages small, the page in view marked and
/// kept in its upper part (the next pages show below it); a click goes to a page. Long PDFs scroll in the rail.
/// </summary>
public partial class AstraViewerView
{
    /// <summary>Space around each page (the gap between pages is twice the vertical part).</summary>
    public static readonly Thickness PageMargin = new(12, 6, 12, 6);

    private PdfFile? _pdf;
    private readonly ObservableCollection<PdfPageItem> _pages = [];
    private double _pdfZoom = 1;
    private bool _pdfFit = true;
    private ScrollViewer? _pdfScroller;
    private readonly System.Windows.Threading.DispatcherTimer _pdfDrawTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private int _currentPage;
    private int _wheelRest; // Fit: wheel movement not yet a whole notch (touchpads send small steps).
    private bool _railSyncing; // The rail's selection is set by code (the page in view), not by a click.
    private ScrollViewer? _railScroller;

    private void InitPdf()
    {
        PdfPages.ItemsSource = _pages;
        PageThumbs.ItemsSource = _pages;
        // After a zoom step, the pages in view are drawn again at the new size (a moment later: zoom steps come fast).
        _pdfDrawTimer.Tick += (_, _) =>
        {
            _pdfDrawTimer.Stop();
            DrawShownPages();
        };
        InitPdfFind();
    }

    private bool IsPdfShown => _pdf is not null;

    /// <summary>Shows an opened PDF (the image, if any, goes): the first page at the top, the start zoom of the settings.</summary>
    private void ShowPdf(PdfFile pdf)
    {
        ClosePdf();
        _pdf = pdf;
        _image = null;
        Picture.Source = null;
        Picture.Visibility = Checker.Visibility = Visibility.Collapsed;
        MessageText.Text = "";
        _pdfFit = _startZoom == "fit";
        _pdfZoom = 1;
        _currentPage = 0;
        for (int i = 0; i < pdf.PageCount; i++)
            _pages.Add(new PdfPageItem(i, pdf.PageSizes[i]));
        PdfPages.Visibility = Visibility.Visible;
        PageBar.Visibility = Visibility.Visible;
        PageRail.Visibility = pdf.PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        RotateButton.IsEnabled = false;
        ApplyPdfZoom();
        PdfScroller()?.ScrollToHome();
        UpdatePageText();
    }

    /// <summary>The PDF goes (another file is shown, or none): its pages and the renderer's document are let go.</summary>
    private void ClosePdf()
    {
        _pdfDrawTimer.Stop();
        CloseFind();
        ClearTextSelection();
        foreach (var page in _pages)
        {
            page.IsShown = false;
            page.Forget();
            page.ForgetThumbnail();
        }
        _railSyncing = true;
        _pages.Clear();
        _railSyncing = false;
        _pdf?.Dispose();
        _pdf = null;
        PdfPages.Visibility = Visibility.Collapsed;
        PageBar.Visibility = Visibility.Collapsed;
        PageRail.Visibility = Visibility.Collapsed;
        RotateButton.IsEnabled = true;
    }

    private ScrollViewer? PdfScroller()
    {
        if (_pdfScroller is null)
        {
            PdfPages.ApplyTemplate();
            _pdfScroller = PdfPages.Template?.FindName("PdfScroller", PdfPages) as ScrollViewer;
        }
        return _pdfScroller;
    }

    // ---------- Zoom ----------

    /// <summary>Fit: the whole page in the window, by this page's own size (a small page is enlarged too).</summary>
    private double PdfFitZoom(int index)
    {
        if (_pdf is null || index < 0 || index >= _pdf.PageCount)
            return 1;
        var scroller = PdfScroller();
        double width = (scroller?.ViewportWidth is > 0 and var viewWidth ? viewWidth : PdfPages.ActualWidth)
                       - PageMargin.Left - PageMargin.Right - 4;
        double height = (scroller?.ViewportHeight is > 0 and var viewHeight ? viewHeight : PdfPages.ActualHeight)
                        - PageMargin.Top - PageMargin.Bottom;
        if (width <= 0 || height <= 0)
            return 1;
        var size = _pdf.PageSizes[index];
        return Math.Clamp(Math.Min(width / size.Width, height / size.Height), MinZoom, MaxZoom);
    }

    /// <summary>The zoom of the page in view (in Fit, each page has its own).</summary>
    private double ShownPdfZoom => _pdfFit ? PdfFitZoom(_currentPage) : _pdfZoom;

    /// <summary>Sizes every page for the current zoom (Fit: each page for itself) and shows the zoom value.</summary>
    private void ApplyPdfZoom()
    {
        for (int i = 0; i < _pages.Count; i++)
            _pages[i].SetZoom(_pdfFit ? PdfFitZoom(i) : _pdfZoom);
        ZoomText.Text = ZoomPercent(ShownPdfZoom);
        _pdfDrawTimer.Stop();
        _pdfDrawTimer.Start();
    }

    /// <summary>Fit after a size change: the pages get their new size and the page in view stands at the top again.</summary>
    private void RefitPdf()
    {
        if (!_pdfFit || _pdf is null)
            return;
        int page = _currentPage;
        ApplyPdfZoom();
        Dispatcher.BeginInvoke(() => GoToPage(page), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>A new zoom; the point under <paramref name="anchorY"/> (viewport, default: its middle) stays in place.</summary>
    private void PdfZoomTo(double zoom, bool fit = false, double? anchorY = null)
    {
        if (_pdf is null || PdfScroller() is not { } scroller)
            return;
        double y = anchorY ?? scroller.ViewportHeight / 2;
        var (index, fraction) = PageAt(y);
        _pdfFit = fit;
        _pdfZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _wheelRest = 0;
        ApplyPdfZoom();
        PdfPages.UpdateLayout();
        if (fit)
        {
            GoToPage(index); // Fit shows that page whole, from its top.
            return;
        }
        // The same spot of the same page back under the anchor.
        BringPageIntoView(index);
        if (PageTop(index) is double top)
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + top + fraction * _pages[index].Height - y);
    }

    private void PdfZoomStep(int steps, double? anchorY = null) => PdfZoomTo(ShownPdfZoom * Math.Pow(1.25, steps), anchorY: anchorY);

    /// <summary>Ctrl+wheel = zoom at the mouse. In Fit, the wheel goes a whole page back or on; zoomed, it scrolls.</summary>
    private void PdfPages_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PdfScroller() is not { } scroller)
            return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            PdfZoomStep(Math.Sign(e.Delta), e.GetPosition(scroller).Y);
            return;
        }
        if (!_pdfFit || Keyboard.Modifiers != ModifierKeys.None)
            return;
        e.Handled = true;
        _wheelRest += e.Delta;
        while (_wheelRest >= 120)
        {
            _wheelRest -= 120;
            GoToPage(_currentPage - 1);
        }
        while (_wheelRest <= -120)
        {
            _wheelRest += 120;
            GoToPage(_currentPage + 1);
        }
    }

    private void PdfPages_SizeChanged(object sender, SizeChangedEventArgs e) => RefitPdf();

    private void PdfScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // A scroll bar that comes or goes changes the room for Fit.
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
            RefitPdf();
        UpdatePageText();
    }

    // ---------- Pages in view ----------

    private void PdfPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PdfPageItem page })
        {
            page.IsShown = true;
            DrawPage(page);
            if (_pdf is { } pdf)
                _ = page.LoadTextAsync(pdf); // For the text cursor and for selecting.
            ShowFindMarks(page);
        }
    }

    private void PdfPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PdfPageItem page })
        {
            page.IsShown = false;
            page.Forget();
            if (page != _textPage)
                page.ForgetText();
        }
    }

    // ---------- Text selection ----------

    private PdfPageItem? _textPage; // The page with the selection (one page at a time).
    private int _textFrom = -1, _textTo = -1;
    private bool _textDragging, _textMoved;

    private bool HasTextSelection => _textPage is not null && _textFrom >= 0;

    /// <summary>The mouse on a page as a point in the page's 0..1 square.</summary>
    private static Point OnPage(object sender, MouseEventArgs e)
    {
        var area = sender is Border { Child: FrameworkElement child } ? child : (FrameworkElement)sender;
        var at = e.GetPosition(area);
        return new Point(area.ActualWidth > 0 ? at.X / area.ActualWidth : 0, area.ActualHeight > 0 ? at.Y / area.ActualHeight : 0);
    }

    private void PdfPage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (sender is not FrameworkElement { DataContext: PdfPageItem page } element)
            return;
        e.Handled = true;
        if (page.Text is not { IsEmpty: false } text)
        {
            ClearTextSelection(); // A page without text (a scan), or its text is not read yet.
            return;
        }
        var point = OnPage(sender, e);
        int hit = text.Hit(point);
        if (e.ClickCount >= 2 && hit >= 0)
        {
            var (from, to) = e.ClickCount == 2 ? text.WordAt(hit) : text.LineAt(hit);
            SelectText(page, from, to);
            return;
        }
        ClearTextSelection();
        _textPage = page;
        _textFrom = _textTo = hit >= 0 ? hit : text.Nearest(point);
        _textDragging = true;
        _textMoved = false;
        element.CaptureMouse();
    }

    private void PdfPage_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PdfPageItem page } element || page.Text is not { IsEmpty: false } text)
            return;
        var point = OnPage(sender, e);
        if (_textDragging && page == _textPage)
        {
            int to = text.Nearest(point);
            if (to >= 0 && (to != _textTo || !_textMoved) && (to != _textFrom || _textMoved))
            {
                _textTo = to;
                _textMoved = true;
                ShowTextSelection();
            }
            return;
        }
        element.Cursor = text.Hit(point) >= 0 ? Cursors.IBeam : null;
    }

    private void PdfPage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_textDragging)
            return;
        (sender as UIElement)?.ReleaseMouseCapture();
        EndTextDrag();
    }

    private void PdfPage_LostMouseCapture(object sender, MouseEventArgs e) => EndTextDrag();

    private void EndTextDrag()
    {
        if (!_textDragging)
            return;
        _textDragging = false;
        if (!_textMoved)
            ClearTextSelection(); // A plain click selects nothing.
    }

    private void SelectText(PdfPageItem page, int from, int to)
    {
        ClearTextSelection();
        _textPage = page;
        _textFrom = from;
        _textTo = to;
        ShowTextSelection();
    }

    private void ShowTextSelection()
    {
        if (_textPage is { Text: { } text } page && _textFrom >= 0)
            page.SelectionRects = text.Rects(Math.Min(_textFrom, _textTo), Math.Max(_textFrom, _textTo));
    }

    private void ClearTextSelection()
    {
        if (_textPage is { } page)
        {
            page.SelectionRects = [];
            if (!page.IsShown)
                page.ForgetText();
        }
        _textPage = null;
        _textFrom = _textTo = -1;
        _textDragging = false;
    }

    private string SelectedPdfText() =>
        _textPage is { Text: { } text } && _textFrom >= 0 ? text.Text(Math.Min(_textFrom, _textTo), Math.Max(_textFrom, _textTo)) : "";

    /// <summary>Ctrl+A: all text of the page in view.</summary>
    private async void SelectPageText()
    {
        if (_pdf is not { } pdf || _currentPage >= _pages.Count)
            return;
        var page = _pages[_currentPage];
        var text = await page.LoadTextAsync(pdf);
        if (_pdf == pdf && !text.IsEmpty)
            SelectText(page, 0, text.Count - 1);
    }

    /// <summary>Ctrl+C on a PDF: the selected text; without a selection the page in view as a picture.</summary>
    private void CopyFromPdf()
    {
        string text = SelectedPdfText();
        if (text.Length == 0)
        {
            CopyPdfPage();
            return;
        }
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy (another program holds it).
        }
    }

    // ---------- Password ----------

    private string? _passwordPath;
    private bool _passwordAddsToHistory;

    /// <summary>The PDF needs a password (or the typed one was wrong): the box on the stage asks for it.</summary>
    private void AskPdfPassword(string path, bool wrong, bool addToHistory)
    {
        _passwordPath = path;
        _passwordAddsToHistory = addToHistory;
        PdfPasswordTitle.Text = $"{Path.GetFileName(path)} is protected. Type its password:";
        PdfPasswordProblem.Text = wrong ? "The password is wrong." : "";
        PdfPasswordProblem.Visibility = wrong ? Visibility.Visible : Visibility.Collapsed;
        PdfPasswordBox.Clear();
        PdfPasswordPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => PdfPasswordBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void HidePdfPassword()
    {
        _passwordPath = null;
        PdfPasswordBox.Clear();
        PdfPasswordPanel.Visibility = Visibility.Collapsed;
    }

    private void PdfPasswordOpen_Click(object sender, RoutedEventArgs e) => OpenWithPassword();

    private void PdfPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenWithPassword();
        }
    }

    private void OpenWithPassword()
    {
        if (_passwordPath is not { } path || PdfPasswordBox.Password.Length == 0)
            return;
        PdfPasswords.Remember(path, PdfPasswordBox.Password);
        bool add = _passwordAddsToHistory;
        HidePdfPassword();
        Focus();
        _ = ShowImageAsync(path, addToHistory: add);
    }

    private void DrawShownPages()
    {
        foreach (var page in _pages.Where(p => p.IsShown))
            DrawPage(page);
    }

    /// <summary>Draws a page at the screen's pixel size for its zoom (at most about 40 megapixels).</summary>
    private void DrawPage(PdfPageItem page)
    {
        if (_pdf is null)
            return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double width = page.Width * scale, height = page.Height * scale;
        const double MaxPixels = 40_000_000;
        if (width * height > MaxPixels)
            width *= Math.Sqrt(MaxPixels / (width * height));
        page.Draw(_pdf, (int)Math.Clamp(Math.Round(width), 1, PdfFile.MaxSide));
    }

    /// <summary>The realized page rows with their top in the scroll viewer's viewport (DIP).</summary>
    private IEnumerable<(int Index, double Top, FrameworkElement Row)> ShownRows()
    {
        if (PdfScroller() is not { } scroller)
            yield break;
        for (int i = 0; i < _pages.Count; i++)
        {
            if (!_pages[i].IsShown || PdfPages.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row || !row.IsVisible)
                continue;
            double top;
            try
            {
                top = row.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y + PageMargin.Top;
            }
            catch (InvalidOperationException)
            {
                continue; // Not connected to the scroll viewer (being recycled).
            }
            yield return (i, top, row);
        }
    }

    private double? PageTop(int index)
    {
        foreach (var row in ShownRows())
        {
            if (row.Index == index)
                return row.Top;
        }
        return null;
    }

    /// <summary>The page under <paramref name="y"/> (viewport DIP) and how far down it the point is (0 top .. 1 bottom).</summary>
    private (int Index, double Fraction) PageAt(double y)
    {
        (int Index, double Fraction) best = (_currentPage, 0);
        double bestDistance = double.MaxValue;
        foreach (var (index, top, _) in ShownRows())
        {
            double height = Math.Max(1, _pages[index].Height);
            double distance = y < top ? top - y : y > top + height ? y - top - height : 0;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = (index, Math.Clamp((y - top) / height, 0, 1));
            }
        }
        return best;
    }

    private void BringPageIntoView(int index)
    {
        if (index < 0 || index >= _pages.Count)
            return;
        if (PdfPages.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row)
        {
            row.BringIntoView();
        }
        else if (FindChild<VirtualizingStackPanel>(PdfPages) is { } panel)
        {
            panel.BringIndexIntoViewPublic(index); // Not realized yet: the panel scrolls it into the list first.
        }
        PdfPages.UpdateLayout();
    }

    /// <summary>Shows a page from its top (Page Up / Page Down, the page buttons, Home / End; in Fit also the wheel).</summary>
    private void GoToPage(int index)
    {
        if (_pdf is null || PdfScroller() is not { } scroller)
            return;
        index = Math.Clamp(index, 0, _pages.Count - 1);
        BringPageIntoView(index);
        if (PageTop(index) is double top)
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + top - PageMargin.Top);
        // The scroll lands with the next layout pass: the page is known now (the scroll event confirms it).
        ShowPage(index);
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e) => GoToPage(_currentPage - 1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(_currentPage + 1);

    /// <summary>"3 / 12": the page that fills the upper part of the view.</summary>
    private void UpdatePageText()
    {
        if (_pdf is null || PdfScroller() is not { } scroller)
            return;
        if (scroller.VerticalOffset <= 0)
            ShowPage(0);
        else if (scroller.VerticalOffset >= scroller.ScrollableHeight - 0.5)
            ShowPage(_pages.Count - 1);
        else
            ShowPage(PageAt(scroller.ViewportHeight / 3).Index);
    }

    private void ShowPage(int index)
    {
        bool moved = index != _currentPage || PageThumbs.SelectedIndex != index;
        _currentPage = index;
        PageText.Text = $"{index + 1} / {_pages.Count}";
        PreviousPageButton.IsEnabled = index > 0;
        NextPageButton.IsEnabled = index < _pages.Count - 1;
        ZoomText.Text = ZoomPercent(ShownPdfZoom); // In Fit, pages of other sizes have another zoom.
        if (moved)
            FollowInRail(index);
    }

    // ---------- Page rail ----------

    /// <summary>Marks the page in the rail and keeps it in the rail's upper quarter (the coming pages show below).</summary>
    private void FollowInRail(int index)
    {
        if (PageRail.Visibility != Visibility.Visible || index < 0 || index >= _pages.Count)
            return;
        _railSyncing = true;
        try
        {
            PageThumbs.SelectedIndex = index;
            PageThumbs.ScrollIntoView(_pages[index]);
            PageThumbs.UpdateLayout();
            _railScroller ??= FindChild<ScrollViewer>(PageThumbs);
            if (_railScroller is { } rail && PageThumbs.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row)
            {
                double top = row.TransformToAncestor(rail).Transform(new Point(0, 0)).Y;
                rail.ScrollToVerticalOffset(rail.VerticalOffset + top - rail.ViewportHeight / 4);
            }
        }
        catch (InvalidOperationException)
        {
            // The row is not connected to the rail's scroll viewer this moment; the selection is set anyway.
        }
        finally
        {
            _railSyncing = false;
        }
    }

    private void PageThumbs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_railSyncing && PageThumbs.SelectedIndex >= 0)
            GoToPage(PageThumbs.SelectedIndex);
    }

    private void PageThumb_Loaded(object sender, RoutedEventArgs e)
    {
        if (_pdf is { } pdf && sender is FrameworkElement { DataContext: PdfPageItem page })
            page.DrawThumbnail(pdf, (int)Math.Ceiling(page.ThumbWidth * VisualTreeHelper.GetDpi(this).DpiScaleX));
    }

    private void PageThumb_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PdfPageItem page })
            page.ForgetThumbnail();
    }

    // ---------- Keys, details, copy ----------

    /// <summary>The PDF's own keys; false = not a PDF key (the image keys apply).</summary>
    private bool PdfKey(Key key, ModifierKeys modifiers)
    {
        if (_pdf is null || PdfScroller() is not { } scroller)
            return false;
        bool plain = modifiers == ModifierKeys.None;
        switch (key)
        {
            case Key.PageUp when plain:
                GoToPage(_currentPage - (PageTop(_currentPage) is double top && top < -1 ? 0 : 1));
                return true;
            case Key.PageDown when plain:
                GoToPage(_currentPage + 1);
                return true;
            case Key.Home when plain:
                scroller.ScrollToHome();
                return true;
            case Key.End when plain:
                scroller.ScrollToEnd();
                return true;
            case Key.Up when plain:
                if (_pdfFit)
                    GoToPage(_currentPage - 1);
                else
                    scroller.ScrollToVerticalOffset(scroller.VerticalOffset - 60);
                return true;
            case Key.Down when plain:
                if (_pdfFit)
                    GoToPage(_currentPage + 1);
                else
                    scroller.ScrollToVerticalOffset(scroller.VerticalOffset + 60);
                return true;
            case Key.F when modifiers == ModifierKeys.Control:
                OpenFind();
                return true;
            case Key.F3 when plain || modifiers == ModifierKeys.Shift:
                FindStep(plain ? 1 : -1);
                return true;
            case Key.F when plain:
                PdfZoomTo(1, fit: true);
                return true;
            case Key.D1 or Key.NumPad1 when plain:
            case Key.D0 or Key.NumPad0 when modifiers == ModifierKeys.Control:
                PdfZoomTo(1);
                return true;
            case Key.OemPlus or Key.Add:
                PdfZoomStep(1);
                return true;
            case Key.OemMinus or Key.Subtract:
                PdfZoomStep(-1);
                return true;
            case Key.R:
                return true; // No rotation for PDFs.
            case Key.A when modifiers == ModifierKeys.Control:
                SelectPageText();
                return true;
            case Key.Escape when HasTextSelection:
                ClearTextSelection();
                return true;
            case Key.Escape when FindIsOpen:
                CloseFind();
                return true;
            default:
                return false;
        }
    }

    /// <summary>"name.pdf · 12 pages · A4 (210 × 297 mm) · PDF · 1,2 MB · modified ...".</summary>
    private void UpdatePdfDetails(string path)
    {
        if (_pdf is null)
            return;
        var parts = new List<string> { Path.GetFileName(path), _pdf.PageCount == 1 ? "1 page" : $"{_pdf.PageCount:N0} pages" };
        var first = _pdf.PageSizes[0];
        double widthMm = first.Width / 96 * 25.4, heightMm = first.Height / 96 * 25.4;
        string paper = PaperName(widthMm, heightMm) is { } name ? $"{name} " : "";
        parts.Add($"{paper}({widthMm:0} × {heightMm:0} mm)");
        if (_pdf.PageSizes.Any(s => Math.Abs(s.Width - first.Width) > 1 || Math.Abs(s.Height - first.Height) > 1))
            parts.Add("pages of different sizes");
        parts.Add("PDF");
        try
        {
            var file = new FileInfo(path);
            parts.Add(DisplayFormat.Size(file.Length));
            parts.Add($"modified {DisplayFormat.Date(file.LastWriteTime)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        DetailsText.Text = string.Join("   ·   ", parts);
        DetailsText.ToolTip = string.Join("\n", parts);
    }

    /// <summary>The usual paper names (either orientation, 2 mm tolerance).</summary>
    private static string? PaperName(double widthMm, double heightMm)
    {
        double small = Math.Min(widthMm, heightMm), large = Math.Max(widthMm, heightMm);
        (string Name, double Small, double Large)[] papers =
        [
            ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210), ("A6", 105, 148),
            ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6), ("Tabloid", 279.4, 431.8),
        ];
        foreach (var paper in papers)
        {
            if (Math.Abs(small - paper.Small) <= 2 && Math.Abs(large - paper.Large) <= 2)
                return paper.Name;
        }
        return null;
    }

    /// <summary>Ctrl+C on a PDF: the page in view as a picture (as drawn now).</summary>
    private void CopyPdfPage()
    {
        if (_pdf is null || _currentPage >= _pages.Count || _pages[_currentPage].Bitmap is not { } page)
            return;
        try
        {
            Clipboard.SetImage(page);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy (another program holds it).
        }
    }
}
