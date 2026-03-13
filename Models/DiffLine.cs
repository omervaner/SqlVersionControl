using Avalonia.Media;
using DiffPlex.DiffBuilder.Model;
using SqlVersionControl.Services;

namespace SqlVersionControl.Models;

public class DiffLine
{
    private List<HighlightedSegment>? _segments;

    public string LineNumber { get; set; } = "";
    public string Text { get; set; } = "";
    public ChangeType Type { get; set; }

    public IBrush Background => Type switch
    {
        ChangeType.Deleted => new SolidColorBrush(ThemeManager.GetDeletedBackground()),
        ChangeType.Inserted => new SolidColorBrush(ThemeManager.GetInsertedBackground()),
        ChangeType.Modified => new SolidColorBrush(ThemeManager.GetModifiedBackground()),
        ChangeType.Imaginary => new SolidColorBrush(ThemeManager.GetImaginaryBackground()),
        _ => Brushes.Transparent
    };

    public IBrush Foreground => Type switch
    {
        ChangeType.Deleted => new SolidColorBrush(ThemeManager.GetDeletedForeground()),
        ChangeType.Inserted => new SolidColorBrush(ThemeManager.GetInsertedForeground()),
        ChangeType.Imaginary => new SolidColorBrush(ThemeManager.GetImaginaryForeground()),
        _ => new SolidColorBrush(ThemeManager.GetDefaultForeground())
    };

    public int FontSize => ThemeManager.FontSize;

    public IBrush LineNumberBackground => new SolidColorBrush(ThemeManager.GetLineNumberBackground());
    public IBrush LineNumberForeground => new SolidColorBrush(ThemeManager.GetLineNumberForeground());

    // Syntax-highlighted segments
    public List<HighlightedSegment> Segments
    {
        get
        {
            if (_segments == null)
            {
                _segments = SqlSyntaxHighlighter.Highlight(Text);

                // For deleted/inserted/imaginary lines, override syntax colors
                if (Type == ChangeType.Imaginary)
                {
                    foreach (var seg in _segments)
                        seg.OverrideForeground = Foreground;
                }
            }
            return _segments;
        }
    }
}
