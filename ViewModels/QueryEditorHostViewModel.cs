using CommunityToolkit.Mvvm.ComponentModel;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class QueryEditorHostViewModel : ObservableObject
{
    public ObjectExplorerViewModel ObjectExplorer { get; }

    public QueryEditorHostViewModel(DatabaseService db)
    {
        ObjectExplorer = new ObjectExplorerViewModel(db);
    }
}
