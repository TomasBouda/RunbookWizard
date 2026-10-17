using System.Text.Json;
using ModelContextProtocol;
using RunbookWizard.App.Mcp;
using RunbookWizard.Core.Execution;

namespace RunbookWizard.App.Tests;

public sealed class RunbookToolsTests : IDisposable
{
    private readonly Harness _h = new(confirmTimeout: TimeSpan.FromSeconds(30));

    public void Dispose() => _h.Dispose();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static Dictionary<string, JsonElement> Values(params (string Name, object Value)[] entries)
        => entries.ToDictionary(e => e.Name, e => JsonSerializer.SerializeToElement(e.Value));

    // ---- run_step ----

    [WindowsFact]
    public async Task RunStep_ConfirmedRun_RunsTheStepAndMasksSecrets()
    {
        _h.OpenAtHello();
        _h.Parameter("Password").Value = "s3cr3t-pa55";

        var call = _h.Tools.RunStep(null!, "hello");
        var request = await _h.WaitForRequestAsync();
        Assert.Equal("an agent", request.Agent);
        Assert.Same(_h.Step("hello"), request.Step);
        Assert.Equal(1, _h.FrontCalls);
        _h.Answer(request, run: true);

        var result = Parse(await call);
        Assert.Equal("succeeded", result.GetProperty("outcome").GetString());
        Assert.Equal("hello", result.GetProperty("stepId").GetString());
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        var output = result.GetProperty("output").GetString()!;
        Assert.Contains("Hello web01 on 8080", output);
        Assert.DoesNotContain("s3cr3t-pa55", output);
        Assert.False(result.GetProperty("outputTruncated").GetBoolean());
        Assert.Equal("wipe", result.GetProperty("currentStep").GetString());
        Assert.False(result.GetProperty("finished").GetBoolean());

        Assert.True(_h.Step("hello").IsDone);
        Assert.Equal("Run at the request of an agent, confirmed by you.", _h.Step("hello").AgentNote);
        Assert.Null(_h.Player.AgentRequest);
    }

    [WindowsFact]
    public async Task RunStep_Declined_RunsNothing()
    {
        _h.OpenAtHello();

        var call = _h.Tools.RunStep(null!, "hello");
        _h.Answer(await _h.WaitForRequestAsync(), run: false);

        var result = Parse(await call);
        Assert.Equal("declined", result.GetProperty("outcome").GetString());
        Assert.Contains("declined", result.GetProperty("reason").GetString());
        Assert.True(_h.Step("hello").IsPending);
        Assert.False(_h.Step("hello").HasOutput);
        Assert.Null(_h.Player.AgentRequest);
    }

    [WindowsFact]
    public async Task RunStep_NoAnswerInTime_CountsAsDeclined()
    {
        using var h = new Harness(confirmTimeout: TimeSpan.FromMilliseconds(200));
        h.OpenAtHello();

        var result = Parse(await h.Tools.RunStep(null!, "hello"));

        Assert.Equal("declined", result.GetProperty("outcome").GetString());
        Assert.Contains("No answer within", result.GetProperty("reason").GetString());
        Assert.True(h.Step("hello").IsPending);
        Assert.Null(h.Player.AgentRequest);
    }

    [WindowsFact]
    public async Task RunStep_SecondRequestWhileOneWaits_IsRefused()
    {
        _h.OpenAtHello();
        var first = _h.Tools.RunStep(null!, "hello");
        var request = await _h.WaitForRequestAsync();

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.RunStep(null!, "hello"));
        Assert.Contains("already waiting", ex.Message);

