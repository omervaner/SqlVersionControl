using Avalonia.Media;

namespace SqlVersionControl.Rendering;

/// <summary>
/// AvaloniaEdit line transformer that highlights all occurrences of the selected word.
/// </summary>
internal class OccurrenceHighlighter : AvaloniaEdit.Rendering.DocumentColorizingTransformer
{
    public string? SelectedWord { get; set; }
    public Color HighlightColor { get; set; } = Color.FromRgb(61, 53, 32);

    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        if (string.IsNullOrEmpty(SelectedWord)) return;

        var lineText = CurrentContext.Document.GetText(line.Offset, line.Length);
        var wordLen = SelectedWord.Length;
        var idx = 0;

        while (idx <= lineText.Length - wordLen)
        {
            var pos = lineText.IndexOf(SelectedWord, idx, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) break;

            // Whole-word check
            var before = pos > 0 ? lineText[pos - 1] : ' ';
            var after = pos + wordLen < lineText.Length ? lineText[pos + wordLen] : ' ';
            if (!IsWordBoundary(before) || !IsWordBoundary(after))
            {
                idx = pos + 1;
                continue;
            }

            var startOffset = line.Offset + pos;
            var endOffset = startOffset + wordLen;

            ChangeLinePart(startOffset, endOffset, element =>
            {
                element.TextRunProperties.SetBackgroundBrush(new SolidColorBrush(HighlightColor));
            });

            idx = pos + wordLen;
        }
    }

    private static bool IsWordBoundary(char c)
        => !char.IsLetterOrDigit(c) && c != '_' && c != '#' && c != '@';
}
