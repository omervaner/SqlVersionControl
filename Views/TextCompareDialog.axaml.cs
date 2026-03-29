using Avalonia.Controls;
using Avalonia.Input;
using DiffPlex;
using DiffPlex.DiffBuilder;

namespace SqlVersionControl.Views;

public partial class TextCompareDialog : Window
{
    public TextCompareDialog()
    {
        InitializeComponent();

        CompareButton.Click += (_, _) => RunCompare();
        CloseButton.Click += (_, _) => Close();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        Opened += (_, _) => LeftInput.Focus();
    }

    private void RunCompare()
    {
        var left = LeftInput.Text ?? "";
        var right = RightInput.Text ?? "";

        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        var model = diffBuilder.BuildDiffModel(left, right);

        DiffOutput.DiffModel = model;
        EmptyHint.IsVisible = false;
    }
}
