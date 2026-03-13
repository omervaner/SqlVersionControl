using Avalonia.Controls;
using Avalonia.Input;

namespace SqlVersionControl.Views;

public partial class CloseTabDialog : Window
{
    /// <summary>null = cancelled, true = save, false = don't save</summary>
    public bool? Result { get; private set; }

    public CloseTabDialog()
    {
        InitializeComponent();
    }

    public CloseTabDialog(string tabTitle) : this()
    {
        MessageText.Text = $"Do you want to save changes to \"{tabTitle}\"?";

        DontSaveButton.Click += (_, _) => { Result = false; Close(); };
        CancelButton.Click += (_, _) => { Result = null; Close(); };
        SaveButton.Click += (_, _) => { Result = true; Close(); };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Result = null; Close(); }
        };
    }
}
