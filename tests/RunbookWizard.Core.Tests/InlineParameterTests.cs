using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;
using RunbookWizard.Core.Parsing;
using Xunit;

namespace RunbookWizard.Core.Tests;

/// <summary>Inline parameters (ADR 0009): what the editable command view is given, and that secrets stay masked there.</summary>
public class InlineParameterTests
{
    private const string Guide = """
        ---
        runbook: 1
        title: T
        variables:
          - { name: Site, default: demo, required: true }
          - { name: Token, type: secret, default: s3cret-value }
          - { name: Port, type: number, default: '8080' }
        ---
        ## Use them
        ```powershell
        deploy --site '{{Site}}' --port {{Port}} --token {{Token}} --os {{os}} --again '{{Site}}' --literal {{!Site}} --path C:\inetpub\{{Site}}
        ```
        """;

    private static (Runbook, VariableStore) Load()
    {
        var result = RunbookParser.Parse(Guide);
        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        return (result.Runbook!, new VariableStore(result.Runbook!, @"C:\guides"));
    }

    [Fact]
    public void Declared_variables_stay_as_placeholders_for_the_view_and_the_rest_is_substituted()
    {
        var (rb, vars) = Load();
        var plan = StepPlanner.Plan(rb, rb.Steps[0], vars);

        Assert.Contains("'{{Site}}'", plan.InlineScript);
        Assert.Contains("--port {{Port}}", plan.InlineScript);
        Assert.Contains("--token {{Token}}", plan.InlineScript);
        Assert.Contains("--os " + vars.Get("os"), plan.InlineScript);
        Assert.DoesNotContain("{{os}}", plan.InlineScript);
    }

    [Fact]
    public void Escaped_placeholders_are_kept_verbatim_for_the_view()
    {
        var (rb, vars) = Load();
        var plan = StepPlanner.Plan(rb, rb.Steps[0], vars);

        Assert.Contains("--literal {{!Site}} ", plan.InlineScript);
        Assert.Contains("--literal {{Site}} ", plan.Script);
    }

    // Regression: `\{{Name}}` used to be the escape, so a placeholder right after a path backslash ("IIS:\AppPools\{{SiteName}}")
    // lost the backslash and was never substituted. A backslash is only a path separator now (ADR 0012).
    [Fact]
    public void A_placeholder_after_a_backslash_is_substituted_and_stays_an_inline_field()
    {
        var (rb, vars) = Load();
        var plan = StepPlanner.Plan(rb, rb.Steps[0], vars);

        Assert.Contains(@"--path C:\inetpub\demo", plan.Script);
        Assert.EndsWith(@"--path C:\inetpub\{{Site}}", plan.InlineScript);
        Assert.EndsWith(@"--path C:\inetpub\demo", plan.MaskedScript);
    }

    [Fact]
    public void Template_substitutes_after_backslashes_and_unc_prefixes()
    {
        string? Lookup(string n) => n == "X" ? "v" : null;
        Assert.Equal(@"IIS:\AppPools\v \\srv\v", Template.Render(@"IIS:\AppPools\{{X}} \\srv\{{X}}", Lookup, out var missing));
        Assert.Empty(missing);
        Assert.Equal(new[] { "X" }, Template.References(@"IIS:\AppPools\{{X}}"));
        Assert.Equal("{{X}}", Template.Render("{{!X}}", Lookup, out _));
    }

    [Fact]
    public void The_secret_value_is_in_neither_the_view_text_nor_the_masked_preview()
    {
        var (rb, vars) = Load();
        var plan = StepPlanner.Plan(rb, rb.Steps[0], vars);

        Assert.DoesNotContain("s3cret-value", plan.InlineScript);
        Assert.DoesNotContain("s3cret-value", plan.MaskedScript);
        Assert.Contains("s3cret-value", plan.Script); // the command that runs
        Assert.Contains(VariableStore.Mask, plan.MaskedScript);
    }

    [Fact]
    public void An_edit_made_in_the_step_reaches_the_executed_command_and_validation()
    {
        var (rb, vars) = Load();
        vars.Set("Site", "edited");
        vars.Set("Port", "not-a-number");

        var plan = StepPlanner.Plan(rb, rb.Steps[0], vars);
        Assert.Contains("--site 'edited'", plan.Script);
        Assert.Contains(plan.Problems, p => p.Contains("must be a number"));

        vars.Set("Port", "9000");
        plan = StepPlanner.Plan(rb, rb.Steps[0], vars);
        Assert.Contains("--port 9000", plan.Script);
        Assert.True(plan.CanRun);
    }

    [Fact]
    public void A_secret_typed_in_the_step_is_masked_in_output_and_in_the_preview()
    {
        var (rb, vars) = Load();
        vars.Set("Token", "typed-in-the-step");

        var plan = StepPlanner.Plan(rb, rb.Steps[0], vars);
        Assert.Contains("typed-in-the-step", plan.Script);
        Assert.DoesNotContain("typed-in-the-step", plan.MaskedScript);
        Assert.Equal("echo " + VariableStore.Mask, vars.MaskSecrets("echo typed-in-the-step"));
    }

    [Fact]
    public void RenderInline_leaves_unknown_names_as_written()
    {
        var text = Template.RenderInline("{{A}} {{B}} {{C}} {{!A}} \\{{A}}", name => name == "A", name => name == "B" ? "b" : null);
        Assert.Equal("{{A}} b {{C}} {{!A}} \\{{A}}", text);
    }
}
