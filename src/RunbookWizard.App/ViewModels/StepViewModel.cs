using System;
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

    public StepViewModel(MainWindowViewModel owner, Step model, int index)
    {
        _owner = owner;
        Model = model;
        Index = index;
    }

    public Step Model { get; }

    public int Index { get; }

    public string Number => Index.ToString("00");

    public string Id => Model.Id;

    public string Title => Model.Title;

    public string? Description => Model.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Model.Description);

    public string? Why => Model.Why;

    public bool HasWhy => !string.IsNullOrWhiteSpace(Model.Why);

    public string? Warning => Model.Warning;

    public bool HasWarning => !string.IsNullOrWhiteSpace(Model.Warning);

    public bool IsDestructive => Model.Destructive;

    public bool IsElevated => Model.Elevated;

    public StepType Type => Model.EffectiveType;

    public bool IsScript => Type == StepType.Script;

    public bool IsManual => Type == StepType.Manual;

    public bool IsInfo => Type == StepType.Info;

    public string TypeLabel => Type.ToString().ToUpperInvariant();

    public string ShellLabel => IsScript ? _owner.ShellName(Model) : string.Empty;

    public string Subtitle => IsScript ? $"script · {ShellLabel}" : Type.ToString().ToLowerInvariant();

    public string ConfirmText => string.IsNullOrWhiteSpace(Model.Confirm) ? "Done" : Model.Confirm!;

    public ObservableCollection<OutputLine> Output { get; } = new();

    public bool HasOutput => Output.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsSkipped), nameof(StatusLabel), nameof(StatusIcon), nameof(RunLabel), nameof(CanRun), nameof(CanSkip), nameof(CanFinish), nameof(CanNext))]
    private StepStatus _status = StepStatus.Pending;

    [ObservableProperty]
    private bool _skippedByCondition;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanFinish), nameof(CanSkip))]
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
    public bool CanRun => IsScript && !IsBlocked && !IsRunning && !ConfirmPending && Problems.Length == 0;

    public bool CanFinish => (IsManual || IsInfo) && !IsBlocked && !IsDone;

    public bool CanNext => IsDone || IsSkipped;

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
        StepStatus.Skipped => SkippedByCondition ? "SKIPPED · CONDITION" : "SKIPPED",
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

    /// <summary>Recomputes the command preview, the parameter problems and the risk findings after a parameter changed.</summary>
    public void RefreshPlan()
    {
        if (!IsScript)
            return;
        _plan = StepPlanner.Plan(_owner.Runbook!, Model, _owner.Variables!);
        Preview = _plan.MaskedScript;
        WorkingDirectory = _plan.WorkingDirectory;
        Problems = _plan.Problems.ToArray();
        Risks = _plan.Risks.ToArray();
    }

    /// <summary>Applies the step's <c>when</c> condition while the step has not been touched; true when the status changed.</summary>
    public bool EvaluateCondition()
    {
        if (string.IsNullOrWhiteSpace(Model.When) || (Status != StepStatus.Pending && !(Status == StepStatus.Skipped && SkippedByCondition)))
            return false;
        var applies = !Condition.TryParse(Model.When, out var condition, out _) || condition!.Evaluate(_owner.Variables!.Resolve);
        var wanted = applies ? StepStatus.Pending : StepStatus.Skipped;
        if (wanted == Status)
            return false;
        SkippedByCondition = !applies;
        Status = wanted;
        return true;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (!IsScript || IsRunning || IsBlocked)
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
        if (!ConfirmPending)
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
        Status = StepStatus.Pending;
        _owner.StepSettled(this, advance: false);
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
