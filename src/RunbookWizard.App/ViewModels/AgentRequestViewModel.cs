using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RunbookWizard.App.ViewModels;

/// <summary>
/// An agent asks to run one step (ADR 0015). The banner shows who asks, which step, the exact command with secrets masked (the step's
/// live preview) and its warnings; nothing runs until the person presses Run. Decline, a timeout, closing the guide or the agent going
/// away all answer "declined". One answer covers this one step only.
/// </summary>
public sealed partial class AgentRequestViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<AgentRequestViewModel> _closed;

    public AgentRequestViewModel(string agent, StepViewModel step, TimeSpan timeout, Action<AgentRequestViewModel> closed)
    {
        Agent = agent;
        Step = step;
        Timeout = timeout;
        _closed = closed;
    }

    public string Agent { get; }

    public StepViewModel Step { get; }

    public TimeSpan Timeout { get; }

    public string Title => $"{Agent} asks to run step {Step.Number} · {Step.Title}";

    public string TimeoutText => $"Nothing runs until you press Run. No answer within {Timeout.TotalSeconds:0} s counts as declined.";

    /// <summary>True when the person pressed Run; false for every other ending.</summary>
    public Task<bool> Answer => _answer.Task;

    public bool IsOpen => !_answer.Task.IsCompleted;

    [RelayCommand]
    private void Run() => Close(true);

    [RelayCommand]
    private void Decline() => Close(false);

    /// <summary>Ends the request as declined (timeout, guide closed, client gone). No effect once answered.</summary>
    internal void Cancel() => Close(false);

    private void Close(bool run)
    {
        if (_answer.TrySetResult(run))
        {
            OnPropertyChanged(nameof(IsOpen));
            _closed(this);
        }
    }
}
