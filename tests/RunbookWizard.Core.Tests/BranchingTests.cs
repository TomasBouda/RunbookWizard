using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;
using RunbookWizard.Core.Parsing;
using Xunit;

namespace RunbookWizard.Core.Tests;

/// <summary>Branching (ADR 0008): the choice step, per-step next and onFailure, validation of targets and loops.</summary>
public class BranchingTests
{
    private static ParseResult Json(string steps, string extra = "") =>
        RunbookParser.Parse($$"""{ "runbook": 1, "title": "T", {{extra}} "steps": [ {{steps}} ] }""");

    private static string Info(string id, string more = "") => $$"""{ "id": "{{id}}", "title": "{{id}}" {{more}} }""";

    private static string Script(string id, string more = "") => $$"""{ "id": "{{id}}", "title": "{{id}}", "script": "echo {{id}}" {{more}} }""";

    private static string Choice(string id, params (string Label, string Goto)[] options) =>
        $$"""{ "id": "{{id}}", "title": "{{id}}", "type": "choice", "options": [ {{string.Join(",", options.Select(o => $$"""{ "label": "{{o.Label}}", "goto": "{{o.Goto}}" }"""))}} ] }""";

    private static string Join(params string[] steps) => string.Join(",", steps);

    // ---- parsing and schema ----

    [Fact]
    public void Markdown_step_block_can_declare_choice_next_and_onFailure()
    {
        var result = RunbookParser.Parse("""
            ---
            runbook: 1
            title: T
            ---
            ## Pick

            ```step
            type: choice
            options:
              - { label: Fast, goto: fast, description: Quick path }
              - { label: Safe, goto: safe }
            ```

            ## Fast

            ```step
            id: fast
            next: done
            onFailure: safe
            ```

            ```powershell
            echo fast
            ```

            ## Safe

            ```step
            id: safe
            ```

            Text.

            ## Done

            ```step
            id: done
            ```

            Text.
            """);

        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        var pick = result.Runbook!.Steps[0];
        Assert.Equal(StepType.Choice, pick.EffectiveType);
        Assert.Equal(new[] { "fast", "safe" }, pick.Options.Select(o => o.Goto));
        Assert.Equal("Quick path", pick.Options[0].Description);
        Assert.Equal("done", result.Runbook.Steps[1].Next);
        Assert.Equal("safe", result.Runbook.Steps[1].OnFailure);
    }

    [Fact]
    public void Options_without_a_type_make_the_step_a_choice()
    {
        var result = Json(Join(
            """{ "id": "q", "title": "q", "options": [ { "label": "a", "goto": "a" }, { "label": "b", "goto": "b" } ] }""",
            Info("a", """, "next": "b" """), Info("b")));

        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        Assert.Equal(StepType.Choice, result.Runbook!.Steps[0].EffectiveType);
    }

    [Theory]
    [InlineData("""{ "id": "q", "title": "q", "type": "choice" }""")]
    [InlineData("""{ "id": "q", "title": "q", "type": "choice", "options": [ { "label": "only", "goto": "q2" } ] }""")]
    [InlineData("""{ "id": "q", "title": "q", "type": "choice", "options": [ { "label": "a", "goto": "q2" }, { "label": "b" } ] }""")]
    [InlineData("""{ "id": "q", "title": "q", "type": "choice", "script": "x", "options": [ { "label": "a", "goto": "q2" }, { "label": "b", "goto": "q2" } ] }""")]
    [InlineData("""{ "id": "q", "title": "q", "type": "script", "script": "x", "options": [ { "label": "a", "goto": "q2" }, { "label": "b", "goto": "q2" } ] }""")]
    [InlineData("""{ "id": "q", "title": "q", "next": "Not An Id" }""")]
    [InlineData("""{ "id": "q", "title": "q", "type": "choice", "options": [ { "label": "a", "goto": "q2", "extra": 1 }, { "label": "b", "goto": "q2" } ] }""")]
    public void Schema_rejects_malformed_branching(string step)
    {
        var result = Json(Join(step, Info("q2")));
        Assert.False(result.IsRunnable);
        Assert.Contains(result.Diagnostics, d => d.IsError);
    }

    [Fact]
    public void Existing_guides_without_branching_are_unaffected()
    {
        var result = Json(Join(Script("a"), Script("b")));
        Assert.True(result.IsRunnable);
        Assert.Empty(result.Diagnostics);
    }

    // ---- target and loop validation ----

    [Theory]
    [InlineData("next", "steps[0].next")]
    [InlineData("onFailure", "steps[0].onFailure")]
    public void Unknown_targets_are_errors_with_their_path(string field, string path)
    {
        var result = Json(Join(Script("a", $", \"{field}\": \"nowhere\""), Script("b")));

        var error = Assert.Single(result.Diagnostics, d => d.IsError);
        Assert.Equal(path, error.Path);
        Assert.Contains("nowhere", error.Message);
    }

