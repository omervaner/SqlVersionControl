using Avalonia.Controls;
using Avalonia.Input;
using SqlVersionControl.Models;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionSettings? Result { get; private set; }

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
        DataContext = new ConnectionViewModel(db, settings ?? new SettingsService(), connSettings =>
        {
            Result = connSettings;
            Close();
        });
    }
}
