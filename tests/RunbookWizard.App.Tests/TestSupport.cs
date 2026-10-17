using System.Diagnostics;
using System.Text;
using RunbookWizard.App.Mcp;
using RunbookWizard.App.Services;
using RunbookWizard.App.ViewModels;
using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;

namespace RunbookWizard.App.Tests;

/// <summary>The UI thread of the tests: work runs at once, one piece at a time, like on a dispatcher.</summary>
internal sealed class InlineUiThread : IUiThread
{
    public object Gate { get; } = new();

    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        lock (Gate)
        {
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return work();
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }
}

/// <summary>A real player on a temp data folder, its MCP tools, and a guide with every kind of step.</summary>
internal sealed class Harness : IDisposable
{
    public const string Guide = """
        ---
        runbook: 1
        title: Agent test guide
        shell: cmd
        platforms: [windows]
        variables:
          - { name: Target, label: Target, required: true }
          - { name: Port, type: number, default: "8080" }
          - { name: Password, type: secret }
        ---

        ## Read me

        ```step
        id: intro
        ```

        Only text.

        ## Say hello

        ```step
        id: hello
        ```

        Greets the target.

        ```cmd
        echo Hello {{Target}} on {{Port}}
        echo secret=%RUNBOOK_VAR_Password%
        ```

        ## Wipe

        ```step
        id: wipe
        destructive: true
        ```

        Pretends to wipe.

        ```cmd
        echo wiping {{Target}}
        ```

        ## Admin

        ```step
        id: admin
        elevated: true
        ```

        Needs administrator rights.

        ```cmd
        echo admin
        ```

        ## Tidy

        ```step
        id: tidy
        ```

        Only says what it would remove; the risk scanner flags it anyway.

        ```cmd
        echo rd /s nothing-here
        ```

        ## Hand over

        ```step
        id: handover
        type: manual
        ```

        Tell the team.
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rbw-app-tests", Guid.NewGuid().ToString("N"));
    private int _frontCalls;

    public Harness(bool elevated = false, TimeSpan? confirmTimeout = null)
    {
        Directory.CreateDirectory(_folder);
        Store = new SettingsStore(["--data-dir", Path.Combine(_folder, "data")]);
        Player = new MainWindowViewModel(Store, new FixedElevationProbe(elevated));
        Player.PostToUi = action =>
        {
            lock (Ui.Gate)
                action();
        };
        Context = new RunbookContext(Player, Ui, () => Interlocked.Increment(ref _frontCalls), confirmTimeout);
        Tools = new RunbookTools(Context);
        GuidePath = Write("agent-test.runbook.md", Guide);
    }

    public InlineUiThread Ui { get; } = new();

    public SettingsStore Store { get; }

    public MainWindowViewModel Player { get; }

    public RunbookContext Context { get; }

    public RunbookTools Tools { get; }

    public string GuidePath { get; }

    public int FrontCalls => Volatile.Read(ref _frontCalls);

    public string Write(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    public StepViewModel Step(string id) => Player.Steps.Single(s => s.Id == id);

    public VariableViewModel Parameter(string name) => Player.Parameters.Single(p => p.Name == name);

    /// <summary>Opens the guide, fills Target and reads the info step, so 'hello' is the current step.</summary>
    public void OpenAtHello()
    {
        Player.Open(GuidePath);
        Parameter("Target").Value = "web01";
        Step("intro").FinishCommand.Execute(null);
    }

    /// <summary>Waits until the agent-request banner is up (run_step asks on the tool's thread).</summary>
    public async Task<AgentRequestViewModel> WaitForRequestAsync()
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            lock (Ui.Gate)
            {
                if (Player.AgentRequest is { } request)
                    return request;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("No agent request appeared.");
    }

    public void Answer(AgentRequestViewModel request, bool run)
    {
        lock (Ui.Gate)
        {
            if (run)
                request.RunCommand.Execute(null);
            else
                request.DeclineCommand.Execute(null);
        }
    }

    public void Dispose()
    {
        Player.Shutdown();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Runs only on Windows, where the guide's cmd steps can run; reports as skipped elsewhere.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || ShellCatalog.Resolve(ShellKind.Cmd) is null)
            Skip = "needs Windows with cmd";
    }
}
