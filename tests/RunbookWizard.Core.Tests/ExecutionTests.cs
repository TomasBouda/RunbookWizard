using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;
using RunbookWizard.Core.Parsing;
using Xunit;

namespace RunbookWizard.Core.Tests;

public class TemplateTests
{
    [Fact]
    public void Render_substitutes_escapes_and_reports_missing()
    {
        var text = Template.Render(@"a {{X}} b {{ Y }} c \{{X}} d {{Z}}", n => n switch { "X" => "1", "Y" => "2", _ => null }, out var missing);

        Assert.Equal("a 1 b 2 c {{X}} d {{Z}}", text);
        Assert.Equal(new[] { "Z" }, missing);
    }

    [Fact]
    public void References_ignores_escaped_and_duplicates()
    {
        Assert.Equal(new[] { "A", "B" }, Template.References(@"{{A}} {{B}} {{A}} \{{C}}"));
    }
}

public class ConditionTests
{
    private static Func<string, string?> Vars(params (string, string)[] values) => n => values.FirstOrDefault(v => v.Item1 == n).Item2;

    [Theory]
    [InlineData("{{os}} == windows", true)]
    [InlineData("{{os}} != 'windows'", false)]
    [InlineData("{{Mode}} == 'prod' && {{Backup}}", true)]
    [InlineData("!{{Backup}}", false)]
    [InlineData("{{Missing}}", false)]
    [InlineData("{{Mode}} == dev || ({{Mode}} == prod && {{Backup}} != no)", true)]
    [InlineData("{{Off}}", false)]
    [InlineData("true", true)]
    public void Evaluates(string text, bool expected)
    {
        var c = Condition.Parse(text);
        var value = c.Evaluate(Vars(("os", "windows"), ("Mode", "PROD"), ("Backup", "true"), ("Off", "false")));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{{A}} ==")]
    [InlineData("{{A}} = 1")]
    [InlineData("({{A}}")]
    [InlineData("{{A")]
    [InlineData("{{A}} {{B}}")]
    public void Rejects_malformed_conditions(string text)
    {
        Assert.False(Condition.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Lists_references()
    {
        Assert.Equal(new[] { "A", "B" }, Condition.Parse("{{A}} == 1 && !{{B}} || {{A}}").References);
    }
}

public class VariableStoreTests
{
    private static VariableStore Store(string yamlVariables)
    {
        var result = RunbookParser.Parse($"---\nrunbook: 1\ntitle: T\nvariables:\n{yamlVariables}\n---\n## A\n");
        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        return new VariableStore(result.Runbook!);
    }

    [Fact]
    public void Defaults_are_used_and_secrets_are_masked_in_previews_but_not_in_the_environment()
    {
        var store = Store("  - { name: Pw, type: secret }\n  - { name: Port, type: number, default: 8080 }");
        store.Set("Pw", "s3cret");

        Assert.Equal("8080", store.Resolve("Port"));
        Assert.Equal("s3cret", store.Resolve("Pw"));
        Assert.Equal(VariableStore.Mask, store.ResolveMasked("Pw"));
        Assert.Equal("s3cret", store.ToEnvironment()["RUNBOOK_VAR_Pw"]);
        Assert.Null(store.Resolve("Unknown"));
    }

    [Fact]
    public void Validation_covers_required_type_choice_and_pattern()
    {
        var store = Store("  - { name: A, required: true }\n  - { name: N, type: number }\n  - { name: C, type: choice, choices: [x, y] }\n  - { name: P, pattern: '[a-z]{3}' }");
        store.Set("N", "abc");
        store.Set("C", "z");
        store.Set("P", "abcd");

        var errors = store.ValidateAll();

        Assert.Equal(4, errors.Count);
        store.Set("A", "v"); store.Set("N", "1.5"); store.Set("C", "x"); store.Set("P", "abc");
        Assert.Empty(store.ValidateAll());
    }
}

public class StepEvaluationTests
{
    private static RunResult Result(int? exit, string output = "") => new(exit, output, TimeSpan.Zero, false, false, null);

    [Fact]
    public void Default_rule_is_exit_code_zero_and_the_reason_says_so()
    {
        Assert.True(StepEvaluation.Judge(null, Result(0)).Success);
        var failed = StepEvaluation.Judge(null, Result(2));
        Assert.False(failed.Success);
        Assert.Contains("Exit code 2", failed.Reason);
    }

    [Fact]
    public void Exit_codes_and_output_rules_all_have_to_hold()
    {
        var rule = new SuccessRule { ExitCodes = { 0, 3010 }, OutputMatches = "^done$", OutputNotMatches = "ERROR" };

        Assert.True(StepEvaluation.Judge(rule, Result(3010, "x\ndone\n")).Success);
        Assert.Contains("required pattern", StepEvaluation.Judge(rule, Result(0, "nothing")).Reason);
        Assert.Contains("forbidden pattern", StepEvaluation.Judge(rule, Result(0, "done\nERROR")).Reason);
        Assert.False(StepEvaluation.Judge(rule, Result(1, "done")).Success);
    }

    [Fact]
    public void Timeout_cancel_and_start_errors_fail_with_a_reason()
    {
        Assert.Contains("out of time", StepEvaluation.Judge(null, new RunResult(null, "", TimeSpan.Zero, true, false, null)).Reason);
        Assert.Contains("Cancelled", StepEvaluation.Judge(null, new RunResult(null, "", TimeSpan.Zero, false, true, null)).Reason);
        Assert.Contains("not installed", StepEvaluation.Judge(null, new RunResult(null, "", TimeSpan.Zero, false, false, "not installed")).Reason);
    }

    [Fact]
    public void Captures_use_group_one_or_the_whole_match()
    {
        var values = StepEvaluation.ExtractCaptures(new[]
        {
            new Model.Capture { Variable = "V", Pattern = @"version (\d+\.\d+)" },
            new Model.Capture { Variable = "W", Pattern = @"\d+\.\d+" },
            new Model.Capture { Variable = "N", Pattern = "nomatch" },
        }, "tool version 7.4 ok");

        Assert.Equal("7.4", values["V"]);
        Assert.Equal("7.4", values["W"]);
        Assert.False(values.ContainsKey("N"));
    }
}

public class RiskScannerTests
{
    [Theory]
    [InlineData("Remove-Item C:\\x -Recurse -Force", true)]
    [InlineData("rm -rf /tmp/x", true)]
    [InlineData("iwr https://x.example/a.ps1 | iex", true)]
    [InlineData("curl -s https://x.example/i.sh | sudo bash", true)]
    [InlineData("Restart-Computer", true)]
    [InlineData("Get-Process | Sort-Object CPU", false)]
    [InlineData("Write-Output 'rm is a word'", false)]
    public void Recognises_dangerous_commands(string script, bool flagged)
    {
        Assert.Equal(flagged, RiskScanner.Scan(script).Count > 0);
    }
}
