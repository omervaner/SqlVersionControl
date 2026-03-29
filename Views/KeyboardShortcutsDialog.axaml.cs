using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace SqlVersionControl.Views;

public partial class KeyboardShortcutsDialog : Window
{
    public KeyboardShortcutsDialog()
    {
        InitializeComponent();

        var isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var mod = isMac ? "\u2318" : "Ctrl"; // ⌘ on Mac
        var shift = isMac ? "\u21E7" : "Shift"; // ⇧ on Mac
        var alt = isMac ? "\u2325" : "Alt"; // ⌥ on Mac
        var opt = isMac ? "\u2325" : "Alt";

        var sections = new (string Category, (string Shortcut, string Action)[] Items)[]
        {
            ("Navigation", [
                ($"{mod}+1", "Switch to Query Editor tab"),
                ($"{mod}+2", "Switch to Version History tab"),
                ($"{mod}+3", "Switch to Compare Databases tab"),
                ($"{mod}+4", "Switch to Execution Plan tab"),
                ($"{mod}+G", "Go to line number"),
                ("Escape", "Back / clear search / deselect"),
            ]),
            ("Query Tabs", [
                ($"{mod}+N", "New query tab"),
                ($"{mod}+W", "Close active query tab"),
                ("F5", "Run query"),
                ($"{mod}+Enter", "Run query"),
            ]),
            ("File", [
                ($"{mod}+O", "Open saved query"),
                ($"{mod}+S", "Save query"),
                ($"{mod}+{shift}+S", "Save As query"),
            ]),
            ("Search", [
                ($"{mod}+F", "Find in editor"),
                ($"{mod}+H", "Replace in editor"),
                ($"{mod}+R", "Refresh"),
                ($"{mod}+D", "Dependencies for selected object"),
            ]),
            ("Editor", [
                ($"{mod}+K", "Comment selected lines"),
                ($"{mod}+L", "Uncomment selected lines"),
                ($"{mod}+{shift}+U", "Uppercase selection"),
                ($"{mod}+{shift}+L", "Lowercase selection"),
                ($"{alt}+\u2191", "Move line up"),
                ($"{alt}+\u2193", "Move line down"),
                ($"{opt}+Z", "Toggle word wrap"),
                (isMac ? $"{mod}+{shift}+Z" : $"{mod}+Y", "Redo"),
            ]),
            ("Tools", [
                ($"{mod}+{shift}+F", "Format SQL"),
                ($"{mod}+{shift}+Q", "Quick quote selection"),
                ($"{(isMac ? "\u2318" : "Ctrl")}+Click", "Peek definition"),
            ]),
            ("Results Grid", [
                ($"{mod}+{shift}+C", "Copy with column headers"),
            ]),
        };

        foreach (var (category, items) in sections)
        {
            ShortcutList.Children.Add(new TextBlock
            {
                Text = category,
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                Foreground = GetBrush("AccentBlue"),
                Margin = new Thickness(0, 4, 0, 4)
            });

            foreach (var (shortcut, action) in items)
            {
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("160,*"),
                    Margin = new Thickness(8, 2, 0, 2)
                };

                var keyLabel = new TextBlock
                {
                    Text = shortcut,
                    FontFamily = new FontFamily("Consolas, Menlo, Monaco, monospace"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = GetBrush("TextBright")
                };

                var descLabel = new TextBlock
                {
                    Text = action,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = GetBrush("TextPrimary")
                };

                Grid.SetColumn(keyLabel, 0);
                Grid.SetColumn(descLabel, 1);
                row.Children.Add(keyLabel);
                row.Children.Add(descLabel);
                ShortcutList.Children.Add(row);
            }
        }

        CloseButton.Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private static IBrush GetBrush(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var res) == true && res is IBrush b)
            return b;
        return Brushes.White;
    }
}
