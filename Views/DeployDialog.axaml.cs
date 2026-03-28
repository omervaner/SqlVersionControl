using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SqlVersionControl.Views;

public partial class DeployDialog : Window
{
    public bool Confirmed { get; private set; }

    private static IBrush FindBrush(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out var r) == true && r is IBrush b
            ? b : Brushes.Transparent;

    public DeployDialog()
    {
        InitializeComponent();
    }

    public DeployDialog(string objectName, string targetDescription, bool isProd) : this()
    {
        ObjectText.Text = $"Object: {objectName}";
        TargetText.Text = $"Target: {targetDescription}";

        if (isProd)
        {
            // Make it scary for PROD
            TitleText.Foreground = FindBrush("ButtonDanger");
            TitleText.Text = "PRODUCTION Deployment";
            WarningText.Text = "You are about to deploy to PRODUCTION!";
            ProdWarning.Text = "This will modify the PRODUCTION environment. Please ensure you have tested this change in lower environments first.";
            ProdWarning.IsVisible = true;
            ConfirmButton.Content = "Deploy to PROD";
            ConfirmButton.Background = FindBrush("ButtonDanger");
        }
        else
        {
            WarningText.Text = $"Deploying to {targetDescription}";
        }

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
