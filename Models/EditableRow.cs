using System.ComponentModel;

namespace SqlVersionControl.Models;

public enum RowEditState
{
    None,
    Modified,
    New,
    Deleted
}

public class EditableRow : INotifyPropertyChanged, IEditableObject
{
    public object?[] Values { get; }
    public object?[]? OriginalValues { get; private set; }
    public Type[] ColumnTypes { get; }

    private RowEditState _state;
    public RowEditState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public EditableRow(object?[] values, Type[] columnTypes, RowEditState state = RowEditState.None)
    {
        Values = values;
        ColumnTypes = columnTypes;
        _state = state;
    }

    public object? this[int index]
    {
        get => Values[index];
        set
        {
            if (value is string strVal)
            {
                if (string.IsNullOrEmpty(strVal))
                {
                    // Empty string → null for non-string types
                    Values[index] = index < ColumnTypes.Length && ColumnTypes[index] != typeof(string)
                        ? null
                        : strVal;
                }
                else if (index < ColumnTypes.Length && ColumnTypes[index] != typeof(string))
                {
                    Values[index] = ConvertValue(strVal, ColumnTypes[index]);
                }
                else
                {
                    Values[index] = strVal;
                }
            }
            else
            {
                Values[index] = value;
            }
        }
    }

    /// <summary>
    /// Take a snapshot of current values (idempotent — only captures the first time).
    /// </summary>
    public void TakeSnapshot()
    {
        if (OriginalValues == null)
            OriginalValues = (object?[])Values.Clone();
    }

    /// <summary>
    /// Compare current values to original snapshot.
    /// </summary>
    public bool HasChanges()
    {
        if (OriginalValues == null) return false;
        for (int i = 0; i < Values.Length; i++)
        {
            if (!Equals(Values[i], OriginalValues[i]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Get indices of columns that changed (for UPDATE SET clause).
    /// </summary>
    public List<int> GetChangedColumnIndices()
    {
        var changed = new List<int>();
        if (OriginalValues == null) return changed;
        for (int i = 0; i < Values.Length; i++)
        {
            if (!Equals(Values[i], OriginalValues[i]))
                changed.Add(i);
        }
        return changed;
    }

    /// <summary>
    /// Revert all values to original snapshot.
    /// </summary>
    public void Revert()
    {
        if (OriginalValues != null)
        {
            Array.Copy(OriginalValues, Values, Values.Length);
            OriginalValues = null;
        }
        State = RowEditState.None;
        // Notify indexer changed so DataGrid refreshes cells
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    // ── IEditableObject ─────────────────────────────────────────────

    void IEditableObject.BeginEdit() => TakeSnapshot();

    void IEditableObject.CancelEdit() => Revert();

    void IEditableObject.EndEdit()
    {
        if (State == RowEditState.New || State == RowEditState.Deleted)
            return; // New/Deleted rows keep their state

        if (HasChanges())
        {
            State = RowEditState.Modified;
        }
        else
        {
            State = RowEditState.None;
            OriginalValues = null;
        }
    }

    // ── Validation ───────────────────────────────────────────────────

    /// <summary>
    /// Returns error messages for cells where a string value doesn't match the column type.
    /// </summary>
    public List<(int ColumnIndex, string Error)> GetTypeErrors()
    {
        var errors = new List<(int, string)>();
        var indicesToCheck = State == RowEditState.New
            ? Enumerable.Range(0, Values.Length).ToList()
            : GetChangedColumnIndices();

        foreach (var i in indicesToCheck)
        {
            if (i >= ColumnTypes.Length) continue;
            if (Values[i] is string s && !string.IsNullOrEmpty(s) && ColumnTypes[i] != typeof(string))
            {
                errors.Add((i, $"'{s}' is not a valid {GetFriendlyTypeName(ColumnTypes[i])}"));
            }
        }
        return errors;
    }

    private static string GetFriendlyTypeName(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ? "integer" :
        t == typeof(decimal) || t == typeof(double) || t == typeof(float) ? "number" :
        t == typeof(bool) ? "boolean" :
        t == typeof(DateTime) || t == typeof(DateTimeOffset) ? "date/time" :
        t == typeof(Guid) ? "GUID" :
        t == typeof(TimeSpan) ? "time span" :
        t.Name.ToLower();

    // ── Type Conversion ─────────────────────────────────────────────

    private static object? ConvertValue(string text, Type targetType)
    {
        try
        {
            if (targetType == typeof(int)) return int.Parse(text);
            if (targetType == typeof(long)) return long.Parse(text);
            if (targetType == typeof(short)) return short.Parse(text);
            if (targetType == typeof(byte)) return byte.Parse(text);
            if (targetType == typeof(decimal)) return decimal.Parse(text);
            if (targetType == typeof(double)) return double.Parse(text);
            if (targetType == typeof(float)) return float.Parse(text);
            if (targetType == typeof(bool)) return text == "1" || bool.Parse(text);
            if (targetType == typeof(DateTime)) return DateTime.Parse(text);
            if (targetType == typeof(DateTimeOffset)) return DateTimeOffset.Parse(text);
            if (targetType == typeof(Guid)) return Guid.Parse(text);
            if (targetType == typeof(TimeSpan)) return TimeSpan.Parse(text);
            return Convert.ChangeType(text, targetType);
        }
        catch
        {
            return text; // Keep as string if conversion fails
        }
    }
}
