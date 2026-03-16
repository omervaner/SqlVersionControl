using System.Text;
using ClosedXML.Excel;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public static class ExportService
{
    public static void ExportToExcel(QueryResult result, string filePath)
        => ExportToExcel(result.ColumnNames, result.ColumnTypes, result.Rows, filePath);

    public static void ExportToExcel(string[] columnNames, Type[] columnTypes,
        List<object?[]> rows, string filePath)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Results");

        // Headers (row 1, bold)
        for (int c = 0; c < columnNames.Length; c++)
        {
            var cell = sheet.Cell(1, c + 1);
            cell.Value = columnNames[c];
            cell.Style.Font.Bold = true;
        }

        // Data rows
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (int c = 0; c < row.Length && c < columnNames.Length; c++)
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

    public static string GenerateInsertStatements(
        string schema, string table, string[] columnNames, Type[] columnTypes, List<object?[]> rows)
    {
        var sb = new StringBuilder();
        var columnList = string.Join(", ", columnNames.Select(c => $"[{c}]"));

        foreach (var row in rows)
        {
            sb.Append($"INSERT INTO [{schema}].[{table}] ({columnList})\nVALUES (");
            for (int i = 0; i < row.Length && i < columnNames.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(FormatSqlLiteral(row[i], i < columnTypes.Length ? columnTypes[i] : typeof(string)));
            }
            sb.AppendLine(");");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatSqlLiteral(object? value, Type type)
    {
        if (value == null) return "NULL";

        // Numeric types — no quotes
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) ||
            type == typeof(byte) || type == typeof(decimal) || type == typeof(double) ||
            type == typeof(float))
            return value.ToString()!;

        // Boolean
        if (type == typeof(bool))
            return (bool)value ? "1" : "0";

        // DateTime
        if (value is DateTime dt)
            return $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'";
        if (value is DateTimeOffset dto)
            return $"'{dto:yyyy-MM-dd HH:mm:ss.fff zzz}'";

        // Byte arrays (binary)
        if (value is byte[] bytes)
            return "0x" + Convert.ToHexString(bytes);

        // Everything else — single-quoted string, escape single quotes
        var str = value.ToString() ?? "";
        return $"'{str.Replace("'", "''")}'";
    }
}
