using System.Reflection;

namespace SqlVersionControl.Services;

public static class AppVersion
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static string DisplayString => $"Lookout v{Version}";
}
