using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RunbookWizard.App.Services;
using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;

namespace RunbookWizard.App.ViewModels;

public enum StepStatus { Pending, Running, Done, Failed, Skipped }

/// <summary>
/// One step of the guide in the player: its text, the exact command that would run (parameters substituted, secrets masked),
/// its status and the live output. Nothing runs except through <see cref="RunCommand"/> (one click, or two for destructive steps).
/// </summary>
public sealed partial class StepViewModel : ObservableObject
{
    private const int MaxOutputLines = 5000;

    private readonly MainWindowViewModel _owner;
    private CancellationTokenSource? _cts;
    private StepPlan? _plan;
    private string? _chosenTarget;

    public StepViewModel(MainWindowViewModel owner, Step model, int index)
    {
        _owner = owner;
        Model = model;
        Index = index;
        InlineParameters = owner.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        Options = new ObservableCollection<OptionViewModel>(model.Options.Select(o => new OptionViewModel(this, o)));
        var parsed = model.EffectiveType == StepType.Manual ? Checklist.Parse(model.Description) : null;
        _description = parsed is { HasItems: true } ? parsed.Text : model.Description;
        ChecklistItems = new ObservableCollection<ChecklistItemViewModel>((parsed?.Items ?? Array.Empty<ChecklistItem>()).Select(i => new ChecklistItemViewModel(this, i)));
    }

    private readonly string? _description;

    /// <summary>The checkboxes of a manual step (ADR 0010); empty for every other step.</summary>
    public ObservableCollection<ChecklistItemViewModel> ChecklistItems { get; }

    public bool HasChecklist => ChecklistItems.Count > 0;

    /// <summary>The indexes of the ticked items. This is the whole checklist state of the step: what a run-state store would persist, with <see cref="ChecklistFingerprint"/>.</summary>
    public IReadOnlySet<int> TickedItems => ChecklistItems.Where(i => i.IsChecked).Select(i => i.Index).ToHashSet();

    public string ChecklistFingerprint => Checklist.Fingerprint(ChecklistItems.Select(i => i.Model).ToList());

    /// <summary>True when every required item is ticked (or there is no checklist).</summary>
    public bool ChecklistComplete => Checklist.IsComplete(ChecklistItems.Select(i => i.Model).ToList(), TickedItems);

    /// <summary>"2 of 3 required items ticked"; empty when nothing is required.</summary>
    public string ChecklistProgress => Checklist.Progress(ChecklistItems.Select(i => i.Model).ToList(), TickedItems);

    /// <summary>The boxes can be changed while the step is open: not blocked, not settled, not skipped.</summary>
    public bool CanTick => HasChecklist && !IsBlocked && !IsDone && !IsSkipped;

    internal void ChecklistChanged(ChecklistItemViewModel item)
    {
        OnPropertyChanged(nameof(ChecklistComplete));
        OnPropertyChanged(nameof(ChecklistProgress));
        OnPropertyChanged(nameof(CanFinish));
        OnPropertyChanged(nameof(WaitsForChecklist));
        AppLog.For("run").Debug("Step {Step} checklist item {Index} {State}", Id, item.Index, item.IsChecked ? "ticked" : "unticked");
    }

    internal MainWindowViewModel Owner => _owner;

    public Step Model { get; }

    public int Index { get; }

    public string Number => Index.ToString("00");

    public string Id => Model.Id;

    public string Title => Model.Title;

    public string? Description => _description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(_description);

    public string? Why => Model.Why;

    public bool HasWhy => !string.IsNullOrWhiteSpace(Model.Why);

    public string? Warning => Model.Warning;

    public bool HasWarning => !string.IsNullOrWhiteSpace(Model.Warning);

    public bool IsDestructive => Model.Destructive;

    public bool IsElevated => Model.Elevated;

    /// <summary>An elevated step cannot run while the player itself is not elevated (ADR 0013); Skip stays available.</summary>
    public bool ElevationBlocked => IsScript && ElevationGate.Check(Model.Elevated, _owner.ElevationProbe).Blocked;

