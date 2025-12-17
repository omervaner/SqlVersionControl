using Avalonia.Controls;
using SqlVersionControl.Models;

namespace SqlVersionControl.Views;

public partial class RollbackDialog : Window
{
    public bool Confirmed { get; private set; }

    public RollbackDialog()
    {
        InitializeComponent();
    }

    public RollbackDialog(ObjectVersion version) : this()
    {
        WarningText.Text = $"Rolling back {version.SchemaName}.{version.ObjectName} to v{version.VersionNumber} ({version.ChangedAtDisplay})";
        PreviewText.Text = version.Definition;

        CancelButton.Click += (s, e) =>
        {
            Confirmed = false;
            Close();
        };

        ConfirmButton.Click += (s, e) =>
        {
            Confirmed = true;
            Close();
        };
    }
}
