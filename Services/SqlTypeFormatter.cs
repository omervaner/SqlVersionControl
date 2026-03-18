namespace SqlVersionControl.Services;

/// <summary>
/// Single source of truth for formatting SQL Server column type strings.
/// Used by ObjectExplorerViewModel (column display) and TableCompareService (DDL generation).
/// </summary>
public static class SqlTypeFormatter
{
    /// <summary>
    /// Formats a SQL Server type name with its size/precision parameters.
    /// </summary>
    /// <param name="typeName">Base type name (e.g. "nvarchar", "decimal")</param>
    /// <param name="maxLength">max_length from sys.columns (-1 = MAX)</param>
    /// <param name="precision">precision from sys.columns (for decimal/numeric)</param>
    /// <param name="scale">scale from sys.columns (for decimal/numeric, datetime2, time)</param>
    public static string Format(string typeName, int maxLength, byte precision = 0, byte scale = 0)
    {
        var upper = typeName.ToUpperInvariant();

        // String/binary types that use max_length
        if (upper is "NVARCHAR" or "NCHAR")
            return maxLength == -1 ? $"{typeName}(MAX)" : $"{typeName}({maxLength / 2})";
        if (upper is "VARCHAR" or "CHAR" or "VARBINARY" or "BINARY")
            return maxLength == -1 ? $"{typeName}(MAX)" : $"{typeName}({maxLength})";

        // Precision + scale types
        if (upper is "DECIMAL" or "NUMERIC")
            return $"{typeName}({precision},{scale})";

        // Precision-only types (fractional seconds)
        if (upper is "DATETIME2" or "TIME" or "DATETIMEOFFSET")
            return scale != 7 ? $"{typeName}({scale})" : typeName; // 7 is default, omit it

        return typeName;
    }
}
