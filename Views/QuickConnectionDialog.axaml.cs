using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Data.SqlClient;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class QuickConnectionDialog : Window
{
    public SavedConnection? Result { get; private set; }
    public string? Password { get; private set; }

    public QuickConnectionDialog()
    {
        InitializeComponent();

        // Enter key triggers connect, Escape closes
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && ConnectButton.IsEnabled)
            {
                ConnectButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }
            else if (e.Key == Key.Escape)
            {
                Close();
            }
        };

        WindowsAuthCheck.IsCheckedChanged += (s, e) =>
        {
            CredentialsPanel.IsVisible = WindowsAuthCheck.IsChecked != true;
        };

        CancelButton.Click += (s, e) => Close();

        TestButton.Click += async (s, e) =>
        {
            ErrorText.IsVisible = false;
            var connStr = BuildConnectionString();
            if (connStr == null) return;

            TestButton.IsEnabled = false;
            TestButton.Content = "Testing...";

            var success = await TestConnectionAsync(connStr);

            TestButton.IsEnabled = true;
            TestButton.Content = "Test";

            if (success)
            {
                ErrorText.Text = "Connection successful!";
                ErrorText.Foreground = Avalonia.Media.Brushes.Green;
                ErrorText.IsVisible = true;
            }
            else
            {
                ErrorText.Text = "Connection failed. Check your settings.";
                ErrorText.Foreground = Avalonia.Media.Brushes.OrangeRed;
                ErrorText.IsVisible = true;
            }
        };

        ConnectButton.Click += async (s, e) =>
        {
            ErrorText.IsVisible = false;
            var connStr = BuildConnectionString();
            if (connStr == null) return;

            ConnectButton.IsEnabled = false;
            ConnectButton.Content = "Connecting...";

            var success = await TestConnectionAsync(connStr);

            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Connect";

            if (success)
            {
                Result = new SavedConnection
                {
                    Server = ServerBox.Text?.Trim() ?? "",
                    Database = DatabaseBox.Text?.Trim() ?? "",
                    Username = UsernameBox.Text?.Trim() ?? "",
                    UseWindowsAuth = WindowsAuthCheck.IsChecked == true
                };
                Password = PasswordBox.Text;
                Close();
            }
            else
            {
                ErrorText.Text = "Connection failed. Check your settings.";
                ErrorText.Foreground = Avalonia.Media.Brushes.OrangeRed;
                ErrorText.IsVisible = true;
            }
        };
    }

    private string? BuildConnectionString()
    {
        var server = ServerBox.Text?.Trim();
        var database = DatabaseBox.Text?.Trim();

        if (string.IsNullOrEmpty(server))
        {
            ErrorText.Text = "Please enter a server address.";
            ErrorText.IsVisible = true;
            return null;
        }

        if (string.IsNullOrEmpty(database))
        {
            ErrorText.Text = "Please enter a database name.";
            ErrorText.IsVisible = true;
            return null;
        }

        if (WindowsAuthCheck.IsChecked == true)
        {
            return $"Server={server};Database={database};Integrated Security=True;TrustServerCertificate=True;";
        }
        else
        {
            var username = UsernameBox.Text?.Trim();
            var password = PasswordBox.Text;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                ErrorText.Text = "Please enter username and password.";
                ErrorText.IsVisible = true;
                return null;
            }

            return $"Server={server};Database={database};User Id={username};Password={password};TrustServerCertificate=True;";
        }
    }

    private async Task<bool> TestConnectionAsync(string connectionString)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
