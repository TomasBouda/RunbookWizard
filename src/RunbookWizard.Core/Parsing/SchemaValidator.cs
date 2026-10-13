using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;

namespace RunbookWizard.Core.Parsing;

/// <summary>Validates the canonical JSON tree against the embedded <c>runbook.schema.json</c> (the single normative schema).</summary>
public static class SchemaValidator
{
    private static readonly Lazy<JsonSchema> Schema = new(() =>
    {
        using var stream = typeof(SchemaValidator).Assembly.GetManifestResourceStream("runbook.schema.json")
            ?? throw new InvalidOperationException("The runbook schema is not embedded in the assembly.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    });

    /// <summary>The schema text, e.g. for tools that want to hand it to an AI.</summary>
    public static string SchemaText()
    {
        using var stream = typeof(SchemaValidator).Assembly.GetManifestResourceStream("runbook.schema.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<Diagnostic> Validate(JsonNode? canonical)
    {
        var result = new List<Diagnostic>();
        var evaluation = Schema.Value.Evaluate(canonical, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (evaluation.IsValid)
            return result;

        foreach (var detail in evaluation.Details)
        {
            if (detail.Errors is null)
                continue;
            foreach (var (keyword, message) in detail.Errors)
            {
                // "required"/"properties" failures on a parent are noise next to the specific child finding.
                if (keyword is "properties" or "items" or "allOf" or "$ref" or "then" && detail.Errors.Count == 1 && detail.Details.Count > 0)
                    continue;
                result.Add(new Diagnostic(Severity.Error, ToPath(detail.InstanceLocation.ToString()), message));
            }
        }

        if (result.Count == 0)
            result.Add(new Diagnostic(Severity.Error, "", "The guide does not match the runbook schema."));
        return result.DistinctBy(d => (d.Path, d.Message)).ToList();
    }

    /// <summary>JSON pointer <c>/steps/2/shell</c> to <c>steps[2].shell</c>.</summary>
    public static string ToPath(string pointer)
    {
        var path = new System.Text.StringBuilder();
        foreach (var part in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out var index))
                path.Append('[').Append(index).Append(']');
            else
                path.Append(path.Length == 0 ? string.Empty : ".").Append(part);
        }
        return path.ToString();
    }
}
