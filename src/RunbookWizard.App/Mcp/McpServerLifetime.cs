using System;
using System.Threading;
using System.Threading.Tasks;

namespace RunbookWizard.App.Mcp;

/// <summary>Owns one server's startup and shutdown, including a host that finishes starting after it was cancelled.</summary>
public sealed class McpServerLifetime : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();

    public McpServerLifetime(Func<CancellationToken, Task<McpServerHost>> start)
    {
        Token = _shutdown.Token;
        Startup = Task.Run(() => start(Token));
    }

    public Task<McpServerHost> Startup { get; }

    public CancellationToken Token { get; }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            var host = await Startup.ConfigureAwait(false);
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception) when (Startup.IsFaulted) { } // Startup reports binding failures to the Settings page.
        finally
        {
            _shutdown.Dispose();
        }
    }
}
