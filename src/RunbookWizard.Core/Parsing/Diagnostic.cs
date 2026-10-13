namespace RunbookWizard.Core.Parsing;

public enum Severity { Warning, Error }

/// <summary>One finding about a guide. <see cref="Path"/> locates it (<c>steps[3].success.outputMatches</c>), <see cref="Line"/> is set for Markdown structure problems.</summary>
public sealed record Diagnostic(Severity Severity, string Path, string Message, int? Line = null)
{
    public bool IsError => Severity == Severity.Error;

    public override string ToString()
    {
        var where = string.IsNullOrEmpty(Path) ? string.Empty : Path;
        if (Line is { } line)
            where = where.Length == 0 ? $"line {line}" : $"{where} (line {line})";
        return $"{Severity.ToString().ToLowerInvariant()}: {(where.Length == 0 ? string.Empty : where + ": ")}{Message}";
    }
}

/// <summary>Outcome of parsing: the typed guide (when the structure was valid enough to build one), the canonical JSON and all findings.</summary>
public sealed class ParseResult
{
    public Model.Runbook? Runbook { get; init; }
    public System.Text.Json.Nodes.JsonNode? Canonical { get; init; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();
    public string? SourcePath { get; init; }

    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    /// <summary>The guide may be shown and run only when it parsed and has no errors.</summary>
    public bool IsRunnable => Runbook is not null && !HasErrors;
}
