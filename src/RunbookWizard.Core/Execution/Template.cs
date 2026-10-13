using System.Text.RegularExpressions;

namespace RunbookWizard.Core.Execution;

/// <summary>
/// <c>{{Name}}</c> substitution. <c>\{{Name}}</c> is a literal. Values are inserted as plain text without quoting
/// or escaping (the format says so, see docs/FORMAT.md section 4.2).
/// </summary>
public static partial class Template
{
    [GeneratedRegex(@"(?<esc>\\)?\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>Names of the variables a text refers to (escaped placeholders excluded), in order of appearance, without duplicates.</summary>
    public static IReadOnlyList<string> References(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();
        return Placeholder().Matches(text)
            .Where(m => !m.Groups["esc"].Success)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Replaces placeholders using <paramref name="lookup"/>; names it cannot resolve stay as written and are reported.</summary>
    public static string Render(string? text, Func<string, string?> lookup, out IReadOnlyList<string> missing)
    {
        var unresolved = new List<string>();
        missing = unresolved;
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        return Placeholder().Replace(text, m =>
        {
            if (m.Groups["esc"].Success)
                return m.Value[1..];
            var name = m.Groups["name"].Value;
            var value = lookup(name);
            if (value is null)
            {
                if (!unresolved.Contains(name))
                    unresolved.Add(name);
                return m.Value;
            }
            return value;
        });
    }
}
