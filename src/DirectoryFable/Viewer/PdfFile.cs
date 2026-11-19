using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DirectoryFable.Viewer;

/// <summary>The PDF needs a password, or the given one is wrong.</summary>
public sealed class PdfPasswordException(bool wrong) : Exception(wrong ? "The password is wrong." : "The PDF is protected by a password.")
{
    /// <summary>True when a password was given and did not open the file.</summary>
    public bool Wrong { get; } = wrong;
}

/// <summary>
/// A PDF document for AstraViewer, read by PDFium (the PDF engine of Chromium): page sizes, pages as pictures, and
/// each page's text with a box per character (text selection). The file is read into memory first, so it is never
/// kept open or locked. PDFium is not thread-safe: every call runs on one background thread (<see cref="PdfWorker"/>).
/// </summary>
public sealed class PdfFile : IDisposable
{
    public const long MaxFileBytes = 1L << 30;

    /// <summary>Largest side of a rendered page in pixels (memory: 8192 x 8192 x 4 bytes = 256 MB at most).</summary>
    public const int MaxSide = 8192;

    private IntPtr _document; // FPDF_DOCUMENT (owned by the worker thread)
    private IntPtr _data;     // The file's bytes: PDFium reads from them as long as the document is open.

    private PdfFile(IntPtr document, IntPtr data, Size[] sizes)
    {
        _document = document;
        _data = data;
        PageSizes = sizes;
    }

    /// <summary>Each page's size at 100 % in DIP (1/96 inch; a PDF point is 1/72 inch), the page's rotation applied.</summary>
    public IReadOnlyList<Size> PageSizes { get; }

    public int PageCount => PageSizes.Count;

    /// <summary>Opens a PDF; <paramref name="password"/> null = none. Throws <see cref="PdfPasswordException"/> when one is needed.</summary>
    public static Task<PdfFile> OpenAsync(string path, string? password, CancellationToken token) =>
        PdfWorker.Run(() => Open(path, password, token), token);

    /// <summary>One page, <paramref name="pixelWidth"/> device pixels wide (the height follows the page), frozen.</summary>
    public Task<BitmapSource> RenderAsync(int index, int pixelWidth, CancellationToken token) =>
        PdfWorker.Run(() => Render(index, pixelWidth), token);

    /// <summary>The page's characters and where they stand (for text selection).</summary>
    public Task<PdfPageText> TextAsync(int index, CancellationToken token) =>
        PdfWorker.Run(() => Text(index), token);

