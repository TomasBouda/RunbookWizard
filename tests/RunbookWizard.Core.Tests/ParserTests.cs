using RunbookWizard.Core.Model;
using RunbookWizard.Core.Parsing;
using Xunit;

namespace RunbookWizard.Core.Tests;

public class ParserTests
{
    private const string Minimal = """
        ---
        runbook: 1
        title: Demo
        variables:
          - name: Site
            default: demo
        ---

        Intro text.

        ## First step

        ```step
        destructive: true
        warning: careful
        success:
          exitCodes: [0, 3010]
          outputMatches: 'ok'
        ```

        Does a thing to {{Site}}.

        ### Why

        Because.

        ```powershell
        Write-Output "ok {{Site}}"
        ```

        ## Second step

        Read only.
        """;

    [Fact]
    public void Markdown_form_builds_the_model()
    {
        var result = RunbookParser.Parse(Minimal, "demo.runbook.md");

        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        var rb = result.Runbook!;
        Assert.Equal("Demo", rb.Title);
        Assert.Equal("Intro text.", rb.Description);
        Assert.Equal(2, rb.Steps.Count);

        var first = rb.Steps[0];
        Assert.Equal("first-step", first.Id);
        Assert.Equal(StepType.Script, first.EffectiveType);
        Assert.Equal(ShellKind.Powershell, first.Shell);
        Assert.Equal("Write-Output \"ok {{Site}}\"", first.Script);
        Assert.Equal("Does a thing to {{Site}}.", first.Description);
        Assert.Equal("Because.", first.Why);
        Assert.True(first.Destructive);
        Assert.Equal(new[] { 0, 3010 }, first.Success!.ExitCodes);
        Assert.Equal(StepType.Info, rb.Steps[1].EffectiveType);
    }

    [Fact]
    public void Json_form_gives_the_same_model()
    {
        var json = """
            { "runbook": 1, "title": "Demo", "steps": [
              { "title": "Say hi", "script": "echo hi", "shell": "bash" },
              { "title": "Say hi", "type": "info" } ] }
            """;
        var result = RunbookParser.Parse(json, "x.runbook.json");

        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        Assert.Equal(new[] { "say-hi", "say-hi-2" }, result.Runbook!.Steps.Select(s => s.Id));
        Assert.Equal(ShellKind.Bash, result.Runbook.Steps[0].Shell);
    }

    [Fact]
    public void Headings_and_steps_inside_code_fences_are_not_structure()
    {
        var text = """
            ---
            runbook: 1
            title: T
            ---
            ## Real

            ~~~text
            ## not a step
            ~~~

            ```powershell
            Write-Output 1
            ```
            """;
        var result = RunbookParser.Parse(text);

        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        Assert.Single(result.Runbook!.Steps);
        Assert.Contains("## not a step", result.Runbook.Steps[0].Description);
    }

    [Fact]
    public void Title_falls_back_to_the_h1_heading()
    {
        var result = RunbookParser.Parse("---\nrunbook: 1\n---\n# From heading\n\nIntro\n\n## A\n", null);
        Assert.Equal("From heading", result.Runbook!.Title);
        Assert.Equal("Intro", result.Runbook.Description);
    }

    [Fact]
    public void Unquoted_version_is_text_not_a_number()
    {
        var result = RunbookParser.Parse("---\nrunbook: 1\ntitle: T\nversion: 1.0\n---\n## A\n", null);
        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        Assert.Equal("1.0", result.Runbook!.Version);
    }

    [Theory]
    [InlineData("no frontmatter at all\n## A", "frontmatter")]
    [InlineData("---\nrunbook: 2\ntitle: T\n---\n## A\n```powershell\nx\n```", "")]
    [InlineData("---\nrunbook: 1\ntitle: T\nbogus: 1\n---\n## A\n", "bogus")]
    [InlineData("---\nrunbook: 1\ntitle: T\n---\n## A\n```powershell\nx\n```\n```bash\ny\n```", "Only one script block")]
    [InlineData("---\nrunbook: 1\ntitle: T\n---\n## A\n```step\ntitle: B\n```\n", "comes from the Markdown")]
    [InlineData("---\nrunbook: 1\ntitle: T\n---\n## A\n```step\ntype: script\n```\n", "script")]
    [InlineData("---\nrunbook: 1\ntitle: T\n---\n## A\n```step\nshell: perl\n```\n", "shell")]
    [InlineData("---\nrunbook: 1\ntitle: T\nsteps: []\n---\n## A\n", "Steps are written")]
    public void Structural_errors_make_the_guide_not_runnable(string text, string expected)
    {
        var result = RunbookParser.Parse(text);

        Assert.False(result.IsRunnable);
        Assert.Contains(result.Diagnostics, d => d.IsError && d.ToString().Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Semantic_errors_are_found_with_paths()
    {
        var text = """
            ---
            runbook: 1
            title: T
            variables:
              - { name: A, type: choice, choices: [x, y], default: z }
            ---
            ## One
            ```step
            id: same
            when: "{{Nope}} =="
            success: { outputMatches: '(' }
            ```
            ```powershell
            echo {{Missing}} {{Later}}
            ```
            ## Two
            ```step
            id: same
            captures:
              - { variable: Later, pattern: 'x' }
            ```
            """;
        var result = RunbookParser.Parse(text);
        var all = string.Join("\n", result.Diagnostics);

        Assert.False(result.IsRunnable);
        Assert.Contains("steps[1].id", all);
        Assert.Contains("variables[0].default", all);
        Assert.Contains("steps[0].when", all);
        Assert.Contains("steps[0].success.outputMatches", all);
        Assert.Contains("'{{Missing}}' is not defined", all);
        Assert.Contains("before the step that captures it", all);
    }

    [Fact]
    public void Unmarked_destructive_script_gets_a_warning_but_stays_runnable()
    {
        var result = RunbookParser.Parse("---\nrunbook: 1\ntitle: T\n---\n## A\n```powershell\nRemove-Item C:\\x -Recurse -Force\n```\n");

        Assert.True(result.IsRunnable);
        Assert.Contains(result.Diagnostics, d => d.Severity == Severity.Warning && d.Path.EndsWith(".destructive"));
    }

    [Fact]
    public void Escaped_placeholders_are_not_references()
    {
        var result = RunbookParser.Parse("---\nrunbook: 1\ntitle: T\n---\n## A\n```powershell\necho \\{{Literal}}\n```\n");
        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
    }
}

public class RunmeStyleAttributeTests
{
    [Fact]
    public void Attributes_after_the_shell_tag_work_like_a_step_block()
    {
        var text = "---\nrunbook: 1\ntitle: T\n---\n## Install\n```powershell { name=install destructive=true timeoutSeconds=90 when=\"{{os}} == windows\" }\nWrite-Output 1\n```\n";
        var result = RunbookParser.Parse(text);

        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        var step = result.Runbook!.Steps[0];
        Assert.Equal("install", step.Id);
        Assert.True(step.Destructive);
        Assert.Equal(90, step.TimeoutSeconds);
        Assert.Equal("{{os}} == windows", step.When);
    }

    [Fact]
    public void Json_notation_is_accepted_and_the_step_block_wins_on_conflict()
    {
        var text = "---\nrunbook: 1\ntitle: T\n---\n## Install\n```step\ndestructive: false\n```\n```bash {\"name\":\"x\",\"destructive\":true}\necho 1\n```\n";
        var result = RunbookParser.Parse(text);

        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        Assert.Equal("x", result.Runbook!.Steps[0].Id);
        Assert.False(result.Runbook.Steps[0].Destructive);
    }
}
