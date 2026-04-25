namespace SqlVersionControl.Services.Formatting;

public enum CommaStyle { Leading, Trailing }

/// <summary>
/// Configurable formatting choices for the ScriptDom-based formatter.
/// Default values and rationale: see docs/FORMATTER-INTERNALS.md.
/// </summary>
public sealed class FormatterOptions
{
    public int IndentSize { get; init; } = 4;

    public bool Uppercase { get; init; } = true;

    public int MaxLineLength { get; init; } = 120;

    public CommaStyle CommaStyle { get; init; } = CommaStyle.Trailing;

    public bool AlignAndOrAtStart { get; init; } = true;

    public bool IncludeSemicolons { get; init; } = true;

    public bool AlignClauseBodies { get; init; } = true;

    public static FormatterOptions Default { get; } = new();
}
