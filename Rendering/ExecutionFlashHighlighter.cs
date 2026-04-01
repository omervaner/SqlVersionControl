using Avalonia.Media;

namespace SqlVersionControl.Rendering;

/// <summary>
/// Temporary line transformer that flashes a highlight on the executed selection range.
/// </summary>
internal class ExecutionFlashHighlighter : AvaloniaEdit.Rendering.DocumentColorizingTransformer
{
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public Color FlashColor { get; set; } = Color.FromArgb(60, 100, 180, 255); // subtle blue

    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        if (StartOffset >= EndOffset) return;

        // Check if this line overlaps with the flash range
        var lineStart = line.Offset;
        var lineEnd = line.Offset + line.Length;

        var overlapStart = Math.Max(lineStart, StartOffset);
        var overlapEnd = Math.Min(lineEnd, EndOffset);

        if (overlapStart < overlapEnd)
        {
            ChangeLinePart(overlapStart, overlapEnd, element =>
            {
                element.TextRunProperties.SetBackgroundBrush(new SolidColorBrush(FlashColor));
            });
        }
    }
}
