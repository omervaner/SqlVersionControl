using Avalonia.Controls;
using Avalonia.Input;

namespace SqlVersionControl.Views;

public partial class AlterSequenceDialog : Window
{
    public long? NewValue { get; private set; }

    public AlterSequenceDialog()
    {
        InitializeComponent();
    }

    public AlterSequenceDialog(string sequenceName, long currentValue) : this()
    {
        SequenceLabel.Text = sequenceName;
        CurrentValueLabel.Text = currentValue.ToString("N0");
        ValueBox.Text = currentValue.ToString();

        OkButton.Click += (_, _) =>
        {
            if (long.TryParse(ValueBox.Text?.Trim(), out var val))
            {
                NewValue = val;
                Close();
            }
        };

        CancelButton.Click += (_, _) =>
        {
            NewValue = null;
            Close();
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { NewValue = null; Close(); }
            if (e.Key == Key.Enter) OkButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        };

        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }
}