    [Fact]
    public void Unknown_option_target_is_an_error()
    {
        var result = Json(Join(Choice("q", ("A", "b"), ("B", "missing")), Info("b")));

        var error = Assert.Single(result.Diagnostics, d => d.IsError);
        Assert.Equal("steps[0].options[1].goto", error.Path);
    }

    [Fact]
    public void A_step_cannot_lead_to_itself()
    {
        var result = Json(Join(Script("a", """, "next": "a" """), Script("b")));

        var error = Assert.Single(result.Diagnostics, d => d.IsError);
        Assert.Equal("steps[0].next", error.Path);
        Assert.Contains("itself", error.Message);
    }

    [Fact]
    public void A_backward_next_that_closes_a_loop_is_an_error()
    {
        var result = Json(Join(Script("a"), Script("b"), Script("c", """, "next": "a" """)));

        var error = Assert.Single(result.Diagnostics, d => d.IsError);
        Assert.Equal("steps[2].next", error.Path);
        Assert.Contains("a -> b -> c -> a", error.Message);
    }

    [Fact]
    public void A_loop_through_onFailure_is_an_error()
    {
        var result = Json(Join(Script("a"), Script("b", """, "onFailure": "a" """)));

        Assert.Contains(result.Diagnostics, d => d.IsError && d.Path == "steps[1].onFailure" && d.Message.Contains("forever"));
    }

    [Fact]
    public void A_loop_through_a_choice_option_is_an_error()
    {
        var result = Json(Join(Info("a"), Choice("q", ("Again", "a"), ("Go on", "z")), Info("z")));

        Assert.Contains(result.Diagnostics, d => d.IsError && d.Path == "steps[1].options[0].goto");
    }

    [Fact]
    public void A_loop_made_of_a_backward_jump_and_the_implicit_next_step_is_reported_at_the_jump()
    {
        // a -> (next) c -> (next) b -> implicit c: the cycle is b -> c -> b
        var result = Json(Join(Script("a", """, "next": "c" """), Script("b"), Script("c", """, "next": "b" """)));

        Assert.Contains(result.Diagnostics, d => d.IsError && d.Path == "steps[2].next");
    }

