using CommunityToolkit.Mvvm.ComponentModel;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class QueryEditorHostViewModel : ObservableObject
{
    public ObjectExplorerViewModel ObjectExplorer { get; }

    public QueryEditorHostViewModel(DatabaseService db, ConnectionRegistry? registry = null)
    {
        ObjectExplorer = new ObjectExplorerViewModel(db);
        if (registry != null)
            ObjectExplorer.SetRegistry(registry);
    }
}
