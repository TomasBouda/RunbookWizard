using System.Text.RegularExpressions;
using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;

namespace RunbookWizard.Core.Parsing;

/// <summary>Checks the schema cannot express: ids, variable references, regular expressions, conditions.</summary>
public static class SemanticValidator
{
    private static readonly string[] BuiltIns = { "os", "runbookDir", "user" };

    public static IReadOnlyList<Diagnostic> Validate(Runbook runbook)
    {
        var result = new List<Diagnostic>();
        var declared = new HashSet<string>(StringComparer.Ordinal);

        for (var v = 0; v < runbook.Variables.Count; v++)
        {
            var variable = runbook.Variables[v];
            var path = $"variables[{v}]";
            if (!declared.Add(variable.Name))
                result.Add(Error($"{path}.name", $"The variable '{variable.Name}' is declared twice."));
            CheckRegex(variable.Pattern, $"{path}.pattern", result);
            if (variable.Type == VariableType.Choice && variable.Default is not null && !variable.Choices.Contains(variable.Default))
                result.Add(Error($"{path}.default", $"The default '{variable.Default}' is not one of the choices."));
            if (variable.Type == VariableType.Secret && variable.Default is not null)
                result.Add(Warning($"{path}.default", "A secret should not have a default value written in the guide."));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var capturedBefore = new HashSet<string>(StringComparer.Ordinal);
        var capturedAnywhere = runbook.Steps.SelectMany(s => s.Captures).Select(c => c.Variable).ToHashSet(StringComparer.Ordinal);

        for (var s = 0; s < runbook.Steps.Count; s++)
        {
            var step = runbook.Steps[s];
            var path = $"steps[{s}]";
            if (!ids.Add(step.Id))
                result.Add(Error($"{path}.id", $"The step id '{step.Id}' is used more than once."));

            var type = step.EffectiveType;
            if (type == StepType.Script && string.IsNullOrWhiteSpace(step.Script))
                result.Add(Error($"{path}.script", "A script step needs a script."));
            if (type != StepType.Script && !string.IsNullOrWhiteSpace(step.Script))
                result.Add(Error($"{path}.script", $"A {type.ToString().ToLowerInvariant()} step cannot have a script."));
            if (type != StepType.Manual && step.Confirm is not null)
                result.Add(Warning($"{path}.confirm", "'confirm' only applies to manual steps."));

            CheckReferences(step.Script, $"{path}.script", declared, capturedBefore, capturedAnywhere, result);
            CheckReferences(step.WorkingDirectory, $"{path}.workingDirectory", declared, capturedBefore, capturedAnywhere, result);
            foreach (var (key, value) in step.Env)
                CheckReferences(value, $"{path}.env.{key}", declared, capturedBefore, capturedAnywhere, result);

            if (step.When is not null)
            {
                if (!Condition.TryParse(step.When, out var condition, out var error))
                {
                    result.Add(Error($"{path}.when", error!));
                }
                else
                {
                    foreach (var name in condition!.References)
                        CheckName(name, $"{path}.when", declared, capturedBefore, capturedAnywhere, result);
                }
            }

            if (step.Success is { } success)
            {
                CheckRegex(success.OutputMatches, $"{path}.success.outputMatches", result);
                CheckRegex(success.OutputNotMatches, $"{path}.success.outputNotMatches", result);
            }
            for (var c = 0; c < step.Captures.Count; c++)
                CheckRegex(step.Captures[c].Pattern, $"{path}.captures[{c}].pattern", result);

            if (type == StepType.Script)
            {
                if (!step.Destructive && RiskScanner.Scan(step.Script).Count > 0)
                    result.Add(Warning($"{path}.destructive", "The script looks potentially destructive (" + string.Join("; ", RiskScanner.Scan(step.Script)) + ") but the step is not marked destructive."));
            }
            if (step.Destructive && string.IsNullOrWhiteSpace(step.Warning))
                result.Add(Warning($"{path}.warning", "A destructive step should say in 'warning' what it destroys."));

            foreach (var capture in step.Captures)
                capturedBefore.Add(capture.Variable);
        }

        return result;
    }

    private static void CheckReferences(string? text, string path, HashSet<string> declared, HashSet<string> capturedBefore, HashSet<string> capturedAnywhere, List<Diagnostic> result)
    {
        foreach (var name in Template.References(text))
            CheckName(name, path, declared, capturedBefore, capturedAnywhere, result);
    }

    private static void CheckName(string name, string path, HashSet<string> declared, HashSet<string> capturedBefore, HashSet<string> capturedAnywhere, List<Diagnostic> result)
    {
        if (declared.Contains(name) || BuiltIns.Contains(name) || capturedBefore.Contains(name))
            return;
        result.Add(capturedAnywhere.Contains(name)
            ? Error(path, $"'{{{{{name}}}}}' is used before the step that captures it.")
            : Error(path, $"'{{{{{name}}}}}' is not defined: declare a variable '{name}', capture it in an earlier step or use a built-in ({string.Join(", ", BuiltIns)})."));
    }

    private static void CheckRegex(string? pattern, string path, List<Diagnostic> result)
    {
        if (string.IsNullOrEmpty(pattern))
            return;
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            result.Add(Error(path, "Invalid regular expression: " + ex.Message));
        }
    }

    private static Diagnostic Error(string path, string message) => new(Severity.Error, path, message);
    private static Diagnostic Warning(string path, string message) => new(Severity.Warning, path, message);
}
