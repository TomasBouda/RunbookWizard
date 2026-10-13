using System.Text.RegularExpressions;
using RunbookWizard.Core.Model;

namespace RunbookWizard.Core.Execution;

/// <summary>The player's verdict on a finished script and the human-readable reason for it.</summary>
public sealed record Verdict(bool Success, string Reason);

public static class StepEvaluation
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Applies the step's success rule (default: exit code 0) and explains which rule decided.</summary>
    public static Verdict Judge(SuccessRule? rule, RunResult result)
    {
        if (result.StartError is not null)
            return new Verdict(false, result.StartError);
        if (result.Cancelled)
            return new Verdict(false, "Cancelled by you.");
        if (result.TimedOut)
            return new Verdict(false, "The step ran out of time and was stopped.");

        var codes = rule is { ExitCodes.Count: > 0 } ? rule.ExitCodes : new List<int> { 0 };
        var exit = result.ExitCode ?? -1;
        var reasons = new List<string>();
        if (!codes.Contains(exit))
        {
            var expected = codes.Count == 1 ? $"expected {codes[0]}" : $"expected one of {string.Join(", ", codes)}";
            return new Verdict(false, $"Exit code {exit} ({expected}).");
        }
        reasons.Add(codes.Count == 1 && codes[0] == 0 && rule is not { ExitCodes.Count: > 0 }
            ? "Exit code 0."
            : $"Exit code {exit} is in the success list ({string.Join(", ", codes)}).");

        if (rule?.OutputMatches is { Length: > 0 } must)
        {
            if (!IsMatch(must, result.Output))
                return new Verdict(false, $"Exit code {exit}, but the output does not match the required pattern /{must}/.");
            reasons.Add($"The output matches /{must}/.");
        }
        if (rule?.OutputNotMatches is { Length: > 0 } mustNot)
        {
            if (IsMatch(mustNot, result.Output))
                return new Verdict(false, $"Exit code {exit}, but the output contains the forbidden pattern /{mustNot}/.");
            reasons.Add($"The output does not contain /{mustNot}/.");
        }
        return new Verdict(true, string.Join(" ", reasons));
    }

    /// <summary>Values to store after a run: group 1 of the first match, else the whole match, trimmed. Captures without a match are omitted.</summary>
    public static IReadOnlyDictionary<string, string> ExtractCaptures(IEnumerable<Model.Capture> captures, string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var capture in captures)
        {
            try
            {
                var match = Regex.Match(output, capture.Pattern, RegexOptions.Multiline, RegexTimeout);
                if (!match.Success)
                    continue;
                var value = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
                values[capture.Variable] = value.Trim();
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }
        return values;
    }

    private static bool IsMatch(string pattern, string text)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.Multiline, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