    public string ElevationNote => ElevationBlocked ? ElevationGate.BlockedReason : string.Empty;

    /// <summary>The "Restart as administrator" action of the guide (also offered next to the blocked step).</summary>
    public IRelayCommand RestartElevatedCommand => _owner.RestartElevatedCommand;

    public bool CanRelaunchElevated => _owner.CanRelaunchElevated;

    public StepType Type => Model.EffectiveType;

    public bool IsScript => Type == StepType.Script;

    public bool IsManual => Type == StepType.Manual;

    public bool IsInfo => Type == StepType.Info;

    public bool IsChoice => Type == StepType.Choice;

    /// <summary>The answers of a choice step.</summary>
    public ObservableCollection<OptionViewModel> Options { get; }

    /// <summary>The parameters the command can be edited with, by name (the same objects the Parameters page edits).</summary>
    public IReadOnlyDictionary<string, VariableViewModel> InlineParameters { get; }

    /// <summary>The command with its parameter placeholders still in it, for the editable command view.</summary>
    [ObservableProperty]
    private string _inlineCode = string.Empty;

    /// <summary>"After this step, continue with 05 Install" when the guide declares where success leads.</summary>
    public string NextText => Model.Next is { } id && !IsChoice ? "After this step, continue with " + _owner.Describe(id) : string.Empty;

    /// <summary>"If this step fails, continue with 07 Roll back" when the guide declares a failure route.</summary>
    public string FailureText => Model.OnFailure is { } id ? "If this step fails, continue with " + _owner.Describe(id) : string.Empty;

    public bool HasNextText => NextText.Length > 0;

    public bool HasFailureText => FailureText.Length > 0;

    public bool HasFailureRoute => Model.OnFailure is not null;

    /// <summary>The path-relevant state of this step (see <see cref="FlowPlanner"/>).</summary>
    public StepFlowState Flow => Status switch
    {
        StepStatus.Done => new StepFlowState(FlowState.Done, _chosenTarget),
        StepStatus.Failed => new StepFlowState(FlowState.Failed),
        StepStatus.Skipped when !SkippedByBranch => new StepFlowState(FlowState.Skipped),
        _ => new StepFlowState(FlowState.Open),
    };

    public string TypeLabel => Type.ToString().ToUpperInvariant();

    public string ShellLabel => IsScript ? _owner.ShellName(Model) : string.Empty;

    /// <summary>The language of the command preview, so it is highlighted as PowerShell, bash or batch.</summary>
    public string PreviewLanguage => CodeLanguage.ForShell(_owner.Runbook?.ShellOf(Model) ?? ShellKind.Powershell);

    public string Subtitle => IsScript ? $"script · {ShellLabel}" : Type.ToString().ToLowerInvariant();

    public string ConfirmText => string.IsNullOrWhiteSpace(Model.Confirm) ? "Done" : Model.Confirm!;

    public ObservableCollection<OutputLine> Output { get; } = new();