    /// <summary>The first page about <paramref name="height"/> pixels high (thumbnails); call off the UI thread.</summary>
    public static BitmapSource FirstPage(string path, int height)
    {
        using var pdf = OpenAsync(path, PdfPasswords.For(path), CancellationToken.None).GetAwaiter().GetResult();
        var size = pdf.PageSizes[0];
        int width = (int)Math.Max(1, Math.Round(height * size.Width / size.Height));
        return pdf.RenderAsync(0, width, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        IntPtr document = Interlocked.Exchange(ref _document, IntPtr.Zero);
        IntPtr data = Interlocked.Exchange(ref _data, IntPtr.Zero);
        if (document == IntPtr.Zero)
            return;
        _ = PdfWorker.Run(() =>
        {
            Pdfium.FPDF_CloseDocument(document);
            Marshal.FreeHGlobal(data);
            return true;
        }, CancellationToken.None);
    }

    // ---------- On the worker thread ----------

    private static PdfFile Open(string path, string? password, CancellationToken token)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("The file no longer exists.", path);
        if (file.Length > MaxFileBytes)
            throw new InvalidDataException($"The file is too large for AstraViewer ({Infrastructure.DisplayFormat.Size(file.Length)}).");
        byte[] bytes = File.ReadAllBytes(path);
        token.ThrowIfCancellationRequested();

        IntPtr data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        IntPtr document = IntPtr.Zero;
        try
        {
            Marshal.Copy(bytes, 0, data, bytes.Length);
            bytes = [];
            byte[]? secret = string.IsNullOrEmpty(password) ? null : Encoding.UTF8.GetBytes(password + "\0");
            document = Pdfium.FPDF_LoadMemDocument64(data, (nuint)file.Length, secret);
            if (document == IntPtr.Zero)
            {
                throw Pdfium.FPDF_GetLastError() switch
                {
                    Pdfium.ErrorPassword => new PdfPasswordException(wrong: secret is not null),
                    Pdfium.ErrorSecurity => new InvalidDataException("The PDF uses a protection that cannot be opened."),
                    _ => new InvalidDataException("The file is not a PDF that can be read (it is damaged, or not a PDF at all)."),
                };
            }

            int count = Pdfium.FPDF_GetPageCount(document);
            if (count <= 0)
                throw new InvalidDataException("The PDF has no pages.");
            var sizes = new Size[count];
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                // Points to DIP; a page that reports no size gets A4, so it still shows.
                sizes[i] = Pdfium.FPDF_GetPageSizeByIndex(document, i, out double width, out double height) != 0 && width > 0 && height > 0
                    ? new Size(width * 96 / 72, height * 96 / 72)
                    : new Size(793.7, 1122.5);
            }
            var pdf = new PdfFile(document, data, sizes);
            document = IntPtr.Zero;
            data = IntPtr.Zero;
            return pdf;
        }
        finally
        {
            if (document != IntPtr.Zero)
                Pdfium.FPDF_CloseDocument(document);
            if (data != IntPtr.Zero)
                Marshal.FreeHGlobal(data);
        }
    }

    private unsafe BitmapSource Render(int index, int pixelWidth)
    {
        IntPtr document = _document;
        ObjectDisposedException.ThrowIf(document == IntPtr.Zero, this);
        var size = PageSizes[index];
        int width = Math.Clamp(pixelWidth, 1, MaxSide);
        int height = (int)Math.Clamp(Math.Round(width * size.Height / size.Width), 1, MaxSide);
        int stride = width * 4;
        var pixels = new byte[(long)stride * height];

        IntPtr page = Pdfium.FPDF_LoadPage(document, index);
        if (page == IntPtr.Zero)
            throw new InvalidDataException("The page cannot be read.");
        try
        {
            fixed (byte* first = pixels)
            {
                IntPtr bitmap = Pdfium.FPDFBitmap_CreateEx(width, height, Pdfium.FormatBgrx, (IntPtr)first, stride);
                if (bitmap == IntPtr.Zero)
                    throw new OutOfMemoryException("No memory for the page.");
                try
                {
                    Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF); // White paper.
                    Pdfium.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, Pdfium.RenderAnnotations);
                }
                finally
                {
                    Pdfium.FPDFBitmap_Destroy(bitmap);
                }
            }
        }
        finally
        {
            Pdfium.FPDF_ClosePage(page);
        }
        var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        picture.Freeze();
        return picture;
    }

    private PdfPageText Text(int index)
    {
        IntPtr document = _document;
        ObjectDisposedException.ThrowIf(document == IntPtr.Zero, this);
        IntPtr page = Pdfium.FPDF_LoadPage(document, index);
        if (page == IntPtr.Zero)
            return PdfPageText.Empty;
        IntPtr text = IntPtr.Zero;
        try
        {
            text = Pdfium.FPDFText_LoadPage(page);
            int count = text == IntPtr.Zero ? 0 : Pdfium.FPDFText_CountChars(text);
            if (count <= 0)
                return PdfPageText.Empty;
            var codes = new int[count];
            var boxes = new Rect[count];
            // Page points to a 0..1 square from the page's top left corner (PDFium applies rotation and the crop box).
            const int Unit = 1_000_000;
            for (int i = 0; i < count; i++)
            {
                codes[i] = (int)Pdfium.FPDFText_GetUnicode(text, i);
                if (Pdfium.FPDFText_GetCharBox(text, i, out double left, out double right, out double bottom, out double top) == 0 ||
                    right <= left || top <= bottom)
                    continue; // A space or line end that PDFium added: no box.
                Pdfium.FPDF_PageToDevice(page, 0, 0, Unit, Unit, 0, left, top, out int x1, out int y1);
                Pdfium.FPDF_PageToDevice(page, 0, 0, Unit, Unit, 0, right, bottom, out int x2, out int y2);
                boxes[i] = new Rect(new Point((double)x1 / Unit, (double)y1 / Unit), new Point((double)x2 / Unit, (double)y2 / Unit));
            }
            var size = PageSizes[index];
            return new PdfPageText(codes, boxes, size.Width / size.Height);
        }
        finally
        {
            if (text != IntPtr.Zero)
                Pdfium.FPDFText_ClosePage(text);
            Pdfium.FPDF_ClosePage(page);
        }
    }
}

