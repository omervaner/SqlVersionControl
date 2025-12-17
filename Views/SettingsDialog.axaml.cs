using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsService _settings = null!;
    public bool SettingsChanged { get; private set; }

    public SettingsDialog()
    {
        InitializeComponent();
    }

    public SettingsDialog(SettingsService settings) : this()
    {
        _settings = settings;

        LoadCurrentSettings();

        // Enter saves, Escape cancels
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
                SaveAndClose();
            else if (e.Key == Key.Escape)
                Close();
        };

        CancelButton.Click += (s, e) => Close();
        SaveButton.Click += (s, e) => SaveAndClose();
        BrowseFolderButton.Click += async (s, e) => await BrowseForFolderAsync();
    }

    private void LoadCurrentSettings()
    {
        var s = _settings.Settings;

        // Theme
        if (s.UseDarkTheme)
            DarkThemeRadio.IsChecked = true;
        else
            LightThemeRadio.IsChecked = true;

        // Font size
        foreach (var obj in FontSizeCombo.Items)
        {
            if (obj is ComboBoxItem item && item.Tag?.ToString() == s.FontSize.ToString())
            {
                FontSizeCombo.SelectedItem = item;
                break;
            }
        }
        if (FontSizeCombo.SelectedItem == null)
            FontSizeCombo.SelectedIndex = 2; // Default to 12

        // Max connections
        MaxConnectionsUpDown.Value = s.MaxRecentConnections;

        // Data folder
        var folder = s.DataFolderPath ?? SettingsService.DefaultDataFolder;
        DataFolderTextBox.Text = folder;
        FolderHintText.Text = $"Default: {SettingsService.DefaultDataFolder}";
    }

    private void SaveAndClose()
    {
        var s = _settings.Settings;

        var oldTheme = s.UseDarkTheme;
        var oldFontSize = s.FontSize;

        // Theme
        s.UseDarkTheme = DarkThemeRadio.IsChecked == true;

        // Font size
        if (FontSizeCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            if (int.TryParse(item.Tag.ToString(), out var fontSize))
                s.FontSize = fontSize;
        }

        // Max connections
        s.MaxRecentConnections = (int)(MaxConnectionsUpDown.Value ?? 5);

        // Data folder (null means use default)
        var folder = DataFolderTextBox.Text;
        s.DataFolderPath = folder == SettingsService.DefaultDataFolder ? null : folder;

        _settings.Save();

        SettingsChanged = oldTheme != s.UseDarkTheme || oldFontSize != s.FontSize;
        Close();
    }

    private async Task BrowseForFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Data Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            DataFolderTextBox.Text = folders[0].Path.LocalPath;
        }
    }
}
