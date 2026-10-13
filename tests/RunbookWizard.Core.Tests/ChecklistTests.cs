using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;
using RunbookWizard.Core.Parsing;
using Xunit;

namespace RunbookWizard.Core.Tests;

/// <summary>Checklists in manual steps (ADR 0010): the item syntax, the gating rule, validation and the interplay with branching.</summary>
public class ChecklistTests
{
    private static IReadOnlySet<int> Ticked(params int[] indexes) => indexes.ToHashSet();

    // ---- parsing ----

    [Fact]
    public void Items_are_read_from_task_list_lines_and_taken_out_of_the_text()
    {
        var parsed = Checklist.Parse("Do these:\n\n- [ ] First\n- [ ] Second\n\nThen relax.");

        Assert.Equal(new[] { "First", "Second" }, parsed.Items.Select(i => i.Text));
        Assert.All(parsed.Items, i => Assert.True(i.Required));
        Assert.Equal(new[] { 0, 1 }, parsed.Items.Select(i => i.Index));
        Assert.Equal("Do these:\n\nThen relax.", parsed.Text);
    }

    [Theory]
    [InlineData("- [ ] (optional) Wipe it", "Wipe it")]
    [InlineData("- [ ] (Optional)Wipe it", "Wipe it")]
    [InlineData("* [ ] (OPTIONAL)   Wipe it  ", "Wipe it")]
    public void The_optional_marker_makes_an_item_optional_and_is_not_part_of_its_text(string line, string text)
    {
        var item = Assert.Single(Checklist.Parse(line).Items);

        Assert.False(item.Required);
        Assert.Equal(text, item.Text);
    }

    [Theory]
    [InlineData("- [ ] Plain item", "Plain item")]
    [InlineData("+ [ ] Plus bullet", "Plus bullet")]
    [InlineData("- [ ] Mentions (optional) in the middle", "Mentions (optional) in the middle")]
    [InlineData("- [ ] (optional)", "(optional)")]
    public void Anything_else_is_a_required_item(string line, string text)
    {
        var item = Assert.Single(Checklist.Parse(line).Items);

        Assert.True(item.Required);
        Assert.Equal(text, item.Text);
    }

    [Theory]
    [InlineData("- [x] Already ticked")]
    [InlineData("- [] No space")]
    [InlineData("- [ ]")]
    [InlineData("1. [ ] Numbered")]
    [InlineData("    - [ ] Nested or code indented")]
    [InlineData("- Plain list item")]
    public void Lines_that_are_not_checklist_items_stay_in_the_text(string line)
    {
        var parsed = Checklist.Parse(line);

        Assert.False(parsed.HasItems);
        Assert.Equal(line, parsed.Text);
    }

    [Fact]
    public void A_checklist_inside_a_code_fence_is_only_an_example()
    {
        var parsed = Checklist.Parse("Write it like this:\n\n```markdown\n- [ ] Example\n```\n\n- [ ] Real");

        Assert.Equal(new[] { "Real" }, parsed.Items.Select(i => i.Text));
        Assert.Contains("- [ ] Example", parsed.Text);
    }

    [Fact]
    public void Text_without_a_checklist_is_returned_unchanged()
    {
        const string text = "Just  text\n\n\n- a list\n";
        Assert.Equal(text, Checklist.Parse(text).Text);
        Assert.Equal(string.Empty, Checklist.Parse(null).Text);
    }

    [Fact]
    public void Item_text_is_shown_without_inline_markdown_markers()
    {
        Assert.Equal("Remove C:\\Share and restart (see the guide)", Checklist.ToPlainText("Remove `C:\\Share` and **restart** (see [the guide](https://example.com))"));
    }

    // ---- gating ----

    [Fact]
    public void The_step_can_be_confirmed_only_when_all_required_items_are_ticked()
    {
        var items = Checklist.Parse("- [ ] A\n- [ ] (optional) B\n- [ ] C").Items;

        Assert.False(Checklist.IsComplete(items, Ticked()));
        Assert.False(Checklist.IsComplete(items, Ticked(0)));
        Assert.False(Checklist.IsComplete(items, Ticked(0, 1)));
        Assert.True(Checklist.IsComplete(items, Ticked(0, 2)));
        Assert.True(Checklist.IsComplete(items, Ticked(0, 1, 2)));
        Assert.Equal(new[] { "C" }, Checklist.Remaining(items, Ticked(0, 1)).Select(i => i.Text));
    }

    [Fact]
    public void Optional_items_never_block()
    {
        var items = Checklist.Parse("- [ ] (optional) A\n- [ ] (optional) B").Items;

        Assert.True(Checklist.IsComplete(items, Ticked()));
        Assert.Equal(string.Empty, Checklist.Progress(items, Ticked()));
    }

    [Fact]
    public void No_checklist_means_no_gating()
    {
        Assert.True(Checklist.IsComplete(Array.Empty<ChecklistItem>(), Ticked()));
    }

    [Fact]
    public void Progress_counts_only_required_items()
    {
        var items = Checklist.Parse("- [ ] A\n- [ ] (optional) B\n- [ ] C").Items;

        Assert.Equal("0 of 2 required items ticked", Checklist.Progress(items, Ticked()));
        Assert.Equal("1 of 2 required items ticked", Checklist.Progress(items, Ticked(0, 1)));
        Assert.Equal("All required items ticked", Checklist.Progress(items, Ticked(0, 2)));
    }

