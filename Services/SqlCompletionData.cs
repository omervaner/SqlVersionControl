using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Avalonia.Media;

namespace SqlVersionControl.Services;

public enum CompletionCategory { Keyword, Table, View, Column, Function }

public class SqlCompletionData : ICompletionData
{
    public SqlCompletionData(string text, CompletionCategory category, string? description = null)
    {
        Text = text;
        Category = category;
        Description = description;
        Priority = category switch
        {
            CompletionCategory.Column => 3,
            CompletionCategory.Table => 2,
            CompletionCategory.View => 2,
            CompletionCategory.Function => 1,
            _ => 0
        };
    }

    public string Text { get; }
    public CompletionCategory Category { get; }
    public object Content => Text;
    public object? Description { get; }
    public IImage? Image => null;
    public double Priority { get; }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, Text);
    }
}
