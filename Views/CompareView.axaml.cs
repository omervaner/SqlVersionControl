using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class CompareView : UserControl
{
    private SettingsService? _settings;

    public CompareView()
    {
        InitializeComponent();
    }

    public void Initialize(SettingsService settings)
    {
        _settings = settings;
        DataContext = new CompareViewModel(settings);

        AddSourceButton.Click += async (s, e) => await ShowAddConnectionDialogAsync(true);
        AddTargetButton.Click += async (s, e) => await ShowAddConnectionDialogAsync(false);

        // Wire up password prompt
        ViewModel.PasswordRequested += OnPasswordRequested;

        // Update selection count when clicking in the list area
        AddHandler(CheckBox.IsCheckedChangedEvent, OnCheckboxChanged, RoutingStrategies.Bubble);
    }

    private void OnCheckboxChanged(object? sender, RoutedEventArgs e)
    {
        ViewModel.UpdateSelectedCount();
    }

    public CompareViewModel ViewModel => (CompareViewModel)DataContext!;

    private async Task<string?> OnPasswordRequested(SavedConnection conn)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return null;

        var dialog = new PasswordDialog(conn);
        await dialog.ShowDialog(window);
        return dialog.Password;
    }

    private async Task ShowAddConnectionDialogAsync(bool isSource)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        var dialog = new QuickConnectionDialog();
        await dialog.ShowDialog(window);

        if (dialog.Result != null)
        {
            ViewModel.AddConnection(dialog.Result, dialog.Password, isSource);
        }
    }

    public void RefreshTheme()
    {
        CompareDiffView.ApplyTheme();
    }
}
