using Avalonia.Controls;
using Avalonia.Input;

namespace SqlVersionControl.Views;

public partial class ConfirmDialog : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string message, string okText = "OK", string cancelText = "Cancel") : this()
    {
        MessageText.Text = message;
        OkButton.Content = okText;
        CancelButton.Content = cancelText;

        OkButton.Click += (_, _) => { Confirmed = true; Close(); };
        CancelButton.Click += (_, _) => { Confirmed = false; Close(); };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Confirmed = false; Close(); }
            if (e.Key == Key.Enter) { Confirmed = true; Close(); }
        };
    }
}
