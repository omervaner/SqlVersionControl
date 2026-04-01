using Avalonia.Input;
using AvaloniaEdit.CodeCompletion;

namespace SqlVersionControl.Views;

public partial class QueryTabView
{
    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow == null || string.IsNullOrEmpty(e.Text)) return;

        var ch = e.Text[0];
        if (!char.IsLetterOrDigit(ch) && ch != '_')
        {
            _completionWindow.CompletionList.RequestInsertion(e);
            _completionWindow = null; // Clear immediately, don't wait for Closed event
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_intellisenseService == null || string.IsNullOrEmpty(e.Text)) return;
        if (_isAutocompleteEnabled?.Invoke() == false) return;

        var ch = e.Text[0];
        if (!char.IsLetter(ch) && ch != '.') return;

        ShowCompletionWindow(ch == '.');
    }

    private void ShowCompletionWindow(bool isDot = false)
    {
        if (_intellisenseService == null) return;

        // Close any existing window and clear the reference
        if (_completionWindow != null)
        {
            _completionWindow.Close();
            _completionWindow = null;
        }

        var text = SqlEditor.Text;
        var offset = SqlEditor.CaretOffset;

        var completions = _intellisenseService.GetCompletions(text, offset);
        if (completions.Count == 0) return;

        _completionWindow = new CompletionWindow(SqlEditor.TextArea);

        if (!isDot)
        {
            var wordStart = offset;
            while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '_'))
                wordStart--;
            _completionWindow.StartOffset = wordStart;
        }

        _completionWindow.Closed += (_, _) =>
        {
            SqlEditor.TextChanged -= OnTextChangedWhileCompleting;
            _completionWindow = null;
        };

        foreach (var item in completions)
            _completionWindow.CompletionList.CompletionData.Add(item);

        _completionWindow.Show();
        SqlEditor.TextChanged += OnTextChangedWhileCompleting;
    }

    private void OnTextChangedWhileCompleting(object? sender, EventArgs e)
    {
        if (_completionWindow == null) return;

        if (SqlEditor.CaretOffset <= _completionWindow.StartOffset)
            _completionWindow.Close();
    }
}
