using RunbookWizard.Core.Model;

namespace RunbookWizard.Core.Execution;

/// <summary>How far a step has got, as far as the path through the guide is concerned.</summary>
public enum FlowState
{
    /// <summary>Not settled: pending, running, or skipped only because it is on a path not taken.</summary>
    Open,
    Done,
    Failed,
    /// <summary>Skipped by the person or by its <c>when</c> condition (counts as passed).</summary>
    Skipped,
}

/// <summary>The state of one step; <see cref="ChosenTarget"/> is the step id picked on a finished <c>choice</c> step.</summary>
public readonly record struct StepFlowState(FlowState State, string? ChosenTarget = null);

/// <summary>
/// Where the person is in a guide with branches. <see cref="Walked"/> are the steps followed so far (in the order taken) up to
/// and including <see cref="Current"/>, the first step that needs attention (null: the path is complete).
/// <see cref="Reachable"/> are all steps that can still be visited; a pending step outside it is on a path not taken.
/// </summary>
public sealed record FlowResult(IReadOnlyList<int> Walked, int? Current, IReadOnlySet<int> Reachable)
{
    public bool IsComplete => Current is null;
}

/// <summary>
/// The path through a guide. Steps are normally taken in order. <c>next</c> replaces "the following step" after a step
/// succeeded or was skipped, <c>onFailure</c> is taken when a step failed, and a <c>choice</c> step continues at the target
/// of the option picked. The rules live here (not in the UI) so they can be tested and used by the validator.
/// </summary>
public static class FlowPlanner
{
    /// <summary>Index of the step with this id, or -1.</summary>
    public static int IndexOf(Runbook runbook, string? id) =>
        id is null ? -1 : runbook.Steps.FindIndex(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>The step after <paramref name="index"/> when it succeeded or was skipped; <c>Steps.Count</c> means the end of the guide.</summary>
    public static int NextOf(Runbook runbook, int index)
    {
        var step = runbook.Steps[index];
        if (step.EffectiveType != StepType.Choice && step.Next is not null)
        {
            var target = IndexOf(runbook, step.Next);
            if (target >= 0)
                return target;
        }
        return index + 1;
    }

    /// <summary>The step to continue with after <paramref name="index"/> failed, or -1 when no failure route is declared.</summary>
    public static int FailureTargetOf(Runbook runbook, int index) => IndexOf(runbook, runbook.Steps[index].OnFailure);

    /// <summary>Every step that can follow <paramref name="index"/>, with the field of the guide that declares the edge (null: the implicit following step).</summary>
    public static IEnumerable<(int Target, string? Field)> Edges(Runbook runbook, int index)
    {
        var step = runbook.Steps[index];
        if (step.EffectiveType == StepType.Choice)
        {
            for (var o = 0; o < step.Options.Count; o++)
            {
                var target = IndexOf(runbook, step.Options[o].Goto);
                if (target >= 0)
                    yield return (target, $"options[{o}].goto");
            }
            yield return (index + 1, null); // skipping the question
        }
        else
        {
            var next = step.Next is null ? -1 : IndexOf(runbook, step.Next);
            yield return next >= 0 ? (next, "next") : (index + 1, null);
        }

        var failure = FailureTargetOf(runbook, index);
        if (failure >= 0)
            yield return (failure, "onFailure");
    }

    /// <summary>Follows the path from the first step through everything already settled.</summary>
    public static FlowResult Evaluate(Runbook runbook, IReadOnlyList<StepFlowState> states)
    {
        var count = runbook.Steps.Count;
        var walked = new List<int>();
        var seen = new HashSet<int>();
        int? current = null;

        var at = 0;
        while (at < count)
        {
            if (!seen.Add(at))
            {
                current = at; // a loop in a guide that was not validated: stop here instead of spinning
                break;
            }
            walked.Add(at);
            var state = states[at];
            var step = runbook.Steps[at];

            switch (state.State)
            {
                case FlowState.Open:
                    current = at;
                    break;
                case FlowState.Done when step.EffectiveType == StepType.Choice:
                    var chosen = IndexOf(runbook, state.ChosenTarget);
                    at = chosen >= 0 ? chosen : at + 1;
                    continue;
                case FlowState.Done:
                case FlowState.Skipped:
                    at = NextOf(runbook, at);
                    continue;
                case FlowState.Failed:
                    var failure = FailureTargetOf(runbook, at);
                    if (failure >= 0)
                    {
                        at = failure;
                        continue;
                    }
                    if (step.ContinueOnFailure)
                    {
                        at = NextOf(runbook, at);
                        continue;
                    }
                    current = at; // blocked: Retry or Skip
                    break;
            }
            break;
        }

        var reachable = new HashSet<int>(walked);
        if (current is { } start)
        {
            // From the step that needs attention on, every route is still possible.
            var queue = new Queue<int>();
            queue.Enqueue(start);
            reachable.Add(start);
            while (queue.Count > 0)
            {
                foreach (var (target, _) in Edges(runbook, queue.Dequeue()))
                {
                    if (target < count && reachable.Add(target))
                        queue.Enqueue(target);
                }
            }
        }
        return new FlowResult(walked, current, reachable);
    }
}
