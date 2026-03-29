using Avalonia.Controls;
using Avalonia.Input;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class SqlQuoterDialog : Window
{
    public SqlQuoterDialog()
    {
        InitializeComponent();

        // Auto-update output when input or format changes
        InputBox.TextChanged += (_, _) => UpdateOutput();
        FormatString.IsCheckedChanged += (_, _) => UpdateOutput();
        FormatNumeric.IsCheckedChanged += (_, _) => UpdateOutput();
        FormatParenthesized.IsCheckedChanged += (_, _) => UpdateOutput();
        FormatNString.IsCheckedChanged += (_, _) => UpdateOutput();

        CopyButton.Click += async (_, _) =>
        {
            var text = OutputBox.Text;
            if (!string.IsNullOrEmpty(text) && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        };

        CloseButton.Click += (_, _) => Close();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        Opened += (_, _) => InputBox.Focus();
    }

    private SqlQuoterService.QuoteFormat GetSelectedFormat()
    {
        if (FormatNumeric.IsChecked == true) return SqlQuoterService.QuoteFormat.Numeric;
        if (FormatParenthesized.IsChecked == true) return SqlQuoterService.QuoteFormat.Parenthesized;
        if (FormatNString.IsChecked == true) return SqlQuoterService.QuoteFormat.NString;
        return SqlQuoterService.QuoteFormat.String;
    }

    private void UpdateOutput()
    {
        var values = SqlQuoterService.ParseValues(InputBox.Text ?? "");
        OutputBox.Text = SqlQuoterService.FormatValues(values, GetSelectedFormat());
    }
}
