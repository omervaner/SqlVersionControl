using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using SqlVersionControl.Models;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionSettings? Result { get; private set; }
    public SavedConnection? ResultConnection { get; private set; }

    public ConnectionDialog()
    {
        InitializeComponent();

        // Enter key triggers connect
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && DataContext is ConnectionViewModel vm && vm.ConnectCommand.CanExecute(null))
            {
                vm.ConnectCommand.Execute(null);
            }
        };
    }

    public ConnectionDialog(DatabaseService db, SettingsService? settings = null) : this()
    {
        var vm = new ConnectionViewModel(db, settings ?? new SettingsService(), connSettings =>
        {
            Result = connSettings;
            ResultConnection = (DataContext as ConnectionViewModel)?.ResultConnection;
            Close();
        });
        DataContext = vm;
        BuildColorPicker(vm);
    }

    private void BuildColorPicker(ConnectionViewModel vm)
    {
        foreach (var (hex, label) in ConnectionViewModel.PresetColors)
        {
            var color = Color.Parse(hex);
            var circle = new Ellipse
            {
                Width = 24, Height = 24,
                Fill = new SolidColorBrush(color),
                Cursor = new Cursor(StandardCursorType.Hand),
                Stroke = Brushes.Transparent,
                StrokeThickness = 2
            };
            ToolTip.SetTip(circle, label);

            var hexCapture = hex;
            circle.PointerPressed += (_, _) =>
            {
                vm.ConnectionColor = hexCapture;
                UpdateColorSelection(vm.ConnectionColor);
            };

            ColorPicker.Children.Add(circle);
        }

        // Show initial selection
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.ConnectionColor))
                UpdateColorSelection(vm.ConnectionColor);
        };
        UpdateColorSelection(vm.ConnectionColor);
    }

    private void UpdateColorSelection(string selectedHex)
    {
        for (int i = 0; i < ColorPicker.Children.Count; i++)
        {
            if (ColorPicker.Children[i] is not Ellipse circle) continue;
            var hex = ConnectionViewModel.PresetColors[i].Hex;
            circle.Stroke = hex == selectedHex
                ? Brushes.White
                : Brushes.Transparent;
        }
    }
}
