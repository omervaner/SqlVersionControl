using Avalonia.Controls;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class DeployDialog : Window
{
    public bool Confirmed { get; private set; }

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
            TitleText.Foreground = ThemeManager.GetBrush("ButtonDanger");
            TitleText.Text = "PRODUCTION Deployment";
            WarningText.Text = "You are about to deploy to PRODUCTION!";
            ProdWarning.Text = "This will modify the PRODUCTION environment. Please ensure you have tested this change in lower environments first.";
            ProdWarning.IsVisible = true;
            ConfirmButton.Content = "Deploy to PROD";
            ConfirmButton.Background = ThemeManager.GetBrush("ButtonDanger");
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
