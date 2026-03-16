using ClosedXML.Excel;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public static class ExportService
{
    public static void ExportToExcel(QueryResult result, string filePath)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Results");

        // Headers (row 1, bold)
        for (int c = 0; c < result.ColumnNames.Length; c++)
        {
            var cell = sheet.Cell(1, c + 1);
            cell.Value = result.ColumnNames[c];
            cell.Style.Font.Bold = true;
        }

        // Data rows
        for (int r = 0; r < result.Rows.Count; r++)
        {
            var row = result.Rows[r];
            for (int c = 0; c < row.Length && c < result.ColumnNames.Length; c++)
            {
                var val = row[c];
                if (val == null) continue; // Leave cell empty for NULLs

                var cell = sheet.Cell(r + 2, c + 1);
                switch (val)
                {
                    case int i: cell.Value = i; break;
                    case long l: cell.Value = l; break;
                    case short s: cell.Value = s; break;
                    case byte b: cell.Value = b; break;
                    case decimal d: cell.Value = d; break;
                    case double dbl: cell.Value = dbl; break;
                    case float f: cell.Value = f; break;
                    case bool bl: cell.Value = bl; break;
                    case DateTime dt: cell.Value = dt; break;
                    case DateTimeOffset dto: cell.Value = dto.DateTime; break;
                    default: cell.Value = val.ToString(); break;
                }
            }
        }

        // Freeze top row
        sheet.SheetView.FreezeRows(1);

        // Auto-size columns (cap at 50 characters width)
        sheet.Columns().AdjustToContents();
        foreach (var col in sheet.ColumnsUsed())
        {
            if (col.Width > 50)
                col.Width = 50;
        }

        workbook.SaveAs(filePath);
    }
}
