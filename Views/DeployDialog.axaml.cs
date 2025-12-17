using Avalonia.Controls;
using Avalonia.Media;

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
            HeaderBorder.Background = new SolidColorBrush(Color.Parse("#e63946"));
            TitleText.Text = "PRODUCTION Deployment";
            WarningText.Text = "You are about to deploy to PRODUCTION!";
            ProdWarning.Text = "This will modify the PRODUCTION environment. Please ensure you have tested this change in lower environments first.";
            ProdWarning.IsVisible = true;
            ConfirmButton.Content = "Deploy to PROD";
            ConfirmButton.Background = new SolidColorBrush(Color.Parse("#e63946"));
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
