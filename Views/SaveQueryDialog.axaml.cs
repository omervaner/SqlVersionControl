using Avalonia.Controls;
using Avalonia.Input;

namespace SqlVersionControl.Views;

public partial class SaveQueryDialog : Window
{
    public string? QueryName { get; private set; }

    public SaveQueryDialog()
    {
        InitializeComponent();
    }

    public SaveQueryDialog(string? defaultName, string database) : this()
    {
        DatabaseLabel.Text = database ?? "(none)";

        if (!string.IsNullOrWhiteSpace(defaultName))
            NameBox.Text = defaultName;

        SaveButton.Click += (_, _) =>
        {
            var name = NameBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                QueryName = name;
                Close();
            }
        };

        CancelButton.Click += (_, _) =>
        {
            QueryName = null;
            Close();
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { QueryName = null; Close(); }
            if (e.Key == Key.Enter) SaveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        };

        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }
}
