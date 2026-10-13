using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RunbookWizard.Core.Parsing;

/// <summary>
/// Turns the Markdown form of a guide (YAML frontmatter + one <c>## </c> section per step, see docs/FORMAT.md)
/// into the canonical JSON tree. Problems with the structure are reported as diagnostics; the tree is built as far as possible.
/// </summary>
internal static partial class MarkdownRunbookReader
{
    private static readonly HashSet<string> ShellTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "pwsh", "winps", "bash", "sh", "cmd",
    };

    private static readonly string[] OwnedByMarkdown = { "title", "description", "why", "script" };

    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,})\s*(?<info>[^`]*?)\s*$")]
    private static partial Regex FenceOpen();

    [GeneratedRegex(@"^ {0,3}(?<h>#{1,6})\s+(?<text>.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    public static JsonObject? Read(string text, List<Diagnostic> diagnostics)
    {
        var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0)
            index++;

        if (index >= lines.Length || lines[index].TrimEnd() != "---")
        {
            diagnostics.Add(new Diagnostic(Severity.Error, "", "The file must start with a YAML frontmatter block ('---' ... '---') containing 'runbook: 1' and a title.", index + 1));
            return null;
        }

        var end = -1;
        for (var i = index + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() is "---" or "...")
            {
                end = i;
                break;
            }
        }
        if (end < 0)
        {
            diagnostics.Add(new Diagnostic(Severity.Error, "", "The frontmatter block is not closed with '---'.", index + 1));
            return null;
        }

        JsonObject root;
        try
        {
            var parsed = YamlJson.Parse(string.Join('\n', lines[(index + 1)..end]));
            if (parsed is null)
            {
                root = new JsonObject();
            }
            else if (parsed is JsonObject obj)
            {
                root = obj;
            }
            else
            {
                diagnostics.Add(new Diagnostic(Severity.Error, "", "The frontmatter must be a YAML mapping.", index + 2));
                return null;
            }
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            diagnostics.Add(new Diagnostic(Severity.Error, "frontmatter", ex.Message, (int)ex.Start.Line + index + 1));
            return null;
        }

        if (root.ContainsKey("steps"))
            diagnostics.Add(new Diagnostic(Severity.Error, "steps", "Steps are written as '## ' sections in the body, not in the frontmatter.", index + 1));

        var body = new List<(int Number, string Text)>();
        for (var i = end + 1; i < lines.Length; i++)
            body.Add((i + 1, lines[i]));

        var (intro, h1, sections) = SplitSections(body);

        if (!root.ContainsKey("title") && h1 is not null)
            root["title"] = h1;
        if (!root.ContainsKey("description") && intro.Length > 0)
            root["description"] = intro;

        var steps = new JsonArray();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections)
            steps.Add(BuildStep(section, usedIds, diagnostics));
        root["steps"] = steps;
        return root;
    }

    private sealed record Section(string Title, int Line, List<(int Number, string Text)> Lines);

    /// <summary>Splits the body at '## ' headings that are outside code fences; returns the introduction, the H1 title and the sections.</summary>
    private static (string Intro, string? H1, List<Section> Sections) SplitSections(List<(int Number, string Text)> body)
    {
        var intro = new List<string>();
        string? h1 = null;
        var sections = new List<Section>();
        Section? current = null;
        string? fence = null;

        foreach (var (number, line) in body)
        {
            if (fence is null)
            {
                var open = FenceOpen().Match(line);
                if (open.Success)
                {
                    fence = open.Groups["fence"].Value;
                }
                else
                {
                    var heading = Heading().Match(line);
                    if (heading.Success && heading.Groups["h"].Length == 2)
                    {
                        current = new Section(heading.Groups["text"].Value, number, new());
                        sections.Add(current);
                        continue;
                    }
                    if (heading.Success && heading.Groups["h"].Length == 1 && current is null && h1 is null)
                    {
                        h1 = heading.Groups["text"].Value;
                        continue;
                    }
                }
            }
            else if (IsFenceClose(line, fence))
            {
                fence = null;
            }

            if (current is null)
                intro.Add(line);
            else
                current.Lines.Add((number, line));
        }

        return (string.Join('\n', intro).Trim(), h1, sections);
    }

    private static bool IsFenceClose(string line, string fence)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= fence.Length && trimmed.All(c => c == fence[0]);
    }

    private static JsonObject BuildStep(Section section, HashSet<string> usedIds, List<Diagnostic> diagnostics)
    {
        var step = new JsonObject();
        var attributes = new JsonObject();
        var description = new StringBuilder();
        var why = new StringBuilder();
        var target = description;
        string? script = null;
        string? scriptShell = null;
        var attributesSeen = false;
        var inline = new JsonObject();
        var path = $"steps['{section.Title}']";

        for (var i = 0; i < section.Lines.Count; i++)
        {
            var (number, line) = section.Lines[i];
            var open = FenceOpen().Match(line);
            if (open.Success)
            {
                var fence = open.Groups["fence"].Value;
                var info = open.Groups["info"].Value.Trim();
                var tag = info.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

                var content = new List<string>();
                var closed = false;
                var j = i + 1;
                for (; j < section.Lines.Count; j++)
                {
                    if (IsFenceClose(section.Lines[j].Text, fence))
                    {
                        closed = true;
                        break;
                    }
                    content.Add(section.Lines[j].Text);
                }
                if (!closed)
                    diagnostics.Add(new Diagnostic(Severity.Error, path, "A code block is not closed.", number));

                var isStep = tag.Equals("step", StringComparison.OrdinalIgnoreCase);
                var isShell = ShellTags.Contains(tag);
                if (isStep && !attributesSeen)
                {
                    attributesSeen = true;
                    ReadAttributes(string.Join('\n', content), attributes, path, number, diagnostics);
                }
                else if (isShell && script is null)
                {
                    script = string.Join('\n', content).TrimEnd('\n', ' ', '\t');
                    scriptShell = tag.ToLowerInvariant();
                    ReadFenceAttributes(info, inline, path, number, diagnostics);
                }
                else
                {
                    if (isStep)
                        diagnostics.Add(new Diagnostic(Severity.Error, path, "Only one 'step' block is allowed per step.", number));
                    else if (isShell)
                        diagnostics.Add(new Diagnostic(Severity.Error, path, $"Only one script block is allowed per step; tag example code '{tag}' differently (e.g. 'text').", number));
                    else
                        target.AppendLine(line);
                    if (!isStep && !isShell)
                    {
                        foreach (var c in content)
                            target.AppendLine(c);
                        if (closed)
                            target.AppendLine(section.Lines[j].Text);
                    }
                }
                i = closed ? j : section.Lines.Count;
                continue;
            }

            var heading = Heading().Match(line);
            if (heading.Success && heading.Groups["h"].Length <= 3)
            {
                if (heading.Groups["h"].Length == 3 && heading.Groups["text"].Value.Equals("why", StringComparison.OrdinalIgnoreCase))
                {
                    target = why;
                    continue;
                }
                target = description;
            }
            target.AppendLine(line);
        }

        foreach (var (key, value) in inline)
        {
            if (!attributes.ContainsKey(key))
                attributes[key] = value?.DeepClone();
        }
        foreach (var (key, value) in attributes)
        {
            if (OwnedByMarkdown.Contains(key))
                diagnostics.Add(new Diagnostic(Severity.Error, path + "." + key, $"'{key}' comes from the Markdown structure and must not be set in the step block.", section.Line));
            else
                step[key] = value?.DeepClone();
        }

        step["title"] = section.Title;
        if (!step.ContainsKey("id"))
            step["id"] = UniqueId(Slug(section.Title), usedIds);
        else if (step["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var explicitId))
            usedIds.Add(explicitId);

        var descriptionText = description.ToString().Trim();
        if (descriptionText.Length > 0)
            step["description"] = descriptionText;
        var whyText = why.ToString().Trim();
        if (whyText.Length > 0)
            step["why"] = whyText;
        if (script is not null)
        {
            step["script"] = script;
            if (step.TryGetPropertyValue("shell", out var declared) && declared is not null && !string.Equals(declared.ToString(), scriptShell, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic(Severity.Error, path + ".shell", $"The step block says shell '{declared}' but the script block is tagged '{scriptShell}'.", section.Line));
            step["shell"] = scriptShell;
        }
        return step;
    }

    [GeneratedRegex("""(?<key>[A-Za-z_][A-Za-z0-9_-]*)\s*=\s*(?:"(?<q>[^"]*)"|'(?<s>[^']*)'|(?<v>[^\s}]+))""")]
    private static partial Regex InlinePair();

    /// <summary>
    /// Runme-style attributes after the shell tag: <c>```powershell { name=install destructive=true }</c> or the JSON
    /// notation <c>{"name":"install"}</c>. <c>name</c> is the step id. Anything the full 'step' block says wins over these.
    /// </summary>
    private static void ReadFenceAttributes(string info, JsonObject into, string path, int line, List<Diagnostic> diagnostics)
    {
        var open = info.IndexOf('{');
        var close = info.LastIndexOf('}');
        if (open < 0 || close < open)
            return;
        var body = info[open..(close + 1)];
        try
        {
            if (body.TrimStart('{').TrimStart().StartsWith('"'))
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(body) is JsonObject json)
                {
                    foreach (var (key, value) in json)
                        Put(into, key, value is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<string>(out var text) ? Scalar(text) : value?.DeepClone());
                }
                return;
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            diagnostics.Add(new Diagnostic(Severity.Error, path, "Invalid attributes after the shell tag: " + ex.Message, line));
            return;
        }

        foreach (Match pair in InlinePair().Matches(body))
        {
            var raw = pair.Groups["q"].Success ? pair.Groups["q"].Value
                : pair.Groups["s"].Success ? pair.Groups["s"].Value
                : pair.Groups["v"].Value;
            var quoted = pair.Groups["q"].Success || pair.Groups["s"].Success;
            Put(into, pair.Groups["key"].Value, quoted ? JsonValue.Create(raw) : Scalar(raw));
        }
    }

    private static void Put(JsonObject into, string key, JsonNode? value) => into[key == "name" ? "id" : key] = value;

    private static JsonNode? Scalar(string text) =>
        text is "true" or "false" ? JsonValue.Create(text == "true")
        : long.TryParse(text, out var number) ? JsonValue.Create(number)
        : JsonValue.Create(text);

    private static void ReadAttributes(string yaml, JsonObject into, string path, int line, List<Diagnostic> diagnostics)
    {
        try
        {
            var parsed = YamlJson.Parse(yaml);
            if (parsed is JsonObject obj)
            {
                foreach (var (key, value) in obj)
                    into[key] = value?.DeepClone();
            }
            else if (parsed is not null)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, "The 'step' block must be a YAML mapping.", line));
            }
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            diagnostics.Add(new Diagnostic(Severity.Error, path, "Invalid YAML in the 'step' block: " + ex.Message, line));
        }
    }

    public static string Slug(string title)
    {
        var slug = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "step" : slug;
    }

    private static string UniqueId(string slug, HashSet<string> used)
    {
        var id = slug;
        for (var n = 2; !used.Add(id); n++)
            id = $"{slug}-{n}";
        return id;
    }
}