    // ---- state for later persistence ----

    [Fact]
    public void The_fingerprint_changes_when_items_change_and_not_otherwise()
    {
        var a = Checklist.Parse("- [ ] One\n- [ ] Two").Items;
        var same = Checklist.Parse("Other text\n- [ ] One\n- [ ] Two").Items;
        var edited = Checklist.Parse("- [ ] One\n- [ ] Two!").Items;
        var optional = Checklist.Parse("- [ ] One\n- [ ] (optional) Two").Items;

        Assert.Equal(Checklist.Fingerprint(a), Checklist.Fingerprint(same));
        Assert.NotEqual(Checklist.Fingerprint(a), Checklist.Fingerprint(edited));
        Assert.NotEqual(Checklist.Fingerprint(a), Checklist.Fingerprint(optional));
    }

    // ---- format and validation ----

    private const string Guide = """
        ---
        runbook: 1
        title: T
        ---
        ## Prepare

        ```step
        type: manual
        confirm: Ready
        ```

        Check **everything**:

        - [ ] Sign out
        - [ ] (optional) Wipe the profile
        """;

    [Fact]
    public void A_manual_step_in_the_markdown_form_carries_its_checklist_in_the_description()
    {
        var result = RunbookParser.Parse(Guide);

        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("checklist", StringComparison.OrdinalIgnoreCase));
        var step = Assert.Single(result.Runbook!.Steps);
        var parsed = Checklist.Parse(step.Description);
        Assert.Equal(2, parsed.Items.Count);
        Assert.Equal("Check **everything**:", parsed.Text);
        Assert.Empty(SchemaValidator.Validate(result.Canonical));
    }

    [Fact]
    public void The_json_form_works_the_same_way()
    {
        var result = RunbookParser.Parse("""
            { "runbook": 1, "title": "T", "steps": [ { "id": "a", "title": "A", "type": "manual", "description": "Do:\n\n- [ ] One\n- [ ] (optional) Two" } ] }
            """);

        Assert.True(result.IsRunnable);
        Assert.Equal(2, Checklist.Parse(result.Runbook!.Steps[0].Description).Items.Count);
    }

    [Fact]
    public void A_checklist_in_a_step_that_is_not_manual_is_a_warning()
    {
        var result = RunbookParser.Parse("---\nrunbook: 1\ntitle: T\n---\n## Read\n\n- [ ] One\n");

        Assert.True(result.IsRunnable);
        var warning = Assert.Single(result.Diagnostics, d => !d.IsError);
        Assert.Contains("only works in a manual step", warning.Message);
    }

    [Fact]
    public void A_manual_step_whose_items_are_all_optional_gets_a_hint()
    {
        var result = RunbookParser.Parse("---\nrunbook: 1\ntitle: T\n---\n## Do\n\n```step\ntype: manual\n```\n\n- [ ] (optional) One\n");

        Assert.True(result.IsRunnable);
        Assert.Contains(result.Diagnostics, d => !d.IsError && d.Message.Contains("(optional)"));
    }

    [Fact]
    public void The_example_guide_has_two_checklists_and_no_findings()
    {
        var result = RunbookParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "examples", "checklist-handover.runbook.md"));

        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        var lists = result.Runbook!.Steps.Where(s => s.EffectiveType == StepType.Manual).Select(s => Checklist.Parse(s.Description)).ToList();
        Assert.Equal(new[] { 4, 2 }, lists.Select(l => l.Items.Count));
        Assert.False(lists[0].Items[3].Required);
    }

    // ---- planner: a manual step with a checklist inside a branching flow ----

    [Fact]
    public void An_open_checklist_step_holds_the_path_until_it_is_confirmed_and_branching_continues_after_it()
    {
        var result = RunbookParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "examples", "checklist-handover.runbook.md"));
        var runbook = result.Runbook!;
        var ids = runbook.Steps.Select(s => s.Id).ToList();
        var states = runbook.Steps.Select(_ => new StepFlowState(FlowState.Open)).ToArray();

        // The choice is answered with the full handover: the path now waits at the manual step with the checklist.
        states[ids.IndexOf("pick")] = new StepFlowState(FlowState.Done, "prepare");
        var waiting = FlowPlanner.Evaluate(runbook, states);
        Assert.Equal(ids.IndexOf("prepare"), waiting.Current);
        Assert.DoesNotContain(ids.IndexOf("quick-signoff"), waiting.Walked);

        // Confirming it (only possible with all required items ticked) moves the path on.
        states[ids.IndexOf("prepare")] = new StepFlowState(FlowState.Done);
        var after = FlowPlanner.Evaluate(runbook, states);
        Assert.Equal(ids.IndexOf("summary"), after.Current);

        // The quick option leads to the other manual step instead.
        states[ids.IndexOf("prepare")] = new StepFlowState(FlowState.Open);
        states[ids.IndexOf("pick")] = new StepFlowState(FlowState.Done, "quick-signoff");
        var quick = FlowPlanner.Evaluate(runbook, states);
        Assert.Equal(ids.IndexOf("quick-signoff"), quick.Current);
        Assert.DoesNotContain(ids.IndexOf("prepare"), quick.Walked);
    }
}