        _h.Answer(request, run: false);
        await first;
    }

    [WindowsFact]
    public async Task RunStep_StepThatIsNotCurrent_IsRefused()
    {
        _h.OpenAtHello();

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.RunStep(null!, "wipe"));

        Assert.Contains("not the current step", ex.Message);
        Assert.Contains("'hello'", ex.Message);
        Assert.Null(_h.Player.AgentRequest);
    }

    [Theory]
    [InlineData("intro", "an info")]
    [InlineData("handover", "a manual")]
    public async Task RunStep_InfoOrManualStep_IsLeftToThePerson(string stepId, string kind)
    {
        _h.Player.Open(_h.GuidePath);

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.RunStep(null!, stepId));

        Assert.Contains($"is {kind} step", ex.Message);
        Assert.Contains("the person has to complete it in the app", ex.Message);
    }

    [Fact]
    public async Task RunStep_UnknownStep_NamesTheSteps()
    {
        _h.Player.Open(_h.GuidePath);

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.RunStep(null!, "nope"));

        Assert.Contains("No step 'nope'", ex.Message);
        Assert.Contains("intro, hello, wipe, admin, tidy, handover", ex.Message);
    }

    [Fact]
    public async Task RunStep_NoGuide_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.RunStep(null!, "hello"));
        Assert.Contains("No guide is open", ex.Message);
    }

    [WindowsFact]
    public async Task RunStep_ElevatedStepInPlayerWithoutAdministratorRights_IsRefused()
    {
        _h.OpenAtHello();
        _h.Step("hello").SkipCommand.Execute(null);
        _h.Step("wipe").SkipCommand.Execute(null);

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.RunStep(null!, "admin"));

        Assert.Equal(ElevationGate.BlockedReason, ex.Message);
        Assert.Null(_h.Player.AgentRequest);
    }

    [WindowsFact]
    public async Task RunStep_DestructiveStep_RunsAfterTheBannerWithoutASecondClick()
    {
        _h.OpenAtHello();
        _h.Step("hello").SkipCommand.Execute(null);

        var call = _h.Tools.RunStep(null!, "wipe");
        _h.Answer(await _h.WaitForRequestAsync(), run: true);
        var result = Parse(await call);

        Assert.Equal("succeeded", result.GetProperty("outcome").GetString());
        Assert.Contains("wiping web01", result.GetProperty("output").GetString());
        Assert.False(_h.Step("wipe").ConfirmPending);
    }

    [WindowsFact]
    public async Task RunStep_MissingRequiredValue_IsRefusedBeforeAsking()
    {
        _h.Player.Open(_h.GuidePath);
        _h.Step("intro").FinishCommand.Execute(null);

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.RunStep(null!, "hello"));

        Assert.Contains("cannot run yet", ex.Message);
        Assert.Null(_h.Player.AgentRequest);
    }

    // ---- fill_placeholders ----

    [Fact]
    public async Task FillPlaceholders_SetsValuesAndMarksThem()
    {
        _h.Player.Open(_h.GuidePath);

        var result = Parse(await _h.Tools.FillPlaceholders(null!, Values(("Target", "web02"), ("Port", 9090))));

        Assert.Equal(["Target", "Port"], result.GetProperty("filled").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0, result.GetProperty("problems").GetArrayLength());
        Assert.Equal("web02", _h.Parameter("Target").Value);
        Assert.Equal("9090", _h.Parameter("Port").Value);
        Assert.Equal("Filled in by an agent", _h.Parameter("Target").FilledByText);
    }

    [Fact]
    public async Task FillPlaceholders_PersonEditsTheValue_ClearsTheMark()
    {
        _h.Player.Open(_h.GuidePath);
        await _h.Tools.FillPlaceholders(null!, Values(("Target", "web02")));

        _h.Parameter("Target").Value = "web03";

        Assert.False(_h.Parameter("Target").IsFilledByAgent);
    }

    [Fact]
    public async Task FillPlaceholders_ValueTheGuideDoesNotAccept_IsReportedAsAProblem()
    {
        _h.Player.Open(_h.GuidePath);

        var result = Parse(await _h.Tools.FillPlaceholders(null!, Values(("Port", "not-a-number"))));

        var problem = Assert.Single(result.GetProperty("problems").EnumerateArray());
        Assert.Equal("Port", problem.GetProperty("name").GetString());
    }

    [Fact]
    public async Task FillPlaceholders_SecretPlaceholder_IsRefusedAndNothingIsSet()
    {
        _h.Player.Open(_h.GuidePath);

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.FillPlaceholders(null!, Values(("Target", "web02"), ("Password", "zz-secret-value"))));

        Assert.Contains("Secret placeholder(s) refused: Password", ex.Message);
        Assert.Contains("Nothing was set", ex.Message);
        Assert.DoesNotContain("zz-secret-value", ex.Message);
        Assert.Null(_h.Parameter("Target").Value);
        Assert.Null(_h.Parameter("Password").Value);
    }

    [Fact]
    public async Task FillPlaceholders_UnknownName_IsRefusedNamingIt()
    {
        _h.Player.Open(_h.GuidePath);

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.FillPlaceholders(null!, Values(("Target", "web02"), ("Hostname", "x"))));

        Assert.Contains("Unknown placeholder(s): Hostname", ex.Message);
        Assert.Contains("Target, Port, Password", ex.Message);
        Assert.Null(_h.Parameter("Target").Value);
    }

    [Fact]
    public async Task FillPlaceholders_MoreThan100Entries_IsRefused()
    {
        _h.Player.Open(_h.GuidePath);
        var values = Enumerable.Range(0, RunbookTools.MaxValues + 1).ToDictionary(i => "v" + i, _ => JsonSerializer.SerializeToElement("x"));

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.FillPlaceholders(null!, values));

        Assert.Contains("the limit is 100", ex.Message);
    }

    [Fact]
    public async Task FillPlaceholders_ValueOver4KB_IsRefused()
    {
        _h.Player.Open(_h.GuidePath);

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.FillPlaceholders(null!, Values(("Target", new string('a', RunbookTools.MaxValueBytes + 1)))));

        Assert.Contains("longer than", ex.Message);
        Assert.Null(_h.Parameter("Target").Value);
    }

    [Fact]
    public async Task FillPlaceholders_ObjectValue_IsRefused()
    {
        _h.Player.Open(_h.GuidePath);

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.FillPlaceholders(null!, Values(("Target", new { a = 1 }))));

        Assert.Contains("must be a string, number or boolean", ex.Message);
    }

    [Fact]
    public async Task FillPlaceholders_NoGuide_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.FillPlaceholders(null!, Values(("Target", "x"))));
        Assert.Contains("No guide is open", ex.Message);
    }

    [WindowsFact]
    public async Task FillPlaceholders_WhileARequestWaits_IsRefused()
    {
        _h.OpenAtHello();
        var call = _h.Tools.RunStep(null!, "hello");
        var request = await _h.WaitForRequestAsync();

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.FillPlaceholders(null!, Values(("Target", "other"))));

        Assert.Contains("waiting for the person", ex.Message);
        Assert.Equal("web01", _h.Parameter("Target").Value);
        _h.Answer(request, run: false);
        await call;
    }

    // ---- open_runbook ----

    [Fact]
    public async Task OpenRunbook_OpensTheGuideMarksItAndBringsTheWindowForward()
    {
        var result = Parse(await _h.Tools.OpenRunbook(null!, _h.GuidePath));

        Assert.Equal(_h.GuidePath, result.GetProperty("path").GetString());
        Assert.Equal("Agent test guide", result.GetProperty("title").GetString());
        Assert.True(result.GetProperty("runnable").GetBoolean());
        Assert.Equal(6, result.GetProperty("stepCount").GetInt32());
        Assert.Equal(0, result.GetProperty("errorCount").GetInt32());
        Assert.Equal("intro", result.GetProperty("currentStep").GetString());
        Assert.Equal(_h.GuidePath, _h.Player.SourcePath);
        Assert.StartsWith("Opened by an agent at ", _h.Player.AgentOpenedNote);
        Assert.Equal(1, _h.FrontCalls);
    }

    [WindowsFact]
    public async Task OpenRunbook_DuringARun_IsRefused()
    {
        _h.OpenAtHello();
        var other = _h.Write("other.runbook.md", Harness.Guide.Replace("Agent test guide", "Other"));

        var ex = await Assert.ThrowsAsync<McpException>(() => _h.Tools.OpenRunbook(null!, other));

        Assert.Contains("A run is in progress", ex.Message);
        Assert.Equal(_h.GuidePath, _h.Player.SourcePath);
        Assert.Equal(0, _h.FrontCalls);
    }

    [Fact]
    public async Task OpenRunbook_AfterOnlyOpening_IsNotARunInProgress()
    {
        _h.Player.Open(_h.GuidePath);
        _h.Parameter("Target").Value = "web01";
        var other = _h.Write("other.runbook.md", Harness.Guide.Replace("Agent test guide", "Other"));

        var result = Parse(await _h.Tools.OpenRunbook(null!, other));

        Assert.Equal("Other", result.GetProperty("title").GetString());
    }

    [Fact]
    public async Task OpenRunbook_PathLimits_AreEnforced()
    {
        var text = _h.Write("notes.txt", "hello");
        var big = _h.Write("big.runbook.md", Harness.Guide + new string('x', RunbookTools.MaxGuideBytes));

        Assert.Contains("must be absolute", (await Assert.ThrowsAsync<McpException>(() => _h.Tools.OpenRunbook(null!, "guide.md"))).Message);
        Assert.Contains(".md or .json", (await Assert.ThrowsAsync<McpException>(() => _h.Tools.OpenRunbook(null!, text))).Message);
        Assert.Contains("No such file", (await Assert.ThrowsAsync<McpException>(() => _h.Tools.OpenRunbook(null!, Path.Combine(Path.GetDirectoryName(text)!, "missing.md")))).Message);
        Assert.Contains("the limit is", (await Assert.ThrowsAsync<McpException>(() => _h.Tools.OpenRunbook(null!, big))).Message);
        Assert.Null(_h.Player.SourcePath);
    }

    // ---- get_runbook ----

    [Fact]
    public void GetRunbook_DescribesStepsAndPlaceholders()
    {
        var result = Parse(_h.Tools.GetRunbook(_h.GuidePath));

        Assert.Equal("Agent test guide", result.GetProperty("title").GetString());
        Assert.True(result.GetProperty("runnable").GetBoolean());
        var steps = result.GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.Equal("info", steps["intro"].GetProperty("kind").GetString());
        Assert.False(steps["intro"].TryGetProperty("shell", out _));
        Assert.Equal("script", steps["hello"].GetProperty("kind").GetString());
        Assert.Equal("cmd", steps["hello"].GetProperty("shell").GetString());
        Assert.Contains("{{Target}}", steps["hello"].GetProperty("script").GetString());
        Assert.True(steps["wipe"].GetProperty("destructive").GetBoolean());
        Assert.True(steps["admin"].GetProperty("elevated").GetBoolean());
        Assert.True(steps["tidy"].GetProperty("destructive").GetBoolean());
        Assert.True(steps["tidy"].GetProperty("risks").GetArrayLength() > 0);
        Assert.False(steps["hello"].GetProperty("destructive").GetBoolean());
        Assert.Equal("manual", steps["handover"].GetProperty("kind").GetString());

        var placeholders = result.GetProperty("placeholders").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        Assert.True(placeholders["Target"].GetProperty("required").GetBoolean());
        Assert.Equal("number", placeholders["Port"].GetProperty("type").GetString());
        Assert.Equal("8080", placeholders["Port"].GetProperty("default").GetString());
        Assert.True(placeholders["Password"].GetProperty("secret").GetBoolean());
        Assert.False(placeholders["Password"].TryGetProperty("default", out _));
    }

    [Fact]
    public void GetRunbook_NeverReturnsValuesThePersonEntered()
    {
        _h.Player.Open(_h.GuidePath);
        _h.Parameter("Target").Value = "typed-by-person";
        _h.Parameter("Password").Value = "typed-secret";

        var json = _h.Tools.GetRunbook(_h.GuidePath);

        Assert.DoesNotContain("typed-by-person", json);
        Assert.DoesNotContain("typed-secret", json);
    }

    [Fact]
    public void GetRunbook_PathLimits_AreEnforced()
    {
        Assert.Throws<McpException>(() => _h.Tools.GetRunbook("relative.md"));
        Assert.Throws<McpException>(() => _h.Tools.GetRunbook(_h.Write("a.txt", "x")));
    }

    // ---- validate_runbook ----

    [Fact]
    public void ValidateRunbook_ValidContent()
    {
        var result = Parse(_h.Tools.ValidateRunbook(content: Harness.Guide));

        Assert.True(result.GetProperty("valid").GetBoolean());
        Assert.Equal(0, result.GetProperty("errorCount").GetInt32());
    }

    [Fact]
    public void ValidateRunbook_ContentWithAnError_ReportsItWithALine()
    {
        var broken = Harness.Guide.Replace("{{Port}}", "{{Missing}}");

        var result = Parse(_h.Tools.ValidateRunbook(content: broken));

        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.True(result.GetProperty("errorCount").GetInt32() > 0);
        var error = result.GetProperty("diagnostics").EnumerateArray().First(d => d.GetProperty("severity").GetString() == "error");
        Assert.Contains("Missing", error.GetProperty("message").GetString());
    }

    [Fact]
    public void ValidateRunbook_Path()
    {
        var result = Parse(_h.Tools.ValidateRunbook(path: _h.GuidePath));
        Assert.True(result.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public void ValidateRunbook_BothOrNeither_IsRefused()
    {
        Assert.Contains("exactly one", Assert.Throws<McpException>(() => _h.Tools.ValidateRunbook()).Message);
        Assert.Contains("exactly one", Assert.Throws<McpException>(() => _h.Tools.ValidateRunbook(_h.GuidePath, Harness.Guide)).Message);
    }

    [Fact]
    public void ValidateRunbook_ContentOver1MB_IsRefused()
    {
        var ex = Assert.Throws<McpException>(() => _h.Tools.ValidateRunbook(content: new string('x', RunbookTools.MaxGuideBytes + 1)));
        Assert.Contains("the limit is", ex.Message);
    }

    // ---- list_runbooks ----

    [Fact]
    public async Task ListRunbooks_ListsTheOpenGuideOnce()
    {
        _h.Player.Open(_h.GuidePath);

        var result = Parse(await _h.Tools.ListRunbooks());

        var rows = result.GetProperty("runbooks").EnumerateArray().ToList();
        Assert.Equal(result.GetProperty("count").GetInt32(), rows.Count);
        var open = Assert.Single(rows, r => string.Equals(r.GetProperty("path").GetString(), _h.GuidePath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("open", open.GetProperty("source").GetString());
        Assert.True(open.GetProperty("open").GetBoolean());
        Assert.True(open.GetProperty("runnable").GetBoolean());
        Assert.Equal("Agent test guide", open.GetProperty("title").GetString());
    }

    // ---- helpers ----

    [Fact]
    public void Tail_KeepsTheEndWithinTheLimitStartingAtALine()
    {
        var text = string.Join("\n", Enumerable.Range(0, 1000).Select(i => $"line {i:0000} ✓"));

        var (tail, truncated) = RunbookTools.Tail(text, 1000);

        Assert.True(truncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(tail) <= 1000);
        Assert.StartsWith("line ", tail);
        Assert.EndsWith("line 0999 ✓", tail);
        Assert.Equal(("short", false), RunbookTools.Tail("short", 1000));
    }

    [Fact]
    public void Fields_ListTheArgumentsOfEveryTool()
    {
        Assert.Equal(
            ["fill_placeholders", "get_runbook", "list_runbooks", "open_runbook", "run_step", "validate_runbook"],
            RunbookTools.Fields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["stepId"], RunbookTools.Fields["run_step"]);
        Assert.Equal(["values"], RunbookTools.Fields["fill_placeholders"]);
        Assert.Equal(["content", "path"], RunbookTools.Fields["validate_runbook"].Order(StringComparer.Ordinal));
        Assert.Empty(RunbookTools.Fields["list_runbooks"]);
    }

    [Fact]
    public void AgentName_FallsBackToAnAgent()
        => Assert.Equal("an agent", RunbookTools.AgentName(null));
}
