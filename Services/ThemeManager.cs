using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace SqlVersionControl.Services;

public static class ThemeManager
{
    public static bool IsDarkTheme { get; private set; } = true;
    public static int FontSize { get; private set; } = 12;

    /// <summary>Fired after ApplyTheme completes. Views should subscribe to re-apply code-behind colors.</summary>
    public static event Action? ThemeChanged;

    private static readonly Uri DarkThemeUri = new("avares://SqlVersionControl/Styles/AppTheme.axaml");
    private static readonly Uri LightThemeUri = new("avares://SqlVersionControl/Styles/AppThemeLight.axaml");
    private static IResourceProvider? _currentThemeDict;

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

    // Light theme colors — warm cream palette, NOT inverted dark
    public static class Light
    {
        public static readonly Color DiffBackground = Color.FromRgb(245, 240, 232);     // EditorBackground #f5f0e8
        public static readonly Color LineNumberBackground = Color.FromRgb(232, 226, 216); // PanelHeaderBackground #e8e2d8
        public static readonly Color LineNumberForeground = Color.FromRgb(138, 132, 122); // TextSecondary #8a847a
        public static readonly Color DefaultForeground = Color.FromRgb(58, 54, 50);      // TextPrimary #3a3632

        // Diff line backgrounds
        public static readonly Color DeletedBackground = Color.FromRgb(245, 213, 213);   // #f5d5d5
        public static readonly Color InsertedBackground = Color.FromRgb(213, 240, 213);  // #d5f0d5
        public static readonly Color ModifiedBackground = Color.FromRgb(245, 240, 208);  // #f5f0d0
        public static readonly Color ImaginaryBackground = Color.FromRgb(239, 233, 222); // #efe9de

        // Diff text foregrounds
        public static readonly Color DeletedForeground = Color.FromRgb(168, 82, 80);     // #a85250
        public static readonly Color InsertedForeground = Color.FromRgb(90, 112, 40);    // #5a7028
        public static readonly Color ImaginaryForeground = Color.FromRgb(138, 132, 122); // TextSecondary #8a847a

        // Syntax highlighting — warm light palette
        public static readonly Color Keyword = Color.FromRgb(44, 95, 138);        // #2c5f8a
        public static readonly Color String = Color.FromRgb(90, 112, 40);         // #5a7028
        public static readonly Color Comment = Color.FromRgb(154, 148, 136);      // #9a9488
        public static readonly Color Number = Color.FromRgb(152, 106, 29);        // #986a1d
        public static readonly Color Variable = Color.FromRgb(123, 90, 138);      // #7b5a8a
        public static readonly Color SystemFunction = Color.FromRgb(138, 109, 26); // #8a6d1a
        public static readonly Color Identifier = Color.FromRgb(42, 122, 112);    // #2a7a70
    }

    public static void ApplyTheme(bool useDarkTheme, int fontSize = 12)
    {
        IsDarkTheme = useDarkTheme;
        FontSize = fontSize;

        if (Application.Current == null) return;

        // Update application theme variant
        Application.Current.RequestedThemeVariant = useDarkTheme
            ? ThemeVariant.Dark
            : ThemeVariant.Light;

        // Swap resource dictionaries: clear all merged dicts and add the correct theme
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Clear();

        var baseUri = new Uri("avares://SqlVersionControl/");
        var targetSource = useDarkTheme
            ? new Uri("/Styles/AppTheme.axaml", UriKind.Relative)
            : new Uri("/Styles/AppThemeLight.axaml", UriKind.Relative);
        var themeInclude = new Avalonia.Markup.Xaml.Styling.ResourceInclude(baseUri)
        {
            Source = targetSource
        };
        resources.MergedDictionaries.Add(themeInclude);

        // Notify all subscribers to re-apply code-behind colors
        ThemeChanged?.Invoke();
    }

    /// <summary>Manually fire ThemeChanged (e.g. after settings save).</summary>
    public static void NotifyThemeChanged() => ThemeChanged?.Invoke();

    public static void ApplyGridRowHeight(int height)
    {
        if (Application.Current != null)
            Application.Current.Resources["GridRowHeight"] = (double)height;
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
