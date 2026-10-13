using System.Text.RegularExpressions;

namespace RunbookWizard.Core.Execution;

/// <summary>
/// Heuristic scan of a script for commands that delete, overwrite, reconfigure the system or run downloaded code.
/// It can only add warnings (ADR 0004): a clean scan proves nothing.
/// </summary>
public static class RiskScanner
{
    private static readonly (string Label, Regex Pattern)[] Rules =
    {
        ("deletes files recursively or forcibly", Rx(@"\bRemove-Item\b[^\n]*-(Recurse|Force)\b|\brm\s+(-\w+\s+)*-\w*[rf]\w*\b|\brmdir\s+/s\b|\brd\s+/s\b|\bdel\s+[^\n]*/[fsq]\b")),
        ("formats or repartitions a disk", Rx(@"\bFormat-Volume\b|\bformat\s+[a-z]:|\bClear-Disk\b|\bRemove-Partition\b|\bdiskpart\b|\bmkfs(\.\w+)?\b|\bdd\s+if=")),
        ("shuts down or restarts the machine", Rx(@"\b(Stop-Computer|Restart-Computer)\b|\bshutdown(\.exe)?\s+/[rs]\b|\bshutdown\s+-[rh]\b")),
        ("deletes registry data", Rx(@"\breg(\.exe)?\s+delete\b|\bRemove-ItemProperty\b")),
        ("drops or truncates database data", Rx(@"\b(DROP\s+(TABLE|DATABASE)|TRUNCATE\s+TABLE)\b")),
        ("changes security settings", Rx(@"\bSet-ExecutionPolicy\b|\bnetsh\s+advfirewall\b|\b(Add|Set)-MpPreference\b|\bNew-LocalUser\b|\bnet\s+user\b|\bchmod\s+-R\s+777\b|\bsetenforce\s+0\b")),
        ("removes services or sites", Rx(@"\bsc(\.exe)?\s+delete\b|\bRemove-(Website|WebAppPool|Service)\b")),
        ("runs downloaded or generated code", Rx(@"\b(iex|Invoke-Expression)\b|\b(curl|wget)\b[^\n|]*\|\s*(sudo\s+)?(ba|z)?sh\b|\b(iwr|Invoke-WebRequest|irm|Invoke-RestMethod)\b[^\n]*\|\s*(iex|Invoke-Expression)\b|-EncodedCommand\b")),
    };

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Short descriptions of what the script appears to do that deserves attention; empty when nothing was recognised.</summary>
    public static IReadOnlyList<string> Scan(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return Array.Empty<string>();

        var findings = new List<string>();
        foreach (var (label, pattern) in Rules)
        {
            try
            {
                if (pattern.IsMatch(script))
                    findings.Add(label);
            }
            catch (RegexMatchTimeoutException)
            {
                findings.Add(label + " (scan timed out)");
            }
        }
        return findings;
    }
}
