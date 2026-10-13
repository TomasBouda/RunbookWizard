using System.Text.Json.Nodes;
using RunbookWizard.Core.Parsing;
using Xunit;

namespace RunbookWizard.Core.Tests;

/// <summary>The documented examples are the contract between spec, schema and parser: all must stay valid.</summary>
public class ExamplesTests
{
    public static IEnumerable<object[]> Examples() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "examples"), "*.runbook.*").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_parses_without_errors_and_validates_against_the_schema(string file)
    {
        var result = RunbookParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "examples", file));

        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        Assert.Empty(SchemaValidator.Validate(result.Canonical));
        Assert.DoesNotContain(result.Diagnostics, d => d.IsError);
    }

    [Fact]
    public void There_are_at_least_three_examples_in_both_forms()
    {
        var files = Examples().Select(e => (string)e[0]).ToList();
        Assert.True(files.Count >= 3);
        Assert.Contains(files, f => f.EndsWith(".md"));
        Assert.Contains(files, f => f.EndsWith(".json"));
    }

    [Fact]
    public void Schema_rejects_what_the_spec_forbids()
    {
        var bad = JsonNode.Parse("""{ "runbook": 1, "title": "t", "steps": [ { "title": "a", "type": "info", "script": "x" } ], "extra": 1 }""");
        var errors = SchemaValidator.Validate(bad);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Path == "" || e.Path.StartsWith("steps[0]"));
    }

    [Fact]
    public void Markdown_and_json_forms_of_the_same_guide_produce_the_same_canonical_tree()
    {
        var md = RunbookParser.Parse("---\nrunbook: 1\ntitle: T\n---\n## Step A\nText\n### Why\nBecause\n```bash\necho hi\n```\n");
        var json = RunbookParser.Parse("""
            { "runbook": 1, "title": "T", "steps": [ { "id": "step-a", "title": "Step A", "description": "Text", "why": "Because", "script": "echo hi", "shell": "bash" } ] }
            """);

        Assert.True(md.IsRunnable && json.IsRunnable);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(json.Canonical, md.Canonical), md.Canonical!.ToJsonString());
    }
}
