using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RunbookWizard.App.Services;
using RunbookWizard.App.ViewModels;
using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;
using RunbookWizard.Core.Parsing;

namespace RunbookWizard.App.Mcp;

/// <summary>
/// The MCP tools agents call (ADR 0015). The read tools go through the Core parser and change nothing. The action tools act on the
/// player in the app on its UI thread, and the player marks what an agent did. <c>run_step</c> runs nothing until the person presses
/// Run in the app. The log records the tool, the step id and the outcome, never the values of the arguments.
/// </summary>
[McpServerToolType]
public sealed class RunbookTools(RunbookContext context)
{
    /// <summary>A guide file and inline content are limited to this many bytes.</summary>
    public const int MaxGuideBytes = 1024 * 1024;

    public const int MaxValues = 100;

    public const int MaxValueBytes = 4 * 1024;

    /// <summary>run_step returns at most this many bytes of the (masked) output, the end of it.</summary>
    public const int MaxOutputBytes = 16 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static Serilog.ILogger Log => AppLog.For("mcp");

    private MainWindowViewModel Player => context.Player;

    /// <summary>The argument names each tool accepts; anything else in a call is rejected.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Fields { get; } = typeof(RunbookTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Select(m => (Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>()))
        .Where(x => x.Tool?.Name is not null)
        .ToDictionary(
            x => x.Tool!.Name!,
            x => (IReadOnlySet<string>)x.Method.GetParameters()
                .Where(p => p.ParameterType != typeof(CancellationToken) && p.ParameterType != typeof(McpServer))
                .Select(p => p.Name!).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

    // ---- read tools ----

    [McpServerTool(Name = "list_runbooks", ReadOnly = true), Description(
        "Lists the guides RunbookWizard knows: the one open in the player (source 'open'), the recent ones ('recent') and the bundled " +
        "examples ('example'). Each entry has path, title, source, open, parses (the file reads as a guide) and runnable (no errors).")]
    public async Task<string> ListRunbooks()
    {
        var known = await context.Ui.InvokeAsync(() =>
        {
            var list = new List<(string Path, string Source)>();
            if (Player.SourcePath is { } open)
                list.Add((open, "open"));
            list.AddRange(Player.RecentGuides.Select(g => (g.Path, "recent")));
            list.AddRange(Player.ExampleGuides.Select(g => (g.Path, "example")));
            return list;
        }).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<object>();
        foreach (var (path, source) in known)
        {
            if (!seen.Add(Path.GetFullPath(path)))
                continue;
            var result = TryParseFile(path);
            rows.Add(new
            {
                path,
                title = result?.Runbook?.Title is { Length: > 0 } title ? title : Path.GetFileName(path),
                source,
                open = source == "open",
                parses = result?.Runbook is not null,
                runnable = result?.IsRunnable ?? false,
            });
        }

        Log.Information("list_runbooks: {Count} guides", rows.Count);
        return Serialize(new { count = rows.Count, runbooks = rows });
    }

    [McpServerTool(Name = "get_runbook", ReadOnly = true), Description(
        "Reads a guide the way the player sees it, without opening it. 'path' is an absolute path of a .md or .json guide up to 1 MB. " +
        "Returns title, description, parses, runnable, the steps (id, title, kind, shell, elevated, destructive (declared or flagged by " +
        "the risk scanner), risks, when, script as written with {{placeholders}} not substituted) and the placeholders (name, type, " +
        "default, required, secret, description). Values the person entered are never returned.")]
    public string GetRunbook([Description("Absolute path of a .md or .json guide.")] string path)
    {
        var full = RequireGuideFile("get_runbook", path);
        var result = RunbookParser.ParseFile(full);
        var runbook = result.Runbook;
        Log.Information("get_runbook: {Outcome}", runbook is null ? "does not parse" : result.IsRunnable ? "runnable" : "has errors");
        return Serialize(new
        {
            path = full,
            title = runbook?.Title,
            description = runbook?.Description,
            parses = runbook is not null,
            runnable = result.IsRunnable,
            steps = runbook?.Steps.Select(s =>
            {
                var risks = s.EffectiveType == StepType.Script ? RiskScanner.Scan(s.Script) : Array.Empty<string>();
                return new
                {
                    id = s.Id,
                    title = s.Title,
                    kind = s.EffectiveType.ToString().ToLowerInvariant(),
                    shell = s.EffectiveType == StepType.Script ? runbook.ShellOf(s).ToString().ToLowerInvariant() : null,
                    elevated = s.Elevated,
                    destructive = s.Destructive || risks.Count > 0,
                    risks = risks.Count > 0 ? risks : null,
                    when = s.When,
                    script = s.Script,
                };
            }).ToList(),
            placeholders = runbook?.Variables.Select(v => new
            {
                name = v.Name,
                type = v.Type.ToString().ToLowerInvariant(),
                @default = v.Type == VariableType.Secret ? null : v.Default,
                required = v.Required,
                secret = v.Type == VariableType.Secret,
                description = v.Description,
            }).ToList(),
            diagnostics = Describe(result.Diagnostics),
        });
    }

    [McpServerTool(Name = "validate_runbook", ReadOnly = true), Description(
        "Validates a guide with the player's parser and checks: give 'path' (absolute, .md or .json, up to 1 MB) or 'content' (the " +
        "guide text, up to 1 MB) to check a draft before writing it. Returns valid (it can be run), parses, errorCount, warningCount " +
        "and diagnostics as { severity, line, message, path }.")]
    public string ValidateRunbook(
        [Description("Absolute path of a .md or .json guide. Exclusive with content.")] string? path = null,
        [Description("The guide text (Markdown with front matter, or JSON), up to 1 MB. Exclusive with path.")] string? content = null)
    {
        var hasPath = !string.IsNullOrWhiteSpace(path);
        var hasContent = content is not null;
        if (hasPath == hasContent)
            throw Refuse("validate_runbook", "Give exactly one of 'path' and 'content'.");

        ParseResult result;
        if (hasPath)
        {
            result = RunbookParser.ParseFile(RequireGuideFile("validate_runbook", path!));
        }
        else
        {
            var bytes = Encoding.UTF8.GetByteCount(content!);
            if (bytes > MaxGuideBytes)
                throw Refuse("validate_runbook", $"'content' is {bytes:N0} bytes; the limit is {MaxGuideBytes:N0} (1 MB).");
            result = RunbookParser.Parse(content!);
        }

        var errors = result.Diagnostics.Count(d => d.IsError);
        Log.Information("validate_runbook: {Errors} errors, {Warnings} warnings", errors, result.Diagnostics.Count - errors);
        return Serialize(new
        {
            valid = result.IsRunnable,
            parses = result.Runbook is not null,
            errorCount = errors,
            warningCount = result.Diagnostics.Count - errors,
            diagnostics = Describe(result.Diagnostics),
        });
    }

    // ---- action tools ----

    [McpServerTool(Name = "open_runbook"), Description(
        "Opens a guide in the player, like File > Open, and brings the window forward; the app shows that an agent opened it. 'path' " +
        "is an absolute path of a .md or .json guide up to 1 MB. Refused while a run is in progress (a step runs or waits for " +
        "confirmation, or steps were done and the guide is not finished): the person finishes it or reopens the guide first. Returns " +
        "path, title, runnable, stepCount, errorCount and currentStep (the step the player offers).")]
    public async Task<string> OpenRunbook(McpServer server, [Description("Absolute path of a .md or .json guide.")] string path)
    {
        var full = RequireGuideFile("open_runbook", path);
        var agent = AgentName(server);
        var opened = await context.Ui.InvokeAsync(() =>
        {
            if (Player.IsRunInProgress)
                throw Refuse("open_runbook", "A run is in progress in the player. The person has to finish it, or close it by reopening or " +
                    "reloading the guide in the app, before an agent can open another guide.");
            Player.OpenForAgent(full, agent);
            if (!string.Equals(Player.SourcePath, full, StringComparison.OrdinalIgnoreCase))
                throw Refuse("open_runbook", Player.StatusText);
            return new
            {
                path = full,
                title = Player.GuideTitle,
                runnable = Player.IsRunnable,
                stepCount = Player.Steps.Count,
                errorCount = Player.Diagnostics.Count(d => d.IsError),
                currentStep = CurrentStep()?.Id,
            };
        }).ConfigureAwait(false);
        context.BringToFront();
        Log.Information("open_runbook: opened for {Agent}, runnable {Runnable}", agent, opened.runnable);
        return Serialize(opened);
    }

    [McpServerTool(Name = "fill_placeholders"), Description(
        "Sets values of placeholders of the guide open in the player, as { name: value }. The person sees them filled in (marked as " +
        "filled by an agent) and can change them. At most 100 entries, each value up to 4 KB. A 'secret' placeholder is refused (the " +
        "person types secrets), and so is an unknown name; nothing is set then. Returns filled (names) and problems ({ name, message } " +
        "for values the guide does not accept).")]
    public async Task<string> FillPlaceholders(
        McpServer server,
        [Description("Placeholder names and their values, e.g. { \"site\": \"Default Web Site\", \"port\": \"8080\" }.")] Dictionary<string, JsonElement> values)
    {
        if (values is null || values.Count == 0)
            throw Refuse("fill_placeholders", "'values' is empty.");
        if (values.Count > MaxValues)
            throw Refuse("fill_placeholders", $"'values' has {values.Count} entries; the limit is {MaxValues}.");
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, element) in values)
        {
            var text = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString()!,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
                _ => throw Refuse("fill_placeholders", $"The value of '{name}' must be a string, number or boolean."),
            };
            if (Encoding.UTF8.GetByteCount(text) > MaxValueBytes)
                throw Refuse("fill_placeholders", $"The value of '{name}' is longer than {MaxValueBytes:N0} bytes (4 KB).");
            texts[name] = text;
        }

        var agent = AgentName(server);
        var filled = await context.Ui.InvokeAsync(() =>
        {
            if (Player.Runbook is null)
                throw Refuse("fill_placeholders", "No guide is open. Use open_runbook first.");
            if (Player.AgentRequest is not null)
                throw Refuse("fill_placeholders", "A run_step request is waiting for the person; the values cannot change while they confirm the command.");
            if (Player.Steps.Any(s => s.IsRunning))
                throw Refuse("fill_placeholders", "A step is running; wait until it ends.");

            var byName = Player.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
            var unknown = texts.Keys.Where(n => !byName.ContainsKey(n)).ToList();
            var secret = texts.Keys.Where(n => byName.TryGetValue(n, out var p) && p.IsSecret).ToList();
            if (unknown.Count > 0 || secret.Count > 0)
            {
                var problems = new List<string>();
                if (unknown.Count > 0)
                    problems.Add($"Unknown placeholder(s): {string.Join(", ", unknown)}. The guide has: {(byName.Count == 0 ? "none" : string.Join(", ", byName.Keys))}.");
                if (secret.Count > 0)
                    problems.Add($"Secret placeholder(s) refused: {string.Join(", ", secret)}. The person types secrets in the app.");
                throw Refuse("fill_placeholders", string.Join(" ", problems) + " Nothing was set.");
            }

            foreach (var (name, text) in texts)
                byName[name].FillByAgent(text, agent);
            Player.StatusText = $"{agent} filled in {texts.Count} parameter(s). Check them on the Parameters page.";
            return new
            {
                filled = texts.Keys.ToList(),
                problems = texts.Keys.Select(n => byName[n]).Where(p => p.HasError).Select(p => new { name = p.Name, message = p.Error }).ToList(),
            };
        }).ConfigureAwait(false);
        Log.Information("fill_placeholders: {Count} filled for {Agent}, {Problems} not accepted", filled.filled.Count, agent, filled.problems.Count);
        return Serialize(filled);
    }

    [McpServerTool(Name = "run_step"), Description(
        "Asks the person to run one script step of the open guide. Only the step the player currently offers can be run. The app shows " +
        "your request with the exact command (secrets masked) and its warnings, and the call waits up to 120 s for the person's Run or " +
        "Decline; no answer counts as declined. An info, manual or choice step is completed by the person in the app; an elevated step " +
        "in a player that is not running as administrator is refused. Only one request waits at a time. Returns outcome (succeeded, " +
        "failed or declined), reason, exitCode, durationSeconds, output (the last 16 KB, secrets masked), outputTruncated, currentStep " +
        "and finished.")]
    public async Task<string> RunStep(McpServer server, [Description("Id of the step, e.g. 'install'.")] string stepId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stepId))
            throw Refuse("run_step", "'stepId' is empty.");
        var agent = AgentName(server);
        var request = await context.Ui.InvokeAsync(() => PrepareRun(stepId, agent)).ConfigureAwait(false);
        var step = request.Step;
        context.BringToFront();
        Log.Information("run_step {Step}: waiting for the person's answer", step.Id);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = Task.Delay(context.ConfirmTimeout, wait.Token);
        var first = await Task.WhenAny(request.Answer, timeout).ConfigureAwait(false);
        await wait.CancelAsync().ConfigureAwait(false);
        var timedOut = first != request.Answer && !cancellationToken.IsCancellationRequested;
        if (first != request.Answer)
            await context.Ui.InvokeAsync(() => { request.Cancel(); return true; }).ConfigureAwait(false);
        var run = await request.Answer.ConfigureAwait(false); // the person may have pressed Run in the same instant

        if (!run)
        {
            var reason = timedOut
                ? $"No answer within {context.ConfirmTimeout.TotalSeconds:0} s; that counts as declined. Nothing ran."
                : cancellationToken.IsCancellationRequested ? "The request was withdrawn. Nothing ran." : "The person declined. Nothing ran.";
            await context.Ui.InvokeAsync(() =>
            {
                Player.StatusText = $"Step {step.Number} was not run for {agent}: {(timedOut ? "no answer in time" : "declined")}.";
                return true;
            }).ConfigureAwait(false);
            Log.Information("run_step {Step}: {Outcome}", step.Id, timedOut ? "timed out (declined)" : "declined");
            cancellationToken.ThrowIfCancellationRequested();
            return Serialize(new { outcome = "declined", stepId = step.Id, reason });
        }

        AgentRunOutcome outcome;
        try
        {
            outcome = await context.Ui.InvokeAsync(() => step.RunForAgentAsync(agent)).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw Refuse("run_step", ex.Message, step.Id);
        }

        var after = await context.Ui.InvokeAsync(() => new { current = CurrentStep()?.Id, finished = Player.IsFinished }).ConfigureAwait(false);
        var (output, truncated) = Tail(outcome.MaskedOutput, MaxOutputBytes);
        Log.Information("run_step {Step}: {Outcome}, exit {ExitCode}", step.Id, outcome.Success ? "succeeded" : "failed", outcome.ExitCode);
        return Serialize(new
        {
            outcome = outcome.Success ? "succeeded" : "failed",
            stepId = step.Id,
            reason = outcome.Reason,
            exitCode = outcome.ExitCode,
            durationSeconds = Math.Round(outcome.Duration.TotalSeconds, 1),
            output,
            outputTruncated = truncated,
            currentStep = after.current,
            finished = after.finished,
        });
    }

    /// <summary>On the UI thread: every check that refuses run_step before the person is asked, then the banner.</summary>
    private AgentRequestViewModel PrepareRun(string stepId, string agent)
    {
        if (Player.Runbook is null)
            throw Refuse("run_step", "No guide is open. Use open_runbook first.", stepId);
        if (!Player.IsRunnable)
            throw Refuse("run_step", "The open guide has errors and cannot be run. Check it with validate_runbook.", stepId);
        if (Player.AgentRequest is not null)
            throw Refuse("run_step", "Another run_step request is already waiting for the person's answer; only one can wait at a time.", stepId);

        var step = Player.Steps.FirstOrDefault(s => string.Equals(s.Id, stepId, StringComparison.Ordinal))
            ?? Player.Steps.FirstOrDefault(s => string.Equals(s.Id, stepId, StringComparison.OrdinalIgnoreCase))
            ?? throw Refuse("run_step", $"No step '{stepId}' in the open guide. Its steps are: {string.Join(", ", Player.Steps.Select(s => s.Id))}.", stepId);
        if (!step.IsScript)
            throw Refuse("run_step", $"Step '{step.Id}' is {(step.IsInfo ? "an" : "a")} {step.Type.ToString().ToLowerInvariant()} step. An agent does not run it: the person has to complete it in the app.", step.Id);

        var current = CurrentStep();
        if (!ReferenceEquals(current, step))
        {
            throw Refuse("run_step", current is null
                ? $"Step '{step.Id}' is not the current step: no step is waiting, the guide is finished."
                : $"Step '{step.Id}' is not the current step. The player offers '{current.Id}' ({current.Number} {current.Title}) now.", step.Id);
        }
        if (step.IsRunning)
            throw Refuse("run_step", $"Step '{step.Id}' is already running.", step.Id);
        if (step.ElevationBlocked)
            throw Refuse("run_step", ElevationGate.BlockedReason, step.Id);
        step.RefreshPlan();
        if (step.HasProblems)
            throw Refuse("run_step", $"Step '{step.Id}' cannot run yet: {string.Join(" ", step.Problems)} Fill the placeholders (fill_placeholders) or ask the person.", step.Id);

        return Player.RequestAgentRun(agent, step, context.ConfirmTimeout);
    }

    /// <summary>The step the player offers now (the first one that needs attention); null when none.</summary>
    private StepViewModel? CurrentStep()
        => Player.Flow?.Current is { } index && index >= 0 && index < Player.Steps.Count ? Player.Steps[index] : null;

    // ---- helpers ----

    /// <summary>The client's name from its MCP client info (per request or from initialize); "an agent" when it sent none.</summary>
    internal static string AgentName(McpServer? server)
    {
        var info = server?.ClientInfo;
        var name = !string.IsNullOrWhiteSpace(info?.Title) ? info!.Title : info?.Name;
        if (string.IsNullOrWhiteSpace(name))
            return "an agent";
        name = name.Trim();
        return name.Length > 60 ? name[..60] : name;
    }

    private static string RequireGuideFile(string tool, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw Refuse(tool, "'path' is empty.");
        if (!Path.IsPathFullyQualified(path))
            throw Refuse(tool, "'path' must be absolute.");
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Refuse(tool, $"'path' is not a valid path: {ex.Message}");
        }

        var extension = Path.GetExtension(full);
        if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw Refuse(tool, "'path' must be a guide file ending in .md or .json.");
        var file = new FileInfo(full);
        if (!file.Exists)
            throw Refuse(tool, $"No such file: {full}");
        if (file.Length > MaxGuideBytes)
            throw Refuse(tool, $"The file is {file.Length:N0} bytes; the limit is {MaxGuideBytes:N0} (1 MB).");
        return full;
    }

    private static ParseResult? TryParseFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length <= MaxGuideBytes ? RunbookParser.ParseFile(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static List<object> Describe(IReadOnlyList<Diagnostic> diagnostics)
        => diagnostics.Select(d => (object)new
        {
            severity = d.Severity.ToString().ToLowerInvariant(),
            line = d.Line,
            message = d.Message,
            path = string.IsNullOrEmpty(d.Path) ? null : d.Path,
        }).ToList();

    /// <summary>The end of <paramref name="text"/> that fits in <paramref name="maxBytes"/> UTF-8 bytes, cut at a line start when possible.</summary>
    internal static (string Text, bool Truncated) Tail(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return (text, false);
        var start = text.Length;
        var bytes = 0;
        while (start > 0)
        {
            var size = char.IsLowSurrogate(text[start - 1]) && start > 1 ? 2 : 1;
            var count = Encoding.UTF8.GetByteCount(text.AsSpan(start - size, size));
            if (bytes + count > maxBytes)
                break;
            bytes += count;
            start -= size;
        }
        var tail = text[start..];
        var newline = tail.IndexOf('\n');
        if (newline >= 0 && newline < tail.Length - 1)
            tail = tail[(newline + 1)..];
        return (tail, true);
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    private static McpException Refuse(string tool, string problem, string? stepId = null)
    {
        if (stepId is null)
            Log.Information("{Tool}: refused: {Reason}", tool, problem);
        else
            Log.Information("{Tool} {Step}: refused: {Reason}", tool, stepId, problem);
        return new McpException(problem);
    }

    /// <summary>
    /// Rejects a call with an argument the tool does not have (ADR 0015): a misspelled 'stepid' or 'value' must not be silently
    /// ignored, because the agent would believe it took effect. The answer names the accepted fields.
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> RejectUnknownFields => next => async (filterContext, ct) =>
    {
        var request = filterContext.Params;
        if (request?.Name is { } tool && Fields.TryGetValue(tool, out var accepted) && request.Arguments is { Count: > 0 } arguments)
        {
            var unknown = arguments.Keys.Where(k => !accepted.Contains(k)).ToList();
            if (unknown.Count > 0)
            {
                Log.Information("{Tool}: refused unknown field(s) {Fields}", tool, string.Join(", ", unknown));
                var fields = accepted.Count == 0 ? "none" : string.Join(", ", accepted.Order(StringComparer.Ordinal));
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = $"Unknown field(s) for {tool}: {string.Join(", ", unknown)}. Accepted fields: {fields}." }],
                };
            }
        }

        return await next(filterContext, ct).ConfigureAwait(false);
    };
}
