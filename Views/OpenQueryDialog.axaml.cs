using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class OpenQueryDialog : Window
{
    public string? SelectedPath { get; private set; }

    private readonly QueryFileService _svc;
    private List<QueryFileInfo> _allQueries;

    public OpenQueryDialog()
    {
        InitializeComponent();
        _svc = new QueryFileService();
        _allQueries = [];
    }

    public OpenQueryDialog(QueryFileService svc) : this()
    {
        _svc = svc;
        _allQueries = _svc.ListSavedQueries();
        QueryGrid.ItemsSource = _allQueries;

        SearchBox.TextChanged += (_, _) => FilterList();

        QueryGrid.DoubleTapped += (_, _) => TryOpen();
        OpenButton.Click += (_, _) => TryOpen();

        DeleteButton.Click += (_, _) =>
        {
            if (QueryGrid.SelectedItem is QueryFileInfo info)
            {
                _svc.DeleteQuery(info.FilePath);
                _allQueries.Remove(info);
                FilterList();
            }
        };

        CancelButton.Click += (_, _) =>
        {
            SelectedPath = null;
            Close();
        };

        BrowseButton.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open SQL File",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("SQL Files") { Patterns = ["*.sql"] },
                    new FilePickerFileType("All Files") { Patterns = ["*"] }
                ]
            });

            if (files.Count > 0)
            {
                SelectedPath = files[0].TryGetLocalPath();
                Close();
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { SelectedPath = null; Close(); }
            if (e.Key == Key.Enter) TryOpen();
        };
    }

    private void FilterList()
    {
        var filter = SearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(filter))
        {
            QueryGrid.ItemsSource = _allQueries;
        }
        else
        {
            QueryGrid.ItemsSource = _allQueries
                .Where(q => q.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            q.Database.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void TryOpen()
    {
        if (QueryGrid.SelectedItem is QueryFileInfo info)
        {
            SelectedPath = info.FilePath;
            Close();
        }
    }
}
