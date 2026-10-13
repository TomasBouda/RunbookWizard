using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;
using Xunit;

namespace RunbookWizard.Core.Tests;

public class ScriptRunnerTests
{
    private static ScriptSpec Spec(string script, ShellKind shell = ShellKind.Powershell, int seconds = 60, Dictionary<string, string>? env = null) =>
        new(shell, script, null, env ?? new Dictionary<string, string>(), TimeSpan.FromSeconds(seconds));

    private static bool Has(ShellKind kind) => ShellCatalog.Resolve(kind) is not null;

    [SkippableWindows]
    public async Task Output_and_exit_code_are_captured_live()
    {
        var lines = new List<OutputLine>();
        var result = await ScriptRunner.RunAsync(Spec("Write-Output 'one'\n[Console]::Error.WriteLine('two')\nexit 7"), lines.Add);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("one", result.Output);
        Assert.Contains(lines, l => l.Text == "two" && l.IsError);
    }

    [SkippableWindows]
    public async Task Native_command_failure_and_script_errors_are_not_swallowed_by_the_powershell_wrapper()
    {
        var native = await ScriptRunner.RunAsync(Spec("cmd /c exit 3"));
        var thrown = await ScriptRunner.RunAsync(Spec("Write-Error 'boom'\nWrite-Output 'not reached'"));

        Assert.Equal(3, native.ExitCode);
        Assert.NotEqual(0, thrown.ExitCode);
        Assert.DoesNotContain("not reached", thrown.Output);
    }

    [SkippableWindows]
    public async Task Parameters_reach_the_script_as_environment_variables()
    {
        var env = new Dictionary<string, string> { ["RUNBOOK_VAR_Secret"] = "s3cret-value" };
        var result = await ScriptRunner.RunAsync(Spec("Write-Output $env:RUNBOOK_VAR_Secret", env: env));

        Assert.Contains("s3cret-value", result.Output);
    }

    [SkippableWindows]
    public async Task A_step_that_runs_too_long_is_killed()
    {
        var result = await ScriptRunner.RunAsync(Spec("Start-Sleep -Seconds 30", seconds: 2));

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.False(StepEvaluation.Judge(null, result).Success);
    }

    [SkippableWindows]
    public async Task Cancellation_stops_the_process()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await ScriptRunner.RunAsync(Spec("Start-Sleep -Seconds 30"), null, cts.Token);

        Assert.True(result.Cancelled);
    }

    [SkippableWindows]
    public async Task Cmd_shell_runs()
    {
        var result = await ScriptRunner.RunAsync(Spec("echo from cmd\nexit /b 4", ShellKind.Cmd));

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("from cmd", result.Output);
    }

    [Fact]
    public async Task A_missing_shell_is_reported_not_thrown()
    {
        if (Has(ShellKind.Cmd) && Has(ShellKind.Bash) && Has(ShellKind.Sh))
            return;
        var missing = new[] { ShellKind.Bash, ShellKind.Sh, ShellKind.Cmd }.First(k => !Has(k));
        var result = await ScriptRunner.RunAsync(Spec("echo hi", missing));

        Assert.NotNull(result.StartError);
    }
}

/// <summary>Runs only on Windows with the shell present; reports as a pass elsewhere (xunit 2.9 has no dynamic skip).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SkippableWindowsAttribute : Xunit.FactAttribute
{
    public SkippableWindowsAttribute()
    {
        if (!OperatingSystem.IsWindows() || ShellCatalog.Resolve(ShellKind.Powershell) is null)
            Skip = "needs Windows with PowerShell";
    }
}
