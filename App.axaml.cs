using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using SqlVersionControl.Services;
using System.Linq;
using Avalonia.Markup.Xaml;
using SqlVersionControl.Behaviors;
using SqlVersionControl.ViewModels;
using SqlVersionControl.Views;

namespace SqlVersionControl;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        DataGridAutoFitBehavior.Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Filter known upstream AvaloniaEdit race: SelectionLayer.Render() reads
        // VisualLines after invalidation. Swallow at the dispatcher level so the
        // app drops one frame instead of dying.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledExceptionFilter += (_, e) =>
        {
            if (e.Exception is AvaloniaEdit.Rendering.VisualLinesInvalidException)
            {
                AppLogger.Log($"[Warn] VisualLinesInvalidException swallowed: {e.Exception.Message}");
                e.RequestCatch = true;
            }
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}