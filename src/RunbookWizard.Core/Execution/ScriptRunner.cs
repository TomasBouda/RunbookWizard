using System.Diagnostics;
using System.Text;
using RunbookWizard.Core.Model;

namespace RunbookWizard.Core.Execution;

/// <summary>What to run: the exact script text (already substituted), its shell, folder, environment and time limit.</summary>
public sealed record ScriptSpec(ShellKind Shell, string Script, string? WorkingDirectory, IReadOnlyDictionary<string, string> Environment, TimeSpan Timeout);

public sealed record OutputLine(string Text, bool IsError);

/// <summary>Outcome of one run. <see cref="ExitCode"/> is null when the process never started or was killed.</summary>
public sealed record RunResult(int? ExitCode, string Output, TimeSpan Duration, bool TimedOut, bool Cancelled, string? StartError);

/// <summary>Runs a script file through a shell as a child process (ADR 0004): redirected output, timeout, cancellation, temp file removed afterwards.</summary>
public static class ScriptRunner
{
    private const string PowerShellWrapper = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }
        $global:LASTEXITCODE = 0
        try {
            & '__SCRIPT__'
        } catch {
            [Console]::Error.WriteLine($_.ToString())
            if ($_.InvocationInfo -and $_.InvocationInfo.PositionMessage) { [Console]::Error.WriteLine($_.InvocationInfo.PositionMessage) }
            exit 1
        }
        if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        exit 0
        """;

    public static async Task<RunResult> RunAsync(ScriptSpec spec, Action<OutputLine>? onOutput = null, CancellationToken cancellation = default)
    {
        var started = Stopwatch.StartNew();
        var shell = ShellCatalog.Resolve(spec.Shell);
        if (shell is null)
            return new RunResult(null, string.Empty, started.Elapsed, false, false, $"The shell '{spec.Shell.ToString().ToLowerInvariant()}' is not installed on this machine.");

        var directory = Path.Combine(Path.GetTempPath(), "RunbookWizard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var scriptPath = Path.Combine(directory, "step" + shell.Extension);
            await File.WriteAllTextAsync(scriptPath, Normalize(spec.Script, shell), shell.Encoding, cancellation);

            var entry = scriptPath;
            if (shell.IsPowerShell)
            {
                entry = Path.Combine(directory, "run.ps1");
                await File.WriteAllTextAsync(entry, PowerShellWrapper.Replace("__SCRIPT__", scriptPath.Replace("'", "''")), shell.Encoding, cancellation);
            }

            var info = new ProcessStartInfo(shell.Executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = !string.IsNullOrWhiteSpace(spec.WorkingDirectory) && Directory.Exists(spec.WorkingDirectory)
                    ? spec.WorkingDirectory : System.Environment.CurrentDirectory,
            };
            foreach (var argument in shell.Arguments(entry))
                info.ArgumentList.Add(argument);
            foreach (var (key, value) in spec.Environment)
                info.Environment[key] = value;

            var output = new StringBuilder();
            var gate = new object();
            void Receive(string? data, bool isError)
            {
                if (data is null)
                    return;
                lock (gate)
                    output.AppendLine(data);
                onOutput?.Invoke(new OutputLine(data, isError));
            }

            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => Receive(e.Data, false);
            process.ErrorDataReceived += (_, e) => Receive(e.Data, true);

            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new RunResult(null, string.Empty, started.Elapsed, false, false, "Could not start the shell: " + ex.Message);
            }

            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(spec.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
                process.WaitForExit(); // flushes the asynchronous output handlers
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // already gone
                }
                await process.WaitForExitAsync(CancellationToken.None);
                string text;
                lock (gate)
                    text = output.ToString();
                return new RunResult(null, text, started.Elapsed, timeout.IsCancellationRequested && !cancellation.IsCancellationRequested, cancellation.IsCancellationRequested, null);
            }

            lock (gate)
                return new RunResult(process.ExitCode, output.ToString(), started.Elapsed, false, false, null);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // a temp folder left behind is harmless
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Normalize(string script, ShellInfo shell)
    {
        var unix = script.Replace("\r\n", "\n").Replace('\r', '\n');
        return shell.Kind == ShellKind.Cmd ? unix.Replace("\n", "\r\n") + "\r\n" : unix + "\n";
    }
}
