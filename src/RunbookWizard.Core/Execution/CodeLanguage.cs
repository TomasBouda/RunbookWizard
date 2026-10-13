using RunbookWizard.Core.Model;

namespace RunbookWizard.Core.Execution;

/// <summary>
/// Maps the language tags people write (a Markdown fence info string such as <c>pwsh</c> or <c>sh</c>, or a step's shell) to one
/// canonical name, so the UI picks one syntax grammar per language. Pure text logic, no UI dependency.
/// </summary>
public static class CodeLanguage
{
    public const string PowerShell = "powershell";
    public const string Bash = "bash";
    public const string Batch = "batch";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["powershell"] = PowerShell,
        ["pwsh"] = PowerShell,
        ["winps"] = PowerShell,
        ["ps1"] = PowerShell,
        ["ps"] = PowerShell,
        ["bash"] = Bash,
        ["sh"] = Bash,
        ["shell"] = Bash,
        ["zsh"] = Bash,
        ["cmd"] = Batch,
        ["bat"] = Batch,
        ["batch"] = Batch,
    };

    /// <summary>
    /// The canonical name of a language tag: <c>powershell</c>, <c>bash</c> or <c>batch</c> for the shells, otherwise the tag
    /// itself in lower case (<c>json</c>, <c>xml</c>, ...). A fence info string may carry attributes after the language
    /// (<c>powershell {name=x}</c>); only the first word counts. Null for an empty tag.
    /// </summary>
    public static string? Canonical(string? tag)
    {
        var first = tag?.Trim().Split(new[] { ' ', '\t', '{' }, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(first))
            return null;
        return Aliases.TryGetValue(first, out var canonical) ? canonical : first.ToLowerInvariant();
    }

    /// <summary>The language a step's command is written in, from its shell.</summary>
    public static string ForShell(ShellKind shell) => shell switch
    {
        ShellKind.Bash or ShellKind.Sh => Bash,
        ShellKind.Cmd => Batch,
        _ => PowerShell,
    };
}