    public bool HasOutput => Output.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsSkipped), nameof(StatusLabel), nameof(StatusIcon), nameof(RunLabel), nameof(CanRun), nameof(CanSkip), nameof(CanFinish), nameof(ShowFinish), nameof(WaitsForChecklist), nameof(CanTick), nameof(CanNext))]
    private StepStatus _status = StepStatus.Pending;

    [ObservableProperty]
    private bool _skippedByCondition;

    /// <summary>The step lies on a path the person did not take (another option of a choice, a jump over it).</summary>
    [ObservableProperty]
    private bool _skippedByBranch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanFinish), nameof(ShowFinish), nameof(WaitsForChecklist), nameof(CanTick), nameof(CanSkip), nameof(CanChoose))]
    private bool _isBlocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _confirmPending;

    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    private string? _workingDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems), nameof(CanRun))]
    private string[] _problems = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRisks))]
    private string[] _risks = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVerdict))]
    private string _verdict = string.Empty;

    [ObservableProperty]
    private string _runInfo = string.Empty;

    public bool HasProblems => Problems.Length > 0;

    public bool HasRisks => Risks.Length > 0;

    public bool HasVerdict => Verdict.Length > 0;

    public bool IsPending => Status == StepStatus.Pending;

    public bool IsRunning => Status == StepStatus.Running;

    public bool IsDone => Status == StepStatus.Done;

    public bool IsFailed => Status == StepStatus.Failed;

    public bool IsSkipped => Status == StepStatus.Skipped;

    /// <summary>The Run button is available when the steps before are settled, the parameters are complete and nothing is running.</summary>
    public bool CanRun => IsScript && !IsBlocked && !IsRunning && !ConfirmPending && Problems.Length == 0 && !ElevationBlocked;

    /// <summary>The confirm button is shown for manual and info steps that are open.</summary>
    public bool ShowFinish => (IsManual || IsInfo) && !IsBlocked && !IsDone;

    /// <summary>The confirm button is enabled once every required checklist item is ticked (ADR 0010).</summary>
    public bool CanFinish => ShowFinish && ChecklistComplete;

    /// <summary>The step could be confirmed but required checklist items are still open.</summary>
    public bool WaitsForChecklist => ShowFinish && !ChecklistComplete;

    /// <summary>Choice options can be picked (also again, to take another path) while the step is not blocked or skipped.</summary>
    public bool CanChoose => IsChoice && !IsBlocked && !IsSkipped && !IsRunning;

    public bool CanNext => IsDone || IsSkipped || (IsFailed && HasFailureRoute);

    public bool CanSkip => !IsRunning && !IsDone && !IsSkipped && !IsBlocked;

    public string RunLabel => Status switch
    {
        StepStatus.Failed => "Retry",
        StepStatus.Done => "Run again",
        _ => "Run",
    };

    public string StatusLabel => Status switch
    {
        StepStatus.Running => "RUNNING",
        StepStatus.Done => "DONE",
        StepStatus.Failed => "FAILED",
        StepStatus.Skipped => SkippedByBranch ? "SKIPPED · BRANCH" : SkippedByCondition ? "SKIPPED · CONDITION" : "SKIPPED",
        _ => IsBlocked ? "WAITING" : "PENDING",
    };

    public string StatusIcon => Status switch
    {
        StepStatus.Running => "fa-solid fa-spinner",
        StepStatus.Done => "fa-solid fa-circle-check",
        StepStatus.Failed => "fa-solid fa-circle-xmark",
        StepStatus.Skipped => "fa-solid fa-forward",
        _ => "fa-regular fa-circle",
    };

    partial void OnIsBlockedChanged(bool value) => OnPropertyChanged(nameof(StatusLabel));

    partial void OnSkippedByConditionChanged(bool value) => OnPropertyChanged(nameof(StatusLabel));

    partial void OnSkippedByBranchChanged(bool value) => OnPropertyChanged(nameof(StatusLabel));

    partial void OnStatusChanged(StepStatus value)
    {
        OnPropertyChanged(nameof(CanChoose));
        OnPropertyChanged(nameof(Flow));
    }

    /// <summary>Recomputes the command preview, the parameter problems and the risk findings after a parameter changed.</summary>
    public void RefreshPlan()
    {
        if (!IsScript)
            return;
        _plan = StepPlanner.Plan(_owner.Runbook!, Model, _owner.Variables!);
        Preview = _plan.MaskedScript;
        InlineCode = _plan.InlineScript;
        WorkingDirectory = _plan.WorkingDirectory;
        Problems = _plan.Problems.ToArray();
        Risks = _plan.Risks.ToArray();
        OnPropertyChanged(nameof(ElevationBlocked));
        OnPropertyChanged(nameof(ElevationNote));
        OnPropertyChanged(nameof(CanRelaunchElevated));
        OnPropertyChanged(nameof(CanRun));
    }

    /// <summary>
    /// Skips the step by itself while it has not been touched: when it lies on a path not taken (<paramref name="offPath"/>) or its
    /// <c>when</c> condition is false. Undoes that when the reason is gone. True when the status changed.
    /// </summary>
    public bool ApplyAutoSkip(bool offPath)
    {
        var auto = Status == StepStatus.Skipped && (SkippedByCondition || SkippedByBranch);
        if (Status != StepStatus.Pending && !auto)
            return false;

        var byCondition = false;
        if (!offPath && !string.IsNullOrWhiteSpace(Model.When))
            byCondition = Condition.TryParse(Model.When, out var condition, out _) && !condition!.Evaluate(_owner.Variables!.Resolve);

        var wanted = offPath || byCondition ? StepStatus.Skipped : StepStatus.Pending;
        if (wanted == Status && SkippedByBranch == offPath && SkippedByCondition == byCondition)
            return false;
        SkippedByBranch = offPath;
        SkippedByCondition = byCondition;
        Status = wanted;
        return true;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (!IsScript || IsRunning || IsBlocked || ElevationBlocked)
            return;
        RefreshPlan();
        if (_plan is null || !_plan.CanRun)
            return;
        if (Model.Destructive && !ConfirmPending)
        {
            ConfirmPending = true;
            return;
        }
        ConfirmPending = false;
        await ExecuteAsync(_plan);
    }

    /// <summary>Second click of a destructive step, after the warning was shown.</summary>
    [RelayCommand]
    private async Task ConfirmRunAsync()
    {
        if (!ConfirmPending || ElevationBlocked)
            return;
        ConfirmPending = false;
        RefreshPlan();
        if (_plan is { CanRun: true } plan)
            await ExecuteAsync(plan);
    }

    [RelayCommand]
    private void CancelConfirm() => ConfirmPending = false;

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void Skip()
    {
        ConfirmPending = false;
        SkippedByCondition = false;
        SkippedByBranch = false;
        Status = StepStatus.Skipped;
        Verdict = "Skipped by you.";
        AppLog.For("run").Information("Step {Step} skipped", Id);
        _owner.StepSettled(this, advance: true);
    }

    [RelayCommand]
    private void Finish()
    {
        if (!CanFinish)
            return;
        Status = StepStatus.Done;
        Verdict = IsManual ? $"Confirmed by you: {ConfirmText}." : "Read.";
        AppLog.For("run").Information("Step {Step} ({Type}) completed", Id, Type);
        _owner.StepSettled(this, advance: true);
    }

    /// <summary>Moves on to the next step that still needs attention (or back to the overview when everything is settled).</summary>
    [RelayCommand]
    private void Next() => _owner.GoToNext(this);

    [RelayCommand]
    private void Reset()
    {
        if (IsRunning)
            return;
        Output.Clear();
        OnPropertyChanged(nameof(HasOutput));
        Verdict = string.Empty;
        RunInfo = string.Empty;
        SkippedByCondition = false;
        SkippedByBranch = false;
        _chosenTarget = null;
        foreach (var item in ChecklistItems)
            item.IsChecked = false;
        foreach (var option in Options)
            option.IsChosen = false;
        Status = StepStatus.Pending;
        _owner.StepSettled(this, advance: false);
    }

    /// <summary>The person picked an answer of a choice step: the path continues at its target and the other branches are skipped.</summary>
    public void Choose(OptionViewModel option)
    {
        if (!CanChoose)
            return;
        _chosenTarget = option.Model.Goto;
        foreach (var other in Options)
            other.IsChosen = ReferenceEquals(other, option);
        Verdict = $"You chose: {option.Model.Label}.";
        RunInfo = "continues with " + _owner.Describe(option.Model.Goto);
        Status = StepStatus.Done;
        OnPropertyChanged(nameof(Flow));
        AppLog.For("run").Information("Step {Step} (choice): '{Option}' chosen, continues at {Target}", Id, option.Model.Label, option.Model.Goto);
        _owner.StepSettled(this, advance: true);
    }

    /// <summary>Cancels a running step; used when the guide is closed or the window exits.</summary>
    public void Abort() => _cts?.Cancel();

    private async Task ExecuteAsync(StepPlan plan)
    {
        Output.Clear();
        OnPropertyChanged(nameof(HasOutput));
        Verdict = string.Empty;
        RunInfo = "running…";
        Status = StepStatus.Running;
        _owner.StepSettled(this, advance: false);

        var variables = _owner.Variables!;
        AppLog.For("run").Information("Step {Step} started in {Shell}{Destructive}", Id, plan.Shell.ToString().ToLowerInvariant(), Model.Destructive ? " (destructive, confirmed)" : string.Empty);
        AppLog.For("run").Debug("Step {Step} command: {Command}", Id, plan.MaskedScript);

        _cts = new CancellationTokenSource();
        RunResult result;
        try
        {
            result = await ScriptRunner.RunAsync(plan.ToSpec(), line => Dispatcher.UIThread.Post(() => AddOutput(variables, line)), _cts.Token);
        }
        catch (Exception ex)
        {
            AppLog.For("run").Error(ex, "Step {Step} could not be run", Id);
            result = new RunResult(null, string.Empty, TimeSpan.Zero, false, false, ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }

        var judged = StepEvaluation.Judge(Model.Success, result);
        if (judged.Success)
        {
            foreach (var (name, value) in StepEvaluation.ExtractCaptures(Model.Captures, result.Output))
                variables.Set(name, value);
        }

        Verdict = variables.MaskSecrets(judged.Reason);
        RunInfo = $"{(result.ExitCode is { } code ? "exit " + code : "no exit code")} · {result.Duration.TotalSeconds:0.0} s";
        Status = judged.Success ? StepStatus.Done : StepStatus.Failed;
        AppLog.For("run").Information("Step {Step} {Outcome}: {Reason} ({Info})", Id, judged.Success ? "succeeded" : "failed", Verdict, RunInfo);
        _owner.StepSettled(this, advance: false);
    }

    private void AddOutput(VariableStore variables, OutputLine line)
    {
        Output.Add(line with { Text = variables.MaskSecrets(line.Text) });
        if (Output.Count > MaxOutputLines)
            Output.RemoveAt(0);
        if (Output.Count == 1)
            OnPropertyChanged(nameof(HasOutput));
    }
}

