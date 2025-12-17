using Avalonia.Media;
using SqlVersionControl.Services;

namespace SqlVersionControl.Models;

public enum SqlTokenType
{
    Default,
    Keyword,
    String,
    Comment,
    Number,
    Variable,
    SystemFunction,
    Identifier
}

public class HighlightedSegment
{
    public string Text { get; set; } = "";
    public SqlTokenType TokenType { get; set; }
    public IBrush? OverrideForeground { get; set; }

    // Colors based on theme
    public IBrush Foreground => OverrideForeground ?? TokenType switch
    {
        SqlTokenType.Keyword => new SolidColorBrush(ThemeManager.GetKeywordColor()),
        SqlTokenType.String => new SolidColorBrush(ThemeManager.GetStringColor()),
        SqlTokenType.Comment => new SolidColorBrush(ThemeManager.GetCommentColor()),
        SqlTokenType.Number => new SolidColorBrush(ThemeManager.GetNumberColor()),
        SqlTokenType.Variable => new SolidColorBrush(ThemeManager.GetVariableColor()),
        SqlTokenType.SystemFunction => new SolidColorBrush(ThemeManager.GetSystemFunctionColor()),
        SqlTokenType.Identifier => new SolidColorBrush(ThemeManager.GetIdentifierColor()),
        _ => new SolidColorBrush(ThemeManager.GetDefaultForeground())
    };

    public int FontSize => ThemeManager.FontSize;
}