/// <summary>Passwords typed for PDFs in this run (never saved): the same file opens again without asking, and gets its thumbnail.</summary>
public static class PdfPasswords
{
    private static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);

    public static string? For(string path) => Known.GetValueOrDefault(path);

    public static void Remember(string path, string password) => Known[path] = password;

    public static void Forget(string path) => Known.TryRemove(path, out _);
}

/// <summary>The one thread that talks to PDFium; jobs run in order.</summary>
internal static class PdfWorker
{
    private static readonly BlockingCollection<Action> Jobs = [];
    private static readonly Lock Gate = new();
    private static Thread? _thread;

    public static Task<T> Run<T>(Func<T> job, CancellationToken token)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (_thread is null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "PDF" };
                _thread.Start();
            }
        }
        Jobs.Add(() =>
        {
            // A page that scrolled away before its turn is not drawn.
            if (token.IsCancellationRequested)
            {
                done.TrySetCanceled(token);
                return;
            }
            try
            {
                done.TrySetResult(job());
            }
            catch (OperationCanceledException)
            {
                done.TrySetCanceled(token);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    private static void Loop()
    {
        try
        {
            Pdfium.FPDF_InitLibrary();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // pdfium.dll is missing or does not fit: every job then fails the same way, and AstraViewer shows that text.
        }
        foreach (var job in Jobs.GetConsumingEnumerable())
            job();
    }
}

/// <summary>PDFium's C API (pdfium.dll), only what AstraViewer uses.</summary>
internal static class Pdfium
{
    private const string Library = "pdfium";

    public const uint ErrorPassword = 4, ErrorSecurity = 5;
    public const int FormatBgrx = 3;
    public const int RenderAnnotations = 0x01;

    [DllImport(Library)]
    public static extern void FPDF_InitLibrary();

    [DllImport(Library)]
    public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, byte[]? password);

    [DllImport(Library)]
    public static extern uint FPDF_GetLastError();

    [DllImport(Library)]
    public static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Library)]
    public static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(Library)]
    public static extern int FPDF_GetPageSizeByIndex(IntPtr document, int index, out double width, out double height);

    [DllImport(Library)]
    public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);

    [DllImport(Library)]
    public static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Library)]
    public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);

    [DllImport(Library)]
    public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(Library)]
    public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY,
        int rotate, int flags);

    [DllImport(Library)]
    public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(Library)]
    public static extern IntPtr FPDFText_LoadPage(IntPtr page);

    [DllImport(Library)]
    public static extern void FPDFText_ClosePage(IntPtr textPage);

    [DllImport(Library)]
    public static extern int FPDFText_CountChars(IntPtr textPage);

    [DllImport(Library)]
    public static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);

    [DllImport(Library)]
    public static extern int FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom,
        out double top);

    [DllImport(Library)]
    public static extern int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate,
        double pageX, double pageY, out int deviceX, out int deviceY);
}
