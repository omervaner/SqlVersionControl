using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using SqlVersionControl.Models;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class CompareView : UserControl
{
    private SettingsService? _settings;
    private ConnectionRegistry? _registry;
    private bool _hasAutoConnected;

    public CompareView()
    {
        InitializeComponent();
    }

    public void Initialize(SettingsService settings, ConnectionRegistry? registry = null)
    {
        _settings = settings;
        _registry = registry;
        DataContext = new CompareViewModel(settings, registry);

        AddSourceButton.Click += async (s, e) => await ShowAddConnectionDialogAsync(true, false);
        AddTargetButton.Click += async (s, e) => await ShowAddConnectionDialogAsync(false, false);
        AddTarget2Button.Click += async (s, e) => await ShowAddConnectionDialogAsync(false, true);

        // Wire up password prompt
        ViewModel.PasswordRequested += OnPasswordRequested;

        // Update selection count when clicking in the list area
        AddHandler(CheckBox.IsCheckedChangedEvent, OnCheckboxChanged, RoutingStrategies.Bubble);

        // Defer auto-connect until control is in visual tree (so password dialogs work)
        AttachedToVisualTree += OnAttachedToVisualTree;

        // Wire mode radio buttons
        ModeCode.Click += (_, _) =>
        {
            ViewModel.IsTableCompareMode = false;
            ViewModel.IsDataCompareMode = false;
        };
        ModeTables.Click += (_, _) =>
        {
            ViewModel.IsDataCompareMode = false;
            ViewModel.IsTableCompareMode = true;
        };
        ModeData.Click += (_, _) =>
        {
            ViewModel.IsTableCompareMode = true;
            ViewModel.IsDataCompareMode = true;
        };

        // Rebuild data compare grid columns when result changes
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CompareViewModel.DataCompareResult))
                RebuildDataCompareColumns();
        };
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

    public void FocusSearch()
    {
        CompareSearchBox.Focus();
        CompareSearchBox.SelectAll();
    }

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

    private void RebuildDataCompareColumns()
    {
        var grid = this.FindControl<DataGrid>("DataCompareGrid");
        if (grid == null) return;

        grid.Columns.Clear();

        var result = ViewModel.DataCompareResult;
        if (result == null) return;

        var pkSet = new HashSet<int>(result.PkColumnIndices);

        // Checkbox column for selection
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "☐",
            Width = new DataGridLength(35),
            CellTemplate = BuildCheckboxTemplate()
        });

        // Status column
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Status",
            Width = new DataGridLength(45),
            CellTemplate = BuildStatusTemplate()
        });

        // Data columns
        for (int i = 0; i < result.ColumnNames.Length; i++)
        {
            var colIndex = i;
            var colName = result.ColumnNames[i];
            var isPk = pkSet.Contains(i);

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = isPk ? $"🔑 {colName}" : colName,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                CellTemplate = BuildDataCellTemplate(colIndex, result)
            });
        }
    }

    private static IDataTemplate BuildCheckboxTemplate()
    {
        return new FuncDataTemplate<DataCompareRow>((row, _) =>
        {
            if (row == null) return null;
            var cb = new CheckBox
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                IsVisible = row.Status != DataRowStatus.Identical
            };
            cb.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(DataCompareRow.IsSelected)));
            return cb;
        });
    }

    private static IDataTemplate BuildStatusTemplate()
    {
        return new FuncDataTemplate<DataCompareRow>((row, _) =>
        {
            if (row == null) return null;
            return new TextBlock
            {
                Text = row.StatusIcon,
                Foreground = new SolidColorBrush(Color.Parse(row.StatusColor)),
                FontWeight = FontWeight.Bold,
                FontSize = 14,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
        });
    }

    private static IDataTemplate BuildDataCellTemplate(int colIndex, DataCompareResult result)
    {
        return new FuncDataTemplate<DataCompareRow>((row, _) =>
        {
            if (row == null) return null;
            var value = row.GetDisplayValue(colIndex);
            var isDiff = row.DifferentColumnIndices.Contains(colIndex);

            var tb = new TextBlock
            {
                Text = value == null ? "NULL" : value.ToString(),
                FontStyle = value == null ? FontStyle.Italic : FontStyle.Normal,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(4, 0),
                FontSize = 12
            };

            if (value == null)
            {
                if (Avalonia.Application.Current?.Resources.TryGetResource("TextNull", null, out var nullBrush) == true
                    && nullBrush is IBrush nb)
                    tb.Foreground = nb;
                else
                    tb.Foreground = new SolidColorBrush(Color.Parse("#95a5a6"));
            }

            if (!isDiff) return tb;

            // Wrap in a border with amber background for different values
            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#33e67e22")),
                Child = tb
            };
        });
    }
}