/// <summary>One answer of a choice step, shown as a button.</summary>
public sealed partial class OptionViewModel : ObservableObject
{
    private readonly StepViewModel _step;

    public OptionViewModel(StepViewModel step, ChoiceOption model)
    {
        _step = step;
        Model = model;
        ChooseCommand = new RelayCommand(() => _step.Choose(this));
    }

    public ChoiceOption Model { get; }

    public string Label => Model.Label;

    public string? Description => Model.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Model.Description);

    /// <summary>Where this option leads, e.g. "continues with 05 Install".</summary>
    public string TargetText => "continues with " + _step.Owner.Describe(Model.Goto);

    public IRelayCommand ChooseCommand { get; }

    [ObservableProperty]
    private bool _isChosen;
}

/// <summary>One checkbox of a manual step (ADR 0010).</summary>
public sealed partial class ChecklistItemViewModel : ObservableObject
{
    private readonly StepViewModel _step;

    public ChecklistItemViewModel(StepViewModel step, ChecklistItem model)
    {
        _step = step;
        Model = model;
    }

    public ChecklistItem Model { get; }

    public int Index => Model.Index;

    /// <summary>The text next to the box: basic inline Markdown shown without its markers.</summary>
    public string Text => Checklist.ToPlainText(Model.Text);

    public bool Required => Model.Required;

    public bool IsOptional => !Model.Required;

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _step.ChecklistChanged(this);
}
