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
            if (type == StepType.Choice && step.Options.Count == 0)
                result.Add(Error($"{path}.options", "A choice step needs at least two options."));
            if (type != StepType.Choice && step.Options.Count > 0)
                result.Add(Error($"{path}.options", $"Only a choice step has options, this is a {type.ToString().ToLowerInvariant()} step."));
            if (type == StepType.Choice && step.Next is not null)
                result.Add(Warning($"{path}.next", "'next' is ignored on a choice step: the options say where to go."));
            if (type != StepType.Script && step.OnFailure is not null)
                result.Add(Warning($"{path}.onFailure", "'onFailure' only applies to script steps; the others cannot fail."));
            if (type == StepType.Choice)
            {
                var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var o = 0; o < step.Options.Count; o++)
                {
                    if (!labels.Add(step.Options[o].Label))
                        result.Add(Error($"{path}.options[{o}].label", $"The option label '{step.Options[o].Label}' is used more than once in this step."));
                }
            }
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

        CheckFlow(runbook, result);
        return result;
    }

    /// <summary>Branching: every target must exist, no route may lead back to a step already passed, and every step should be reachable.</summary>
    private static void CheckFlow(Runbook runbook, List<Diagnostic> result)
    {
        var steps = runbook.Steps;
        var known = steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        for (var s = 0; s < steps.Count; s++)
        {
            var step = steps[s];
            CheckTarget(step.Next, $"steps[{s}].next", step.Id, known, result);
            CheckTarget(step.OnFailure, $"steps[{s}].onFailure", step.Id, known, result);
            for (var o = 0; o < step.Options.Count; o++)
                CheckTarget(step.Options[o].Goto, $"steps[{s}].options[{o}].goto", step.Id, known, result);
        }

        // Loops: depth-first search over every route a step can take (explicit jumps and the implicit following step).
        const int white = 0, grey = 1, black = 2;
        var colour = new int[steps.Count];
        var stack = new List<int>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        void Visit(int at)
        {
            colour[at] = grey;
            stack.Add(at);
            foreach (var (target, field) in FlowPlanner.Edges(runbook, at))
            {
                if (target >= steps.Count || target == at)
                    continue; // the end of the guide; a jump to itself is reported by CheckTarget
                if (colour[target] == grey)
                    ReportLoop(at, target, field);
                else if (colour[target] == white)
                    Visit(target);
            }
            stack.RemoveAt(stack.Count - 1);
            colour[at] = black;
        }

        void ReportLoop(int from, int to, string? field)
        {
            var loop = stack.SkipWhile(i => i != to).ToList();
            if (!reported.Add(string.Join(",", loop.OrderBy(i => i))))
                return;
            var names = string.Join(" -> ", loop.Select(i => steps[i].Id).Append(steps[to].Id));
            // Point at an explicit jump of the loop; the implicit following step is never the cause.
            var where = $"steps[{from}]" + (field is null ? string.Empty : "." + field);
            if (field is null)
            {
                for (var k = 0; k < loop.Count; k++)
                {
                    var following = k + 1 < loop.Count ? loop[k + 1] : to;
                    var edge = FlowPlanner.Edges(runbook, loop[k]).FirstOrDefault(e => e.Field is not null && e.Target == following);
                    if (edge.Field is not null)
                    {
                        where = $"steps[{loop[k]}].{edge.Field}";
                        break;
                    }
                }
            }
            result.Add(Error(where, $"These steps would repeat forever: {names}. Guides can branch but must not loop."));
        }

        for (var s = 0; s < steps.Count; s++)
        {
            if (colour[s] == white)
                Visit(s);
        }

        // Reachability from the first step.
        if (steps.Count > 0)
        {
            var reachable = new HashSet<int> { 0 };
            var queue = new Queue<int>();
            queue.Enqueue(0);
            while (queue.Count > 0)
            {
                foreach (var (target, _) in FlowPlanner.Edges(runbook, queue.Dequeue()))
                {
                    if (target < steps.Count && reachable.Add(target))
                        queue.Enqueue(target);
                }
            }
            for (var s = 1; s < steps.Count; s++)
            {
                if (!reachable.Contains(s))
                    result.Add(Warning($"steps[{s}]", $"No route leads to the step '{steps[s].Id}': it can never be offered."));
            }
        }
    }

    private static void CheckTarget(string? target, string path, string ownId, HashSet<string> known, List<Diagnostic> result)
    {
        if (target is null)
            return;
        if (!known.Contains(target))
            result.Add(Error(path, $"There is no step with the id '{target}'."));
        else if (string.Equals(target, ownId, StringComparison.Ordinal))
            result.Add(Error(path, "A step cannot lead to itself."));
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
