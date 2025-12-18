using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class CompareView : UserControl
{
    private SettingsService? _settings;
    private bool _hasAutoConnected;

    public CompareView()
    {
        InitializeComponent();
    }

    public void Initialize(SettingsService settings)
    {
        _settings = settings;
        DataContext = new CompareViewModel(settings);

        AddSourceButton.Click += async (s, e) => await ShowAddConnectionDialogAsync(true, false);
        AddTargetButton.Click += async (s, e) => await ShowAddConnectionDialogAsync(false, false);
        AddTarget2Button.Click += async (s, e) => await ShowAddConnectionDialogAsync(false, true);

        // Wire up password prompt
        ViewModel.PasswordRequested += OnPasswordRequested;

        // Update selection count when clicking in the list area
        AddHandler(CheckBox.IsCheckedChangedEvent, OnCheckboxChanged, RoutingStrategies.Bubble);

        // Defer auto-connect until control is in visual tree (so password dialogs work)
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Only auto-connect once (not every time tab is switched)
        if (_hasAutoConnected) return;
        _hasAutoConnected = true;

        // Now TopLevel.GetTopLevel(this) will work for password dialogs
        _ = ViewModel.TryAutoConnectSourceAsync();
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

    private async Task ShowAddConnectionDialogAsync(bool isSource, bool isTarget2)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        var dialog = new QuickConnectionDialog();
        await dialog.ShowDialog(window);

        if (dialog.Result != null)
        {
            if (isTarget2)
            {
                ViewModel.AddConnectionToTarget2(dialog.Result, dialog.Password);
            }
            else
            {
                ViewModel.AddConnection(dialog.Result, dialog.Password, isSource);
            }
        }
    }

    public void RefreshTheme()
    {
        // Refresh theme on all diff views
        CompareDiffView?.ApplyTheme();
        CompareDiffView1?.ApplyTheme();
        CompareDiffView2?.ApplyTheme();
    }
}
