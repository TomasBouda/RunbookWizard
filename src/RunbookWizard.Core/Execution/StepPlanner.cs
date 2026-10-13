using RunbookWizard.Core.Model;

namespace RunbookWizard.Core.Execution;

/// <summary>
/// Everything needed to run (and to show before running) one script step: the exact script after substitution,
/// a masked copy for display and logs, folder, environment, limits, and reasons why it cannot run yet.
/// </summary>
public sealed record StepPlan(
    ShellKind Shell,
    string Script,
    string MaskedScript,
    string InlineScript,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Risks)
{
    public bool CanRun => Problems.Count == 0;

    public ScriptSpec ToSpec() => new(Shell, Script, WorkingDirectory, Environment, Timeout);
}

public static class StepPlanner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(600);

    public static StepPlan Plan(Runbook runbook, Step step, VariableStore variables)
    {
        var shell = runbook.ShellOf(step);
        var problems = new List<string>();

        var script = Template.Render(step.Script, variables.Resolve, out var missing);
        var masked = Template.Render(step.Script, variables.ResolveMasked, out _);
        var directory = Template.Render(step.WorkingDirectory, variables.Resolve, out var missingDirectory);
        if (string.IsNullOrWhiteSpace(directory))
            directory = variables.Get("runbookDir");

        var environment = variables.ToEnvironment();
        var references = new HashSet<string>(Template.References(step.Script), StringComparer.Ordinal);
        foreach (var name in Template.References(step.WorkingDirectory))
            references.Add(name);
        var unresolved = new HashSet<string>(missing, StringComparer.Ordinal);
        foreach (var name in missingDirectory)
            unresolved.Add(name);
        foreach (var (key, value) in step.Env)
        {
            environment[key] = Template.Render(value, variables.Resolve, out var missingEnv);
            foreach (var name in Template.References(value))
                references.Add(name);
            foreach (var name in missingEnv)
                unresolved.Add(name);
        }

        foreach (var name in unresolved)
            problems.Add($"'{name}' has no value yet: a previous step has to provide it.");
        problems.AddRange(variables.ValidateAll(references));
        if (step.EffectiveType == StepType.Script && ShellCatalog.Resolve(shell) is null)
            problems.Add($"The shell '{shell.ToString().ToLowerInvariant()}' is not installed on this machine.");

        var timeout = step.TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(step.TimeoutSeconds.Value) : DefaultTimeout;
        var inline = Template.RenderInline(step.Script, variables.IsDeclared, variables.ResolveMasked);
        return new StepPlan(shell, script, masked, inline, directory, environment, timeout, problems, RiskScanner.Scan(script));
    }
}
