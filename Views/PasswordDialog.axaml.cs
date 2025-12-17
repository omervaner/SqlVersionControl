using Avalonia.Controls;
using Avalonia.Input;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class PasswordDialog : Window
{
    public string? Password { get; private set; }

    public PasswordDialog()
    {
        InitializeComponent();

        // Enter key triggers OK, Escape cancels
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Password = PasswordBox.Text;
                Close();
            }
            else if (e.Key == Key.Escape)
            {
                Password = null;
                Close();
            }
        };

        CancelButton.Click += (s, e) =>
        {
            Password = null;
            Close();
        };

        OkButton.Click += (s, e) =>
        {
            Password = PasswordBox.Text;
            Close();
        };
    }

    public PasswordDialog(SavedConnection conn) : this()
    {
        MessageText.Text = $"Enter password for {conn.Username}@{conn.Server}/{conn.Database}";
    }
}
