using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RunbookWizard.App.Services;
using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;
using RunbookWizard.Core.Parsing;

namespace RunbookWizard.App.ViewModels;

public enum Page { Welcome, Overview, Parameters, Step, Logs, Settings }

/// <summary>A finding of the parser shown on the Overview page.</summary>
public sealed record DiagnosticItem(bool IsError, string Where, string Message)
{
    public string SeverityLabel => IsError ? "ERROR" : "WARNING";
}

/// <summary>A guide file offered on the welcome page (recent or bundled example).</summary>
public sealed record GuideLink(string Title, string Path, string Hint);

/// <summary>
/// The player: loads a guide, owns the parameters and the steps, and decides which step is available (steps are done in order;
/// a failed step blocks the ones after it unless it is marked <c>continueOnFailure</c> or skipped).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;

    public MainWindowViewModel(SettingsStore store, IElevationProbe? elevation = null)
    {
        _store = store;
        ElevationProbe = elevation ?? new ProcessElevationProbe();
        _settings = store.Load();
        _theme = ThemeModes.Normalize(_settings.Theme);
        Settings = new SettingsViewModel(_settings, () => _store.Save(_settings));
        Palette = new CommandPaletteViewModel(this);
        Logs = new LogsViewModel();
        LoadWelcomeLists();
    }

    public string Version => AppInfo.Version;

    // ---- elevation (ADR 0013) ----

    /// <summary>Tells whether this process has administrator/root rights. The player never elevates itself (ADR 0004).</summary>
    public IElevationProbe ElevationProbe { get; private set; }

    /// <summary>Set by the application: closes this instance after an elevated copy was started.</summary>
    public Action? ExitApplication { get; set; }

    public bool IsElevated => ElevationProbe.IsElevated;

    public int ElevatedStepCount => Runbook is null ? 0 : ElevationGate.CountElevated(Runbook.Steps);

    /// <summary>The guide has steps that need administrator rights and this process does not have them.</summary>
    public bool ShowElevationBanner => IsRunnable && ElevatedStepCount > 0 && !IsElevated;

    public string ElevationBannerText => ElevationGate.Summary(ElevatedStepCount, Steps.Count)
        + " The player is not running as administrator, so those steps cannot run. Restart it as administrator, or skip them.";

    /// <summary>The "Restart as administrator" button works through the Windows UAC prompt; elsewhere the person starts the player with sudo.</summary>
    public bool CanRelaunchElevated => OperatingSystem.IsWindows() && SourcePath is not null;

    public bool ShowSudoHint => ShowElevationBanner && !OperatingSystem.IsWindows();

    /// <summary>Replaces the probe (tests and the automation channel) and recomputes what depends on it.</summary>
    public void SetElevationProbe(IElevationProbe probe)
    {
        ElevationProbe = probe;
        NotifyElevationChanged();
        RefreshAll();
    }

    private void NotifyElevationChanged()
    {
        OnPropertyChanged(nameof(IsElevated));
        OnPropertyChanged(nameof(ElevatedStepCount));
        OnPropertyChanged(nameof(ShowElevationBanner));
        OnPropertyChanged(nameof(ElevationBannerText));
        OnPropertyChanged(nameof(CanRelaunchElevated));
        OnPropertyChanged(nameof(ShowSudoHint));
    }

    /// <summary>
    /// Starts a second copy of the player through the UAC prompt, with this guide, and closes this one. The command line holds the guide
    /// path and the data folder, never a parameter value. If the prompt is cancelled this instance stays open.
    /// </summary>
    [RelayCommand]
    private void RestartElevated()
    {
        if (!CanRelaunchElevated || SourcePath is null)
            return;
        try
        {
            var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The path of this program is unknown.");
            var info = ElevatedRelaunch.BuildStartInfo(System.Reflection.Assembly.GetEntryAssembly()?.Location, processPath, SourcePath, _store.Directory);
            AppLog.For("app").Information("Restarting as administrator with guide {Path}", SourcePath);
            System.Diagnostics.Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AppLog.For("app").Information("The administrator prompt was cancelled");
            StatusText = "The administrator prompt was cancelled. This window stays as it is.";
            return;
        }
        catch (Exception ex)
        {
            AppLog.For("app").Error(ex, "Restart as administrator failed");
            StatusText = "Could not restart as administrator: " + ex.Message;
            return;
        }
        ExitApplication?.Invoke();
    }

    public string DataDirectory => _store.Directory;

    public CommandPaletteViewModel Palette { get; }

    public LogsViewModel Logs { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>Queues work on the UI thread (step output lines). Tests replace it to run without a dispatcher.</summary>
    public Action<Action> PostToUi { get; set; } = action => Dispatcher.UIThread.Post(action);

    // ---- agent actions (ADR 0015) ----

    /// <summary>The agent's pending request to run a step; shown as the banner above the page. At most one at a time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAgentRequest))]
    private AgentRequestViewModel? _agentRequest;

    public bool HasAgentRequest => AgentRequest is not null;

    /// <summary>"Opened by Claude Code at 14:05" when an agent opened the guide; cleared when the person opens one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAgentOpenedNote))]
    private string _agentOpenedNote = string.Empty;

    public bool HasAgentOpenedNote => AgentOpenedNote.Length > 0;

    /// <summary>
    /// A run is in progress and an agent may not replace the guide: a step runs, a request waits, or the person has settled a step
    /// (done, failed, or skipped by hand) and the guide is not finished.
    /// </summary>
    public bool IsRunInProgress => AgentRequest is not null
        || Steps.Any(s => s.IsRunning)
        || (!IsFinished && Steps.Any(s => s.IsDone || s.IsFailed || (s.IsSkipped && !s.SkippedByCondition && !s.SkippedByBranch)));

    /// <summary>Opens a guide for an agent and marks it as such. The caller has checked <see cref="IsRunInProgress"/>.</summary>
    internal void OpenForAgent(string path, string agent)
    {
        Open(path);
        AgentOpenedNote = $"Opened by {agent} at {DateTime.Now:HH:mm}. Nothing has been run.";
        StatusText = $"{agent} opened {GuideTitle}. Nothing has been run.";
    }

    /// <summary>Shows an agent's request to run <paramref name="step"/>: selects the step and puts the banner up.</summary>
    internal AgentRequestViewModel RequestAgentRun(string agent, StepViewModel step, TimeSpan timeout)
    {
        var request = new AgentRequestViewModel(agent, step, timeout, closed =>
        {
            if (ReferenceEquals(AgentRequest, closed))
                AgentRequest = null;
        });
        SelectedStep = step;
        AgentRequest = request;
        StatusText = $"{agent} asks to run step {step.Number}. Run or decline it in the banner.";
        return request;
    }

    /// <summary>Set by the view: shows the file picker and returns the chosen guide path (null when cancelled).</summary>
    public Func<Task<string?>>? PickGuide { get; set; }

    public Runbook? Runbook { get; private set; }

    public VariableStore? Variables { get; private set; }

    public string? SourcePath { get; private set; }

    public ObservableCollection<StepViewModel> Steps { get; } = new();

    public ObservableCollection<VariableViewModel> Parameters { get; } = new();

    public ObservableCollection<DiagnosticItem> Diagnostics { get; } = new();

    public ObservableCollection<GuideLink> RecentGuides { get; } = new();

    public ObservableCollection<GuideLink> ExampleGuides { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome), nameof(ShowOverview), nameof(ShowParameters), nameof(ShowStep), nameof(ShowLogs), nameof(ShowSettings), nameof(OverviewSelected), nameof(ParametersSelected))]
    private Page _page = Page.Welcome;

    private Page _pageBeforeLogs = Page.Welcome;

    [ObservableProperty]
    private StepViewModel? _selectedStep;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeIcon), nameof(ThemeTip))]
    private string _theme;

    [ObservableProperty]
    private string _statusText = "Open a guide to begin.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGuide), nameof(IsRunnable))]
    private string _guideTitle = string.Empty;

    [ObservableProperty]
    private string? _guideDescription;

    [ObservableProperty]
    private string _guideMeta = string.Empty;

    [ObservableProperty]
    private int _doneCount;

    [ObservableProperty]
    private bool _isFinished;

    [ObservableProperty]
    private string _finishedText = string.Empty;

    public bool HasGuide => Runbook is not null;

    public bool IsRunnable { get; private set; }

    public bool HasParameters => Parameters.Count > 0;

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    public bool ShowWelcome => Page == Page.Welcome;

    public bool ShowOverview => Page == Page.Overview;

    public bool ShowParameters => Page == Page.Parameters;

    public bool ShowStep => Page == Page.Step;

    public bool ShowLogs => Page == Page.Logs;

    public bool ShowSettings => Page == Page.Settings;

    public bool OverviewSelected => Page == Page.Overview;

    public bool ParametersSelected => Page == Page.Parameters;

    public int TotalSteps => Steps.Count;

    public double ProgressPercent => Steps.Count == 0 ? 0 : 100.0 * DoneCount / Steps.Count;

    public string ProgressText => Steps.Count == 0 ? string.Empty : $"{DoneCount} of {Steps.Count} steps done";

    public string ThemeIcon => ThemeModes.Icon(Theme);

    public string ThemeTip => $"Theme: {Theme} (click to change, Ctrl+Shift+L)";

    public string ShellName(Step step)
    {
        var kind = Runbook?.ShellOf(step) ?? ShellKind.Powershell;
        return ShellCatalog.Resolve(kind)?.DisplayName ?? kind.ToString().ToLowerInvariant() + " (not installed)";
    }

    partial void OnSelectedStepChanged(StepViewModel? value)
    {
        if (value is not null)
            Page = Page.Step;
    }

    partial void OnThemeChanged(string value)
    {
        ThemeModes.Apply(value);
        _settings.Theme = value;
        _store.Save(_settings);
    }

    // ---- opening a guide ----

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (PickGuide is null)
            return;
        if (await PickGuide() is { } path)
            Open(path);
    }

    [RelayCommand]
    private void OpenGuide(GuideLink? link)
    {
        if (link is not null)
            Open(link.Path);
    }

    [RelayCommand]
    private void Reload()
    {
        if (SourcePath is not null)
            Open(SourcePath);
    }

    /// <summary>Loads and validates a guide. Nothing in it is executed; a guide with errors can be read but not run.</summary>
    public void Open(string path)
    {
        ParseResult result;
        try
        {
            result = RunbookParser.ParseFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.For("guide").Warning(ex, "Guide {Path} could not be read", path);
            StatusText = "Cannot read " + path + ": " + ex.Message;
            return;
        }

        AgentRequest?.Cancel();
        AgentOpenedNote = string.Empty;
        foreach (var step in Steps)
            step.Abort();
        Steps.Clear();
        Parameters.Clear();
        Diagnostics.Clear();
        SelectedStep = null;
        IsFinished = false;
        DoneCount = 0;

        SourcePath = Path.GetFullPath(path);
        Runbook = result.Runbook;
        IsRunnable = result.IsRunnable;
        foreach (var d in result.Diagnostics)
            Diagnostics.Add(new DiagnosticItem(d.IsError, d.Path, d.Message + (d.Line is { } line ? $" (line {line})" : string.Empty)));

        if (Runbook is null)
        {
            Variables = null;
            GuideTitle = Path.GetFileName(path);
            GuideDescription = "This file could not be read as a guide.";
            GuideMeta = string.Empty;
        }
        else
        {
            Variables = new VariableStore(Runbook, Path.GetDirectoryName(SourcePath));
            GuideTitle = Runbook.Title;
            GuideDescription = Runbook.Description;
            GuideMeta = BuildMeta(Runbook);
            foreach (var variable in Runbook.Variables)
                Parameters.Add(new VariableViewModel(variable, Variables, OnParametersChanged));
            if (IsRunnable)
            {
                for (var i = 0; i < Runbook.Steps.Count; i++)
                    Steps.Add(new StepViewModel(this, Runbook.Steps[i], i + 1));
            }
        }

        OnPropertyChanged(nameof(HasGuide));
        OnPropertyChanged(nameof(IsRunnable));
        OnPropertyChanged(nameof(HasParameters));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(TotalSteps));
        NotifyElevationChanged();
        RefreshAll();

        Page = Page.Overview;
        StatusText = IsRunnable
            ? $"Opened {GuideTitle}. Nothing has been run."
            : $"{GuideTitle} has {Diagnostics.Count(d => d.IsError)} error(s) and cannot be run.";
        AppLog.For("guide").Information("Opened {Path}: {Title}, {Steps} steps, {Errors} errors, {Warnings} warnings",
            SourcePath, GuideTitle, Steps.Count, Diagnostics.Count(d => d.IsError), Diagnostics.Count(d => !d.IsError));
        Remember(SourcePath);
    }

    private static string BuildMeta(Runbook runbook)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(runbook.Version))
            parts.Add("v" + runbook.Version);
        if (!string.IsNullOrWhiteSpace(runbook.Author))
            parts.Add("by " + runbook.Author);
        if (runbook.Platforms.Count > 0)
            parts.Add(string.Join(", ", runbook.Platforms.Select(p => p.ToString().ToLowerInvariant())));
        return string.Join(" · ", parts);
    }

    // ---- navigation ----

    [RelayCommand]
    private void ShowOverviewPage()
    {
        if (Runbook is null)
            return;
        SelectedStep = null;
        Page = Page.Overview;
    }

    [RelayCommand]
    private void ShowParametersPage()
    {
        if (Runbook is null)
            return;
        SelectedStep = null;
        Page = Page.Parameters;
    }

    [RelayCommand]
    private void ToggleLogs()
    {
        if (Page == Page.Logs)
        {
            Page = _pageBeforeLogs;
            return;
        }
        _pageBeforeLogs = Page;
        Page = Page.Logs;
        Logs.ReloadFilesCommand.Execute(null);
    }

    /// <summary>Opens the Settings page; again returns to the page before it.</summary>
    [RelayCommand]
    private void ToggleSettings()
    {
        if (Page == Page.Settings)
        {
            Page = _pageBeforeLogs;
            return;
        }
        if (Page != Page.Logs)
            _pageBeforeLogs = Page;
        Page = Page.Settings;
    }

    /// <summary>Opens the first step that still needs attention (or the Parameters page when something required is empty).</summary>
    [RelayCommand]
    private void Start()
    {
        if (!IsRunnable)
            return;
        if (Parameters.Any(p => p.HasError))
        {
            ShowParametersPage();
            return;
        }
        SelectedStep = (Flow?.Current is { } current && current < Steps.Count ? Steps[current] : null)
            ?? Steps.FirstOrDefault(s => s.IsFailed) ?? Steps.FirstOrDefault();
    }

    public void SelectStep(string idOrNumber)
    {
        var step = Steps.FirstOrDefault(s => string.Equals(s.Id, idOrNumber, StringComparison.OrdinalIgnoreCase))
            ?? (int.TryParse(idOrNumber, out var n) ? Steps.FirstOrDefault(s => s.Index == n) : null)
            ?? throw new KeyNotFoundException($"No step '{idOrNumber}'");
        SelectedStep = step;
    }

    public void GoTo(Page page)
    {
        switch (page)
        {
            case Page.Overview: ShowOverviewPage(); break;
            case Page.Parameters: ShowParametersPage(); break;
            case Page.Logs: if (Page != Page.Logs) ToggleLogs(); break;
            case Page.Settings: if (Page != Page.Settings) ToggleSettings(); break;
            default: break;
        }
    }

    // ---- theme ----

    [RelayCommand]
    private void CycleTheme() => Theme = ThemeModes.Next(Theme);

    [RelayCommand]
    private void SetTheme(string mode) => Theme = ThemeModes.Normalize(mode);

    // ---- state of the steps ----

    private void OnParametersChanged() => RefreshAll();

    /// <summary>Called by a step whenever its status changed; recomputes availability and may move to the next step.</summary>
    public void StepSettled(StepViewModel step, bool advance)
    {
        RefreshAll();
        if (!advance || !_settings.AutoAdvance || !ReferenceEquals(SelectedStep, step))
            return;

        GoToNext(step);
    }

    /// <summary>The path through the guide as of the last refresh.</summary>
    public FlowResult? Flow { get; private set; }

    /// <summary>"05 Install" for a step id (for texts that say where a route leads).</summary>
    public string Describe(string id)
    {
        var step = Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        return step is null ? id : $"{step.Number} {step.Title}";
    }

    /// <summary>Selects the step the path leads to next (the first one that needs attention); the overview when the path is complete.</summary>
    public void GoToNext(StepViewModel step)
    {
        RefreshAll();
        if (Flow?.Current is { } current && current < Steps.Count)
        {
            SelectedStep = Steps[current];
            return;
        }
        if (Flow is { IsComplete: true })
        {
            SelectedStep = null;
            Page = Page.Overview;
        }
    }

    private FlowResult Evaluate() => FlowPlanner.Evaluate(Runbook!, Steps.Select(s => s.Flow).ToList());

    private void RefreshAll()
    {
        if (Variables is null)
            return;

        foreach (var parameter in Parameters)
            parameter.Refresh();

        // Conditions can depend on a variable another step has just captured, and a skipped step changes the path, which
        // changes which steps lie on a path not taken: settle until nothing changes.
        var flow = Evaluate();
        for (var pass = 0; pass < 6; pass++)
        {
            var changed = 0;
            for (var i = 0; i < Steps.Count; i++)
                changed += Steps[i].ApplyAutoSkip(!flow.Reachable.Contains(i)) ? 1 : 0;
            if (changed == 0)
                break;
            flow = Evaluate();
        }
        Flow = flow;

        // Only the steps up to the one that needs attention are available; later ones wait (also those finished earlier).
        var walked = flow.Walked.ToHashSet();
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsBlocked = !walked.Contains(i);
            Steps[i].RefreshPlan();
        }

        DoneCount = Steps.Count(s => s.IsDone || s.IsSkipped);
        IsFinished = Steps.Count > 0 && flow.IsComplete;
        FinishedText = Steps.Any(s => s.IsFailed)
            ? "The guide reached its end. A step failed on the way and the route for that case was followed."
            : "Every step is done or skipped. The guide is finished.";
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        if (Runbook is not null && IsRunnable)
            StatusText = IsFinished ? "All steps are done." : ProgressText;
    }

    // ---- recent guides, examples ----

    private void Remember(string path)
    {
        _settings.Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _settings.Recent.Insert(0, path);
        if (_settings.Recent.Count > 8)
            _settings.Recent.RemoveRange(8, _settings.Recent.Count - 8);
        _store.Save(_settings);
        LoadWelcomeLists();
    }

    private void LoadWelcomeLists()
    {
        RecentGuides.Clear();
        foreach (var path in _settings.Recent.Where(File.Exists))
            RecentGuides.Add(new GuideLink(TitleOf(path), path, path));

        ExampleGuides.Clear();
        var examples = Path.Combine(AppContext.BaseDirectory, "examples");
        if (Directory.Exists(examples))
        {
            foreach (var path in Directory.GetFiles(examples, "*.runbook.*").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                ExampleGuides.Add(new GuideLink(TitleOf(path), path, Path.GetFileName(path)));
        }
        OnPropertyChanged(nameof(HasRecent));
    }

    private static string TitleOf(string path)
    {
        try
        {
            return RunbookParser.ParseFile(path).Runbook?.Title is { Length: > 0 } title ? title : Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetFileName(path);
        }
    }

    public bool HasRecent => RecentGuides.Count > 0;

    public void Shutdown()
    {
        AgentRequest?.Cancel();
        foreach (var step in Steps)
            step.Abort();
    }
}
