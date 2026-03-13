using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        VersionLabel.Text = $"v{AppVersion.Version}";

        GitHubButton.Click += (_, _) =>
        {
            var url = "https://github.com/omervaner/SqlVersionControl/releases";
            OpenUrl(url);
        };

        CloseButton.Click += (_, _) => Close();
    }

    private static void OpenUrl(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", url);
        else
            Process.Start("xdg-open", url);
    }
}
