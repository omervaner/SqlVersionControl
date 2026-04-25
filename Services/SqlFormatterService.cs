using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Services;

/// <summary>
/// Thin dispatcher — routes Format() calls to either the legacy Hogimn formatter or the
/// new ScriptDom-based formatter based on the UseNewEngine flag. Public signature unchanged
/// from the pre-revamp implementation. Three call sites (see FORMATTER-OVERHAUL.md).
/// </summary>
public static class SqlFormatterService
{
    /// <summary>
    /// Whether the new formatter is the shipped default for this build. Flipped to true at
    /// sequencing step 6. Gates the "fell back to legacy" surfaced message so it only shows
    /// to opt-in testers and disappears automatically after the flip.
    /// </summary>
    private const bool DefaultIsNewEngine = false;

    /// <summary>
    /// Runtime flag — set once at MainWindow startup from AppSettings.UseNewFormatter,
    /// with LOOKOUT_USE_NEW_FORMATTER=1 env var taking precedence for test-harness use.
    /// </summary>
    public static bool UseNewEngine { get; set; }

    /// <summary>
    /// Raised when the new formatter throws and the dispatcher falls back to legacy.
    /// MainWindow subscribes once at startup and routes to the active tab's message area,
    /// marshalled via Dispatcher.UIThread.Post. Gated so only opt-in testers see it
    /// (UseNewEngine true while DefaultIsNewEngine false). After step 6 the raise is
    /// dropped entirely.
    /// </summary>
    public static event Action<string>? FallbackOccurred;

    public static string Format(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;

        if (UseNewEngine)
        {
            try
            {
                return ScriptDomFormatter.Format(sql, FormatterOptions.Default);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("ScriptDomFormatter", ex);
                if (UseNewEngine && !DefaultIsNewEngine)
                    FallbackOccurred?.Invoke("New formatter hit an error — fell back to legacy. Check logs.");
                return LegacyHogimnFormatter.Format(sql);
            }
        }

        return LegacyHogimnFormatter.Format(sql);
    }
}
