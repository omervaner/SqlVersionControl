using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class ExportProgressDialog : Window
{
    private CancellationTokenSource? _cts;
    private bool _exportComplete;

    public ExportProgressDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => OnCloseClicked();
        OpenFolderButton.Click += (_, _) => OnOpenFolderClicked();
    }

    public string ExportPath { get; set; } = "";

    public async Task RunExportAsync(DatabaseService db, string connectionString, string exportPath,
        bool includeSystemDatabases)
    {
        ExportPath = exportPath;
        _cts = new CancellationTokenSource();

        var service = new GitExportService(db);
        service.ProgressChanged += (msg, current, total) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ProgressMessage.Text = msg;
                ProgressBar.Maximum = total;
                ProgressBar.Value = current;
                ProgressDetail.Text = $"({current}/{total})";
            });
        };

        try
        {
            var result = await Task.Run(() => service.ExportAsync(connectionString, exportPath,
                includeSystemDatabases, _cts.Token));
            ShowSummary(result);
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowSummary(ExportResult result)
    {
        _exportComplete = true;

        ProgressPanel.IsVisible = false;
        SummaryPanel.IsVisible = true;

        SummaryTitle.Text = result.TotalChanges > 0 ? "Export Complete" : "Export Complete — No Changes";
        SummaryServer.Text = $"Server: {result.ServerDisplay} ({result.UserDisplay})";
        SummaryDatabases.Text = $"Databases: {result.TotalDatabases} scanned, {result.DatabasesWithChanges} with changes";
        SummaryDuration.Text = $"Duration: {result.Duration.TotalSeconds:F1}s";

        SummaryAdded.Text = $"+{result.Added} added";
        SummaryModified.Text = $"~{result.Modified} modified";
        SummaryDeleted.Text = $"-{result.Deleted} deleted";

        if (result.Errors.Count > 0)
        {
            SummaryErrors.IsVisible = true;
            SummaryErrors.Text = $"Warnings: {string.Join("; ", result.Errors)}";
        }

        OpenFolderButton.IsVisible = true;
        CloseButton.Content = "Close";
    }

    private void ShowError(string message)
    {
        _exportComplete = true;

        ProgressPanel.IsVisible = false;
        SummaryPanel.IsVisible = true;

        SummaryTitle.Text = "Export Failed";
        SummaryServer.Text = message;
        SummaryDatabases.IsVisible = false;
        SummaryDuration.IsVisible = false;
        SummaryAdded.IsVisible = false;
        SummaryModified.IsVisible = false;
        SummaryDeleted.IsVisible = false;

        CloseButton.Content = "Close";
    }

    private void OnCloseClicked()
    {
        if (!_exportComplete)
            _cts?.Cancel();
        else
            Close();
    }

    private void OnOpenFolderClicked()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", ExportPath);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start("explorer", ExportPath);
            else
                Process.Start("xdg-open", ExportPath);
        }
        catch { /* ignore if folder open fails */ }
    }
}
