using System;
using System.Threading.Tasks;

namespace RunbookWizard.App.Mcp;

/// <summary>Serializes start, stop and restart so an old listener releases its port before the next one starts (ADR 0015).</summary>
public sealed class McpServerController(Func<McpOptions, IAsyncDisposable?> start) : IAsyncDisposable
{
    private readonly object _gate = new();
    private Task _pending = Task.CompletedTask;
    private IAsyncDisposable? _current;
    private bool _stopped;

    /// <summary>Stops the running server (if any) and starts one with <paramref name="options"/> (which may say it is off).</summary>
    public Task RestartAsync(McpOptions options)
    {
        lock (_gate)
        {
            if (_stopped)
                return _pending;
            var previous = _pending;
            return _pending = Task.Run(() => ReplaceAsync(previous, options));
        }
    }

    private async Task ReplaceAsync(Task previous, McpOptions? options)
    {
        await previous.ConfigureAwait(false);
        if (_current is { } current)
            await current.DisposeAsync().ConfigureAwait(false);
        _current = options is null ? null : start(options);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_stopped)
            {
                _stopped = true;
                var previous = _pending;
                _pending = Task.Run(() => ReplaceAsync(previous, null));
            }
            return new ValueTask(_pending);
        }
    }
}
