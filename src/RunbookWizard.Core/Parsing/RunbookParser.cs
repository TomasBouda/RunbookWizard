using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RunbookWizard.Core.Model;

namespace RunbookWizard.Core.Parsing;

/// <summary>
/// Reads a guide in either form (Markdown with YAML frontmatter, or JSON), validates it against the schema and the
/// semantic rules and returns the typed model with all findings. Nothing in a guide is executed here.
/// </summary>
public static class RunbookParser
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static ParseResult ParseFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ParseResult
            {
                SourcePath = path,
                Diagnostics = new[] { new Diagnostic(Severity.Error, "", $"Cannot read the file: {ex.Message}") },
            };
        }
        return Parse(text, path);
    }

    /// <param name="text">The file content.</param>
    /// <param name="sourcePath">Used to recognise the JSON form by extension; null = decide by content.</param>
    public static ParseResult Parse(string text, string? sourcePath = null)
    {
        var diagnostics = new List<Diagnostic>();
        var isJson = sourcePath is not null && sourcePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || sourcePath is null && text.TrimStart('﻿', ' ', '\t', '\r', '\n').StartsWith('{');

        JsonNode? canonical;
        if (isJson)
        {
            try
            {
                canonical = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException ex)
            {
                return Failed(sourcePath, new Diagnostic(Severity.Error, "", "Invalid JSON: " + ex.Message, (int?)ex.LineNumber + 1));
            }
        }
        else
        {
            canonical = MarkdownRunbookReader.Read(text, diagnostics);
        }

        if (canonical is null)
            return Failed(sourcePath, diagnostics.ToArray());

        diagnostics.AddRange(SchemaValidator.Validate(canonical));

        Runbook? runbook = null;
        if (diagnostics.All(d => !d.IsError))
        {
            try
            {
                runbook = canonical.Deserialize<Runbook>(JsonOptions);
            }
            catch (JsonException ex)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, ex.Path ?? "", ex.Message));
            }
            if (runbook is not null)
            {
                EnsureStepIds(runbook);
                diagnostics.AddRange(SemanticValidator.Validate(runbook));
            }
        }

        return new ParseResult
        {
            Runbook = runbook,
            Canonical = canonical,
            Diagnostics = diagnostics,
            SourcePath = sourcePath,
        };
    }

    /// <summary>Gives steps without an id the slug of their title (JSON form); Markdown steps already have one.</summary>
    private static void EnsureStepIds(Runbook runbook)
    {
        var used = runbook.Steps.Where(s => !string.IsNullOrEmpty(s.Id)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var step in runbook.Steps.Where(s => string.IsNullOrEmpty(s.Id)))
        {
            var slug = MarkdownRunbookReader.Slug(step.Title);
            var id = slug;
            for (var n = 2; !used.Add(id); n++)
                id = $"{slug}-{n}";
            step.Id = id;
        }
    }

    private static ParseResult Failed(string? path, params Diagnostic[] diagnostics) => new() { SourcePath = path, Diagnostics = diagnostics };
}
