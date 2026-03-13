using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace SqlVersionControl.Services;

public static class ThemeManager
{
    public static bool IsDarkTheme { get; private set; } = true;
    public static int FontSize { get; private set; } = 12;

    // Dark theme colors — must match Styles/AppTheme.axaml (Ghostty Default Dark)
    public static class Dark
    {
        public static readonly Color DiffBackground = Color.FromRgb(41, 44, 51);       // EditorBackground #292c33
        public static readonly Color LineNumberBackground = Color.FromRgb(29, 31, 33);  // PanelHeaderBackground #1d1f21
        public static readonly Color LineNumberForeground = Color.FromRgb(102, 102, 102); // TextSecondary #666666
        public static readonly Color DefaultForeground = Color.FromRgb(197, 200, 198);  // TextPrimary #c5c8c6

        // Diff line backgrounds
        public static readonly Color DeletedBackground = Color.FromRgb(61, 32, 32);     // DiffDeletedBackground #3d2020
        public static readonly Color InsertedBackground = Color.FromRgb(32, 61, 32);    // DiffInsertedBackground #203d20
        public static readonly Color ModifiedBackground = Color.FromRgb(61, 61, 32);    // DiffModifiedBackground #3d3d20
        public static readonly Color ImaginaryBackground = Color.FromRgb(37, 39, 41);   // DiffImaginaryBackground #252729

        // Diff text foregrounds
        public static readonly Color DeletedForeground = Color.FromRgb(191, 107, 105);  // ButtonDanger #bf6b69 lightened
        public static readonly Color InsertedForeground = Color.FromRgb(183, 189, 115); // ButtonPrimary #b7bd73
        public static readonly Color ImaginaryForeground = Color.FromRgb(102, 102, 102); // TextSecondary #666666

        // Syntax highlighting — Ghostty Default Dark palette
        public static readonly Color Keyword = Color.FromRgb(136, 161, 187);       // #88a1bb
        public static readonly Color String = Color.FromRgb(183, 189, 115);        // #b7bd73
        public static readonly Color Comment = Color.FromRgb(102, 102, 102);       // #666666
        public static readonly Color Number = Color.FromRgb(233, 200, 128);        // #e9c880
        public static readonly Color Variable = Color.FromRgb(173, 149, 184);      // #ad95b8
        public static readonly Color SystemFunction = Color.FromRgb(225, 198, 94); // #e1c65e
        public static readonly Color Identifier = Color.FromRgb(149, 189, 183);    // #95bdb7
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

        // Diff text foregrounds
        public static readonly Color DeletedForeground = Color.FromRgb(180, 50, 50);
        public static readonly Color InsertedForeground = Color.FromRgb(50, 150, 50);
        public static readonly Color ImaginaryForeground = Color.FromRgb(100, 100, 100);

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

    public static Color GetDeletedForeground() => IsDarkTheme ? Dark.DeletedForeground : Light.DeletedForeground;
    public static Color GetInsertedForeground() => IsDarkTheme ? Dark.InsertedForeground : Light.InsertedForeground;
    public static Color GetImaginaryForeground() => IsDarkTheme ? Dark.ImaginaryForeground : Light.ImaginaryForeground;

    public static Color GetKeywordColor() => IsDarkTheme ? Dark.Keyword : Light.Keyword;
    public static Color GetStringColor() => IsDarkTheme ? Dark.String : Light.String;
    public static Color GetCommentColor() => IsDarkTheme ? Dark.Comment : Light.Comment;
    public static Color GetNumberColor() => IsDarkTheme ? Dark.Number : Light.Number;
    public static Color GetVariableColor() => IsDarkTheme ? Dark.Variable : Light.Variable;
    public static Color GetSystemFunctionColor() => IsDarkTheme ? Dark.SystemFunction : Light.SystemFunction;
    public static Color GetIdentifierColor() => IsDarkTheme ? Dark.Identifier : Light.Identifier;
}
