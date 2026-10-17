using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace RunbookWizard.App.Mcp;

/// <summary>Runs work on the thread that owns the player's view models (ADR 0015): tool calls arrive on Kestrel threads.</summary>
public interface IUiThread
{
    Task<T> InvokeAsync<T>(Func<T> work);

    Task<T> InvokeAsync<T>(Func<Task<T>> work);
}

/// <summary>The Avalonia UI thread of the running application.</summary>
public sealed class AvaloniaUiThread : IUiThread
{
    public Task<T> InvokeAsync<T>(Func<T> work) => Dispatcher.UIThread.InvokeAsync(work).GetTask();

    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);
}
