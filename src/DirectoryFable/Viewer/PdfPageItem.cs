using System.Windows;
using System.Windows.Media.Imaging;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Viewer;

/// <summary>
/// One page of the PDF that AstraViewer shows: its size at the current zoom (DIP) and its picture, drawn only while
/// the page is in view (and drawn again, sharper or smaller, after a zoom change).
/// </summary>
public sealed class PdfPageItem(int index, Size baseSize) : ObservableObject
{
    /// <summary>The box a page's thumbnail in the page rail fits into (DIP).</summary>
    public const double ThumbBoxWidth = 112, ThumbBoxHeight = 150;

    private double _width = baseSize.Width, _height = baseSize.Height;
    private BitmapSource? _bitmap, _thumbnail;
    private CancellationTokenSource? _drawing, _thumbDrawing;

    public int Index { get; } = index;

    /// <summary>"1", "2", ... under the thumbnail.</summary>
    public string Number => (Index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The page at 100 % in DIP.</summary>
    public Size BaseSize { get; } = baseSize;

    private double ThumbScale => Math.Min(ThumbBoxWidth / BaseSize.Width, ThumbBoxHeight / BaseSize.Height);

    public double ThumbWidth => Math.Round(BaseSize.Width * ThumbScale);

    public double ThumbHeight => Math.Round(BaseSize.Height * ThumbScale);

    /// <summary>The small page of the page rail (null until drawn).</summary>
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set => SetField(ref _thumbnail, value);
    }

    /// <summary>Draws the rail's thumbnail once (while its row is in view).</summary>
    public async void DrawThumbnail(PdfFile pdf, int pixelWidth)
    {
        if (_thumbnail is not null || _thumbDrawing is not null)
            return;
        var drawing = new CancellationTokenSource();
        _thumbDrawing = drawing;
        try
        {
            var bitmap = await pdf.RenderAsync(Index, pixelWidth, drawing.Token);
            if (_thumbDrawing == drawing)
                Thumbnail = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Stays white, like a page that cannot be drawn.
        }
        finally
        {
            if (_thumbDrawing == drawing)
                _thumbDrawing = null;
            drawing.Dispose();
        }
    }

    /// <summary>The thumbnail's row left the rail's view.</summary>
    public void ForgetThumbnail()
    {
        _thumbDrawing?.Cancel();
        _thumbDrawing = null;
        Thumbnail = null;
    }

    public double Width
    {
        get => _width;
        private set => SetField(ref _width, value);
    }

    public double Height
    {
        get => _height;
        private set => SetField(ref _height, value);
    }

    /// <summary>The drawn page (null until drawn; the white page shows meanwhile).</summary>
    public BitmapSource? Bitmap
    {
        get => _bitmap;
        private set => SetField(ref _bitmap, value);
    }

    /// <summary>The page is on screen (its row is realized).</summary>
    public bool IsShown { get; set; }

    private IReadOnlyList<Rect> _selection = [];
    private Task<PdfPageText>? _textLoad;

    /// <summary>The page's text, once read (<see cref="LoadTextAsync"/>); null before.</summary>
    public PdfPageText? Text { get; private set; }

    /// <summary>The selected text's highlight: rectangles in the page's 0..1 square.</summary>
    public IReadOnlyList<Rect> SelectionRects
    {
        get => _selection;
        set => SetField(ref _selection, value);
    }

    /// <summary>Reads the page's text once (for the text cursor and for selecting); a page without text gives an empty one.</summary>
    public async Task<PdfPageText> LoadTextAsync(PdfFile pdf)
    {
        if (Text is { } known)
            return known;
        try
        {
            Text = await (_textLoad ??= pdf.TextAsync(Index, CancellationToken.None));
        }
        catch (Exception)
        {
            Text = PdfPageText.Empty; // The PDF was closed meanwhile, or the page cannot be read.
        }
        return Text;
    }

    /// <summary>The page left the view and has no selection: its text is read again when it comes back.</summary>
    public void ForgetText()
    {
        Text = null;
        _textLoad = null;
    }

    public void SetZoom(double zoom)
    {
        Width = Math.Round(BaseSize.Width * zoom, 1);
        Height = Math.Round(BaseSize.Height * zoom, 1);
    }

    /// <summary>Draws the page <paramref name="pixelWidth"/> device pixels wide, unless it is drawn about that wide already.</summary>
    public async void Draw(PdfFile pdf, int pixelWidth)
    {
        if (_bitmap is { } current && Math.Abs(current.PixelWidth - pixelWidth) <= Math.Max(2, pixelWidth / 50))
            return;
        _drawing?.Cancel();
        var drawing = new CancellationTokenSource();
        _drawing = drawing;
        try
        {
            var bitmap = await pdf.RenderAsync(Index, pixelWidth, drawing.Token);
            if (_drawing == drawing && IsShown)
                Bitmap = bitmap;
        }
        catch (OperationCanceledException)
        {
            // Scrolled away or zoomed again before it was drawn.
        }
        catch (Exception)
        {
            // A page that cannot be drawn stays white; the other pages still show.
        }
        finally
        {
            if (_drawing == drawing)
                _drawing = null;
            drawing.Dispose();
        }
    }

    /// <summary>The page left the view: stop drawing it and let its picture go (memory).</summary>
    public void Forget()
    {
        _drawing?.Cancel();
        _drawing = null;
        Bitmap = null;
    }
}
