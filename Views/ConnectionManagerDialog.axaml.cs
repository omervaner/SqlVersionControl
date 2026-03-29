using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class ConnectionManagerDialog : Window
{
    public ConnectionManagerDialog()
    {
        InitializeComponent();
    }

    public ConnectionManagerDialog(ConnectionRegistry registry, DatabaseService db) : this()
    {
        var vm = new ConnectionManagerViewModel(registry, db);
        DataContext = vm;
        BuildColorPicker(vm);

        // Wire password prompt — registry asks for password, we show a simple dialog
        registry.PasswordRequested += async config =>
        {
            var passwordBox = new TextBox { PasswordChar = '●', Watermark = "Enter password..." };
            var dialog = new Window
            {
                Title = $"Password for {config.Name ?? config.Server}",
                Width = 350, Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = $"Enter password for {config.Username}@{config.Server}" },
                        passwordBox,
                        new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                    }
                }
            };

            string? result = null;
            var okButton = ((StackPanel)dialog.Content).Children[2] as Button;
            okButton!.Click += (_, _) =>
            {
                result = passwordBox.Text;
                dialog.Close();
            };

            await dialog.ShowDialog(this);
            return result;
        };
    }

    private void BuildColorPicker(ConnectionManagerViewModel vm)
    {
        foreach (var (hex, label) in ConnectionManagerViewModel.PresetColors)
        {
            var color = Color.Parse(hex);
            var circle = new Ellipse
            {
                Width = 24, Height = 24,
                Fill = new SolidColorBrush(color),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Stroke = Brushes.Transparent,
                StrokeThickness = 2
            };
            ToolTip.SetTip(circle, label);

            var hexCapture = hex;
            circle.PointerPressed += (_, _) =>
            {
                vm.EditColor = hexCapture;
                UpdateColorSelection(vm.EditColor);
            };

            ColorPicker.Children.Add(circle);
        }

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionManagerViewModel.EditColor))
                UpdateColorSelection(vm.EditColor);
        };
        UpdateColorSelection(vm.EditColor);
    }

    private void UpdateColorSelection(string selectedHex)
    {
        for (int i = 0; i < ColorPicker.Children.Count; i++)
        {
            if (ColorPicker.Children[i] is not Ellipse circle) continue;
            var hex = ConnectionManagerViewModel.PresetColors[i].Hex;
            circle.Stroke = hex == selectedHex
                ? Brushes.White
                : Brushes.Transparent;
        }
    }
}
