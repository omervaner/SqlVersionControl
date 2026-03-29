using Avalonia;
using SqlVersionControl.Services;
using Velopack;

namespace SqlVersionControl;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();    // MUST be first line

        // Global exception handlers — catch crashes before the process dies
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            CrashLogger.LogCrash(e.ExceptionObject as Exception);
            AppLogger.LogError("UnhandledException", e.ExceptionObject as Exception ?? new Exception("Unknown fatal error"));
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLogger.LogCrash(e.Exception);
            AppLogger.LogError("UnobservedTaskException", e.Exception);
            e.SetObserved(); // prevent termination
        };

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLogger.LogCrash(ex);
            AppLogger.LogError("Main.FatalCrash", ex);
            throw; // re-throw so the OS sees the crash
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
