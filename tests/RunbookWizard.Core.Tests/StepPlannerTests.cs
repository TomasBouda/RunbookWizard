using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;
using RunbookWizard.Core.Parsing;
using Xunit;

namespace RunbookWizard.Core.Tests;

public class StepPlannerTests
{
    private const string Guide = """
        ---
        runbook: 1
        title: T
        variables:
          - { name: Site, default: demo, required: true }
          - { name: Token, type: secret, default: s3cret-value }
          - { name: Port, type: number, default: abc }
        ---
        ## Use it
        ```step
        env: { SITE_COPY: '{{Site}}' }
        ```
        ```powershell
        Write-Output '{{Site}} {{Token}}'
        ```
        ## Needs a capture
        ```powershell
        Write-Output '{{Port}}'
        ```
        """;

    private static (Runbook, VariableStore) Load()
    {
        var result = RunbookParser.Parse(Guide);
        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        return (result.Runbook!, new VariableStore(result.Runbook!, @"C:\guides"));
    }

    [Fact]
    public void Plan_substitutes_and_masks_secrets_only_in_the_preview()
    {
        var (rb, vars) = Load();
        var plan = StepPlanner.Plan(rb, rb.Steps[0], vars);

        Assert.Equal("Write-Output 'demo s3cret-value'", plan.Script);
        Assert.Equal("Write-Output 'demo " + VariableStore.Mask + "'", plan.MaskedScript);
        Assert.Equal("demo", plan.Environment["SITE_COPY"]);
        Assert.Equal("s3cret-value", plan.Environment["RUNBOOK_VAR_Token"]);
        Assert.Equal(@"C:\guides", plan.WorkingDirectory);
        Assert.Equal(StepPlanner.DefaultTimeout, plan.Timeout);
    }

    [Fact]
    public void Plan_reports_invalid_values_and_blocks_running()
    {
        var (rb, vars) = Load();
        var plan = StepPlanner.Plan(rb, rb.Steps[1], vars);

        Assert.False(plan.CanRun);
        Assert.Contains(plan.Problems, p => p.Contains("must be a number"));
    }

    [Fact]
    public void Plan_requires_required_values()
    {
        var (rb, vars) = Load();
        vars.Set("Site", "");

        Assert.Contains(StepPlanner.Plan(rb, rb.Steps[0], vars).Problems, p => p.Contains("required"));
    }

    [Fact]
    public void Secrets_are_masked_in_output_text()
    {
        var (_, vars) = Load();
        Assert.Equal("token=" + VariableStore.Mask + "!", vars.MaskSecrets("token=s3cret-value!"));
        Assert.Equal("nothing here", vars.MaskSecrets("nothing here"));
    }
}