    [Fact]
    public void Forward_jumps_and_diamonds_are_not_loops()
    {
        var result = Json(Join(
            Choice("q", ("Left", "left"), ("Right", "right")),
            Script("left", """, "next": "end", "onFailure": "end" """),
            Script("right", """, "onFailure": "end" """),
            Info("end")));

        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void A_step_nothing_leads_to_is_a_warning()
    {
        var result = Json(Join(Script("a", """, "next": "c" """), Script("b"), Script("c")));

        Assert.True(result.IsRunnable);
        Assert.Contains(result.Diagnostics, d => !d.IsError && d.Path == "steps[1]" && d.Message.Contains("'b'"));
    }

    [Fact]
    public void Duplicate_option_labels_and_options_on_wrong_steps_are_reported()
    {
        var result = Json(Join(Choice("q", ("Same", "a"), ("same", "b")), Info("a"), Info("b")));

        Assert.Contains(result.Diagnostics, d => d.IsError && d.Path == "steps[0].options[1].label");
    }

    [Fact]
    public void Warnings_for_next_on_a_choice_and_onFailure_on_a_step_that_cannot_fail()
    {
        var result = Json(Join(
            """{ "id": "q", "title": "q", "type": "choice", "next": "a", "options": [ { "label": "x", "goto": "a" }, { "label": "y", "goto": "a" } ] }""",
            Info("a", """, "onFailure": "q2" """), Info("q2")));

        Assert.True(result.IsRunnable);
        Assert.Contains(result.Diagnostics, d => !d.IsError && d.Path == "steps[0].next");
        Assert.Contains(result.Diagnostics, d => !d.IsError && d.Path == "steps[1].onFailure");
    }

    [Fact]
    public void When_conditions_combine_with_branching()
    {
        var result = Json(
            Join(Script("a", """, "when": "{{Mode}} == full", "next": "c" """), Script("b"), Script("c")),
            """ "variables": [ { "name": "Mode", "default": "quick" } ],""");

        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        Assert.Equal("{{Mode}} == full", result.Runbook!.Steps[0].When);
        Assert.Equal("c", result.Runbook.Steps[0].Next);
    }

    // ---- the path through the guide ----

    private static Runbook Load(string steps)
    {
        var result = Json(steps);
        Assert.NotNull(result.Runbook);
        return result.Runbook!;
    }

    private static StepFlowState Open => new(FlowState.Open);
    private static StepFlowState Done => new(FlowState.Done);
    private static StepFlowState Failed => new(FlowState.Failed);
    private static StepFlowState Skipped => new(FlowState.Skipped);
    private static StepFlowState Picked(string target) => new(FlowState.Done, target);

    private static FlowResult Flow(Runbook rb, params StepFlowState[] states)
    {
        var all = states.Concat(Enumerable.Repeat(Open, rb.Steps.Count - states.Length)).ToArray();
        return FlowPlanner.Evaluate(rb, all);
    }

    [Fact]
    public void Without_branching_the_path_is_the_order_of_the_steps()
    {
        var rb = Load(Join(Script("a"), Script("b"), Script("c")));

        Assert.Equal(0, Flow(rb).Current);
        Assert.Equal(2, Flow(rb, Done, Done).Current);
        Assert.Equal(new[] { 0, 1, 2 }, Flow(rb, Done, Done).Walked);
        Assert.True(Flow(rb, Done, Done, Done).IsComplete);
        Assert.Equal(new[] { 0, 1, 2 }, Flow(rb).Reachable.OrderBy(i => i));
    }

    [Fact]
    public void Next_replaces_the_following_step_and_the_skipped_steps_are_not_reachable()
    {
        var rb = Load(Join(Script("a", """, "next": "d" """), Script("b"), Script("c"), Script("d")));

        var flow = Flow(rb, Done);
        Assert.Equal(3, flow.Current);
        Assert.Equal(new[] { 0, 3 }, flow.Walked);
        Assert.DoesNotContain(1, flow.Reachable);
        Assert.DoesNotContain(2, flow.Reachable);
    }

    [Fact]
    public void A_skipped_step_follows_its_next_like_a_finished_one()
    {
        var rb = Load(Join(Script("a", """, "next": "c" """), Script("b"), Script("c")));

        Assert.Equal(2, Flow(rb, Skipped).Current);
    }

    [Fact]
    public void A_choice_waits_for_an_answer_and_then_follows_the_picked_option()
    {
        var rb = Load(Join(Choice("q", ("Left", "left"), ("Right", "right")), Script("left", """, "next": "end" """), Script("right"), Info("end")));

        var waiting = Flow(rb);
        Assert.Equal(0, waiting.Current);
        Assert.Equal(new[] { 0, 1, 2, 3 }, waiting.Reachable.OrderBy(i => i)); // both branches are still possible

        var left = Flow(rb, Picked("left"));
        Assert.Equal(1, left.Current);
        Assert.DoesNotContain(2, left.Reachable);

        var right = Flow(rb, Picked("right"));
        Assert.Equal(2, right.Current);
        Assert.DoesNotContain(1, right.Reachable);

        var finished = Flow(rb, Picked("left"), Done, Open, Done);
        Assert.True(finished.IsComplete);
        Assert.Equal(new[] { 0, 1, 3 }, finished.Walked);
    }

    [Fact]
    public void Choosing_again_reroutes_the_path()
    {
        var rb = Load(Join(Choice("q", ("Left", "left"), ("Right", "right")), Script("left"), Script("right")));

        Assert.Equal(1, Flow(rb, Picked("left")).Current);
        Assert.Equal(2, Flow(rb, Picked("right"), Open, Open).Current);
    }

    [Fact]
    public void A_failure_follows_onFailure_when_one_is_declared()
    {
        var rb = Load(Join(Script("a", """, "onFailure": "rollback" """), Script("b"), Script("rollback"), Info("end")));

        var flow = Flow(rb, Failed);
        Assert.Equal(2, flow.Current);
        Assert.Equal(new[] { 0, 2 }, flow.Walked);
        Assert.DoesNotContain(1, flow.Reachable);
    }

    [Fact]
    public void A_failure_without_a_route_blocks_unless_it_continues_on_failure()
    {
        var blocked = Load(Join(Script("a"), Script("b")));
        Assert.Equal(0, Flow(blocked, Failed).Current);

        var lenient = Load(Join(Script("a", """, "continueOnFailure": true """), Script("b")));
        Assert.Equal(1, Flow(lenient, Failed).Current);
    }

    [Fact]
    public void Success_after_a_retry_goes_back_to_the_normal_route()
    {
        var rb = Load(Join(Script("a", """, "onFailure": "rollback" """), Script("b"), Script("rollback")));

        Assert.Equal(2, Flow(rb, Failed).Current);
        Assert.Equal(1, Flow(rb, Done).Current);
    }

    [Fact]
    public void Retrying_a_failed_step_with_a_route_keeps_the_route_open_while_it_runs()
    {
        var rb = Load(Join(Script("a", """, "onFailure": "rollback" """), Script("b"), Script("rollback")));

        var flow = Flow(rb, Open);
        Assert.Equal(0, flow.Current);
        Assert.Contains(2, flow.Reachable); // the failure route is still possible until the step has run
    }

    [Fact]
    public void Edges_list_the_explicit_routes_and_the_implicit_following_step()
    {
        var rb = Load(Join(Script("a", """, "next": "c", "onFailure": "b" """), Script("b"), Script("c")));

        Assert.Equal(new (int, string?)[] { (2, "next"), (1, "onFailure") }, FlowPlanner.Edges(rb, 0).Select(e => (e.Target, e.Field)));
        Assert.Equal(new (int, string?)[] { (2, null) }, FlowPlanner.Edges(rb, 1).Select(e => (e.Target, e.Field)));
    }
}
