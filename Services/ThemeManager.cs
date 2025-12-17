using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace SqlVersionControl.Services;

public static class ThemeManager
{
    public static bool IsDarkTheme { get; private set; } = true;
    public static int FontSize { get; private set; } = 12;

    // Dark theme colors
    public static class Dark
    {
        public static readonly Color DiffBackground = Color.FromRgb(30, 30, 30);
        public static readonly Color LineNumberBackground = Color.FromRgb(45, 45, 45);
        public static readonly Color LineNumberForeground = Color.FromRgb(133, 133, 133);
        public static readonly Color DefaultForeground = Color.FromRgb(212, 212, 212);

        // Diff line backgrounds
        public static readonly Color DeletedBackground = Color.FromRgb(80, 30, 30);
        public static readonly Color InsertedBackground = Color.FromRgb(30, 60, 30);
        public static readonly Color ModifiedBackground = Color.FromRgb(60, 60, 30);
        public static readonly Color ImaginaryBackground = Color.FromRgb(40, 40, 40);

        // Syntax highlighting
        public static readonly Color Keyword = Color.FromRgb(86, 156, 214);
        public static readonly Color String = Color.FromRgb(206, 145, 120);
        public static readonly Color Comment = Color.FromRgb(106, 153, 85);
        public static readonly Color Number = Color.FromRgb(181, 206, 168);
        public static readonly Color Variable = Color.FromRgb(156, 220, 254);
        public static readonly Color SystemFunction = Color.FromRgb(220, 220, 170);
        public static readonly Color Identifier = Color.FromRgb(78, 201, 176);
    }

    // Light theme colors
    public static class Light
    {
        public static readonly Color DiffBackground = Color.FromRgb(255, 255, 255);
        public static readonly Color LineNumberBackground = Color.FromRgb(240, 240, 240);
        public static readonly Color LineNumberForeground = Color.FromRgb(120, 120, 120);
        public static readonly Color DefaultForeground = Color.FromRgb(30, 30, 30);

        // Diff line backgrounds
        public static readonly Color DeletedBackground = Color.FromRgb(255, 220, 220);
        public static readonly Color InsertedBackground = Color.FromRgb(220, 255, 220);
        public static readonly Color ModifiedBackground = Color.FromRgb(255, 255, 200);
        public static readonly Color ImaginaryBackground = Color.FromRgb(245, 245, 245);

        // Syntax highlighting
        public static readonly Color Keyword = Color.FromRgb(0, 0, 255);
        public static readonly Color String = Color.FromRgb(163, 21, 21);
        public static readonly Color Comment = Color.FromRgb(0, 128, 0);
        public static readonly Color Number = Color.FromRgb(9, 134, 88);
        public static readonly Color Variable = Color.FromRgb(0, 100, 148);
        public static readonly Color SystemFunction = Color.FromRgb(116, 83, 31);
        public static readonly Color Identifier = Color.FromRgb(38, 127, 153);
    }

    public static void ApplyTheme(bool useDarkTheme, int fontSize = 12)
    {
        IsDarkTheme = useDarkTheme;
        FontSize = fontSize;

        // Update application theme variant
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = useDarkTheme
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }
    }

    // Helper methods to get current theme colors
    public static Color GetDiffBackground() => IsDarkTheme ? Dark.DiffBackground : Light.DiffBackground;
    public static Color GetLineNumberBackground() => IsDarkTheme ? Dark.LineNumberBackground : Light.LineNumberBackground;
    public static Color GetLineNumberForeground() => IsDarkTheme ? Dark.LineNumberForeground : Light.LineNumberForeground;
    public static Color GetDefaultForeground() => IsDarkTheme ? Dark.DefaultForeground : Light.DefaultForeground;

    public static Color GetDeletedBackground() => IsDarkTheme ? Dark.DeletedBackground : Light.DeletedBackground;
    public static Color GetInsertedBackground() => IsDarkTheme ? Dark.InsertedBackground : Light.InsertedBackground;
    public static Color GetModifiedBackground() => IsDarkTheme ? Dark.ModifiedBackground : Light.ModifiedBackground;
    public static Color GetImaginaryBackground() => IsDarkTheme ? Dark.ImaginaryBackground : Light.ImaginaryBackground;

    public static Color GetKeywordColor() => IsDarkTheme ? Dark.Keyword : Light.Keyword;
    public static Color GetStringColor() => IsDarkTheme ? Dark.String : Light.String;
    public static Color GetCommentColor() => IsDarkTheme ? Dark.Comment : Light.Comment;
    public static Color GetNumberColor() => IsDarkTheme ? Dark.Number : Light.Number;
    public static Color GetVariableColor() => IsDarkTheme ? Dark.Variable : Light.Variable;
    public static Color GetSystemFunctionColor() => IsDarkTheme ? Dark.SystemFunction : Light.SystemFunction;
    public static Color GetIdentifierColor() => IsDarkTheme ? Dark.Identifier : Light.Identifier;
}
