using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace DirectoryFable.Editor;

/// <summary>
/// Notepad#'s word highlight, as in VS Code: a click into a word (or a selected word) marks every place of that word
/// in the file with a gray box. Whole words only, capital letters do not matter. Only the lines on screen are
/// searched while they are drawn, so large files stay fast.
/// </summary>
public sealed class WordMarker : IBackgroundRenderer
{
    private const int MaxWordLength = 100;

    private static readonly Brush Fill = Frozen(Color.FromRgb(0x44, 0x49, 0x50));

    private readonly TextArea _area;
    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private string _word = "";

    public WordMarker(TextArea area)
    {
        _area = area;
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            Update();
        };
        area.Caret.PositionChanged += (_, _) => Later();
        area.SelectionChanged += (_, _) => Later();
        area.DocumentChanged += (_, _) => Later();
    }

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>A short wait: the caret moves many times while keys are held or the mouse drags.</summary>
    private void Later()
    {
        _delay.Stop();
        _delay.Start();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The word to mark: a selected word (one line, word characters only), or the word around the caret.</summary>
    private void Update()
    {
        string word = "";
        var document = _area.Document;
        if (document is not null)
        {
            if (!_area.Selection.IsEmpty)
            {
                string selected = _area.Selection.GetText();
                if (selected.Length <= MaxWordLength && selected.Length > 0 && selected.All(IsWordChar))
                    word = selected;
            }
            else
            {
                int offset = _area.Caret.Offset;
                var line = document.GetLineByOffset(offset);
                string text = document.GetText(line);
                int at = offset - line.Offset, from = at, to = at;
                while (from > 0 && IsWordChar(text[from - 1]))
                    from--;
                while (to < text.Length && IsWordChar(text[to]))
                    to++;
                if (to - from is > 0 and <= MaxWordLength)
                    word = text[from..to];
            }
        }
        if (word == _word)
            return;
        _word = word;
        _area.TextView.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_word.Length == 0 || textView.Document is not { } document || !textView.VisualLinesValid)
            return;
        var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 0 };
        foreach (var visual in textView.VisualLines)
        {
            int start = visual.FirstDocumentLine.Offset, end = visual.LastDocumentLine.EndOffset;
            string text = document.GetText(start, end - start);
            int at = 0;
            while ((at = text.IndexOf(_word, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int after = at + _word.Length;
                bool whole = (at == 0 || !IsWordChar(text[at - 1])) && (after >= text.Length || !IsWordChar(text[after]));
                if (whole)
                {
                    builder.AddSegment(textView, new ICSharpCode.AvalonEdit.Document.TextSegment { StartOffset = start + at, Length = _word.Length });
                    builder.CloseFigure();
                }
                at = after;
            }
        }
        if (builder.CreateGeometry() is { } geometry)
            drawingContext.DrawGeometry(Fill, null, geometry);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
