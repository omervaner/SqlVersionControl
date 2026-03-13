using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace SqlVersionControl.Behaviors;

/// <summary>
/// Global behavior: double-click a column header's right edge to auto-fit width.
/// Call Initialize() once at startup — applies to ALL DataGrids automatically.
/// </summary>
public static class DataGridAutoFitBehavior
{
    public static void Initialize()
    {
        InputElement.DoubleTappedEvent.AddClassHandler<DataGridColumnHeader>(
            OnHeaderDoubleTapped);
    }

    private static void OnHeaderDoubleTapped(DataGridColumnHeader header, TappedEventArgs e)
    {
        // Only trigger near the right edge (resize zone, ~6px)
        var pos = e.GetPosition(header);
        if (pos.X < header.Bounds.Width - 6) return;

        var grid = header.FindAncestorOfType<DataGrid>();
        if (grid == null) return;

        // Map visual header position to column index
        var headers = grid.GetVisualDescendants()
            .OfType<DataGridColumnHeader>()
            .ToList();
        var colIndex = headers.IndexOf(header);
        if (colIndex < 0 || colIndex >= grid.Columns.Count) return;

        AutoFitColumn(grid, colIndex, header);
        e.Handled = true;
    }

    private static void AutoFitColumn(DataGrid grid, int colIndex, DataGridColumnHeader header)
    {
        double maxWidth = 0;

        // Measure header text
        var headerTb = header.FindDescendantOfType<TextBlock>();
        if (headerTb != null && !string.IsNullOrEmpty(headerTb.Text))
            maxWidth = MeasureText(headerTb.Text, headerTb.FontSize, headerTb.FontFamily) + 28;

        // Measure visible row cells
        foreach (var row in grid.GetVisualDescendants().OfType<DataGridRow>())
        {
            var cells = row.GetVisualDescendants().OfType<DataGridCell>().ToList();
            if (colIndex >= cells.Count) continue;

            var tb = cells[colIndex].FindDescendantOfType<TextBlock>();
            if (tb != null && !string.IsNullOrEmpty(tb.Text))
                maxWidth = Math.Max(maxWidth, MeasureText(tb.Text, tb.FontSize, tb.FontFamily) + 18);
        }

        if (maxWidth > 20)
            grid.Columns[colIndex].Width = new DataGridLength(Math.Ceiling(Math.Min(maxWidth, 800)));
    }

    private static double MeasureText(string text, double fontSize, Avalonia.Media.FontFamily fontFamily)
    {
        var tb = new TextBlock { Text = text, FontSize = fontSize, FontFamily = fontFamily };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return tb.DesiredSize.Width;
    }
}
