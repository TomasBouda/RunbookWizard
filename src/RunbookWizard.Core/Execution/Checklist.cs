using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RunbookWizard.Core.Execution;

/// <summary>One checkbox of a manual step. <see cref="Index"/> is the 0-based position among the step's items.</summary>
public sealed record ChecklistItem(int Index, string Text, bool Required);

/// <summary>The description of a step with its checklist taken out, and the checklist.</summary>
public sealed record ChecklistParse(string Text, IReadOnlyList<ChecklistItem> Items)
{
    public bool HasItems => Items.Count > 0;
}

/// <summary>
/// Checklists in manual steps (ADR 0010). An item is a Markdown task-list line in the step description,
/// <c>- [ ] text</c>, outside code fences; <c>- [ ] (optional) text</c> marks an item the person may leave unticked.
/// The step can only be confirmed when every required item is ticked. The tick state is plain data (a set of item
/// indexes, guarded by <see cref="Fingerprint"/>) so that a later "persist run state" can store and restore it.
/// </summary>
public static partial class Checklist
{
    [GeneratedRegex(@"^ {0,3}[-*+] \[ \][ \t]+(?<text>\S.*?)\s*$")]
    private static partial Regex ItemLine();

    [GeneratedRegex(@"^\(optional\)[ \t]*(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex OptionalMarker();

    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,})")]
    private static partial Regex FenceOpen();

    /// <summary>Finds the items and returns the description without their lines (at most one blank line in a row is kept where they were).</summary>
    public static ChecklistParse Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return new ChecklistParse(markdown ?? string.Empty, Array.Empty<ChecklistItem>());

        var items = new List<ChecklistItem>();
        var kept = new List<string>();
        string? fence = null;
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (fence is null)
            {
                if (FenceOpen().Match(line) is { Success: true } open)
                {
                    fence = open.Groups["fence"].Value;
                }
                else if (ItemLine().Match(line) is { Success: true } item)
                {
                    var text = item.Groups["text"].Value;
                    var required = true;
                    if (OptionalMarker().Match(text) is { Success: true } marker && marker.Groups["rest"].Value.Length > 0)
                    {
                        required = false;
                        text = marker.Groups["rest"].Value.TrimEnd();
                    }
                    items.Add(new ChecklistItem(items.Count, text, required));
                    continue;
                }
            }
            else if (line.Trim() is { Length: > 0 } trimmed && trimmed.Length >= fence.Length && trimmed.All(c => c == fence[0]))
            {
                fence = null;
            }
            kept.Add(line);
        }

        if (items.Count == 0)
            return new ChecklistParse(markdown, items);

        var text2 = new StringBuilder();
        var blank = true;
        foreach (var line in kept)
        {
            var isBlank = line.Trim().Length == 0;
            if (isBlank && blank)
                continue;
            text2.Append(line).Append('\n');
            blank = isBlank;
        }
        return new ChecklistParse(text2.ToString().Trim(), items);
    }

    /// <summary>The required items that are not ticked yet. <paramref name="ticked"/> holds item indexes.</summary>
    public static IReadOnlyList<ChecklistItem> Remaining(IReadOnlyList<ChecklistItem> items, IReadOnlySet<int> ticked) =>
        items.Where(i => i.Required && !ticked.Contains(i.Index)).ToList();

    /// <summary>True when every required item is ticked (also when there is no checklist).</summary>
    public static bool IsComplete(IReadOnlyList<ChecklistItem> items, IReadOnlySet<int> ticked) =>
        items.All(i => !i.Required || ticked.Contains(i.Index));

    /// <summary>"2 of 3 required items ticked", or "All required items ticked"; empty without required items.</summary>
    public static string Progress(IReadOnlyList<ChecklistItem> items, IReadOnlySet<int> ticked)
    {
        var required = items.Count(i => i.Required);
        if (required == 0)
            return string.Empty;
        var done = items.Count(i => i.Required && ticked.Contains(i.Index));
        return done == required ? "All required items ticked" : $"{done} of {required} required items ticked";
    }

    /// <summary>The item text as shown next to a checkbox: basic inline Markdown (code, bold, italic, links) without its markers.</summary>
    public static string ToPlainText(string text)
    {
        var plain = LinkPattern().Replace(text, "${label}");
        plain = plain.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
        return plain;
    }

    [GeneratedRegex(@"\[(?<label>[^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkPattern();

    /// <summary>
    /// A short stable hash of the items (text and required flag, in order). Persisted tick state is valid only while the fingerprint
    /// of the step is the same as when it was stored: an edited guide must not inherit ticks that meant something else.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<ChecklistItem> items)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', items.Select(i => (i.Required ? "r:" : "o:") + i.Text))));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }
}
