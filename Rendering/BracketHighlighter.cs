using Avalonia;
using Avalonia.Media;

namespace SqlVersionControl.Rendering;

/// <summary>
/// Highlights matching bracket pairs (parentheses) when cursor is adjacent.
/// </summary>
internal class BracketHighlighter : AvaloniaEdit.Rendering.DocumentColorizingTransformer
{
    public int OpenOffset { get; set; } = -1;
    public int CloseOffset { get; set; } = -1;

    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        if (OpenOffset < 0 || CloseOffset < 0) return;

        var brush = GetBracketHighlightBrush();

        HighlightIfOnLine(line, OpenOffset, brush);
        HighlightIfOnLine(line, CloseOffset, brush);
    }

    private void HighlightIfOnLine(AvaloniaEdit.Document.DocumentLine line, int offset, SolidColorBrush brush)
    {
        if (offset >= line.Offset && offset < line.EndOffset)
        {
            ChangeLinePart(offset, offset + 1, element =>
            {
                element.TextRunProperties.SetBackgroundBrush(brush);
            });
        }
    }

    private static SolidColorBrush GetBracketHighlightBrush()
    {
        if (Application.Current?.Resources.TryGetResource("BracketMatchBackground", null, out var res) == true
            && res is SolidColorBrush brush)
            return brush;
        return new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
    }
}
