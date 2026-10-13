using System.Diagnostics;
using RunbookWizard.Core.Model;

namespace RunbookWizard.Core.Execution;

/// <summary>Tells whether the player process runs with administrator (Windows) or root (Unix) rights. Injected so the gating is testable.</summary>
public interface IElevationProbe
{
    bool IsElevated { get; }
}

/// <summary>The real probe: <see cref="Environment.IsPrivilegedProcess"/> (elevated administrator on Windows, euid 0 on Unix).</summary>
public sealed class ProcessElevationProbe : IElevationProbe
{
    public bool IsElevated => Environment.IsPrivilegedProcess;
}

/// <summary>A probe with a fixed answer, for tests and for the automation channel.</summary>
public sealed class FixedElevationProbe(bool isElevated) : IElevationProbe
{
    public bool IsElevated { get; } = isElevated;
}

/// <summary>Whether a step may run with the rights the player has (ADR 0012: the player never elevates itself, ADR 0004).</summary>
public sealed record ElevationCheck(bool Blocked, string Reason)
{
    public static readonly ElevationCheck Allowed = new(false, string.Empty);
}

public static class ElevationGate
{
    public const string BlockedReason = "This step needs administrator rights and the player is not running as administrator. Restart as administrator to run it, or skip it.";

    public static ElevationCheck Check(bool stepNeedsElevation, IElevationProbe probe)
        => stepNeedsElevation && !probe.IsElevated ? new ElevationCheck(true, BlockedReason) : ElevationCheck.Allowed;

    /// <summary>The steps of a guide that have to run elevated.</summary>
    public static int CountElevated(IEnumerable<Step> steps) => steps.Count(s => s.Elevated && s.EffectiveType == StepType.Script);

    /// <summary>"2 of 7 steps need administrator rights." Empty when none does.</summary>
    public static string Summary(int elevated, int total)
        => elevated == 0 ? string.Empty : $"{elevated} of {total} steps need administrator rights.";
}

/// <summary>
/// How the player starts a second copy of itself with administrator rights (Windows UAC, the "runas" verb). The command line carries the
/// guide path and the data folder only: never a parameter value (they would be visible in the process list), so the new instance starts
/// with the guide's defaults and the person re-enters what they had typed.
/// </summary>
public static class ElevatedRelaunch
{
    /// <summary>The arguments for the new instance, quoted for the Windows command line.</summary>
    public static IReadOnlyList<string> BuildArguments(string? entryAssemblyPath, string processPath, string guidePath, string? dataDirectory)
    {
        var args = new List<string>();
        // Started as `dotnet RunbookWizard.dll`: the new process must be given the library again.
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entryAssemblyPath))
            args.Add(entryAssemblyPath);
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            args.Add("--data-dir");
            args.Add(dataDirectory);
        }
        args.Add(guidePath);
        return args;
    }

    public static ProcessStartInfo BuildStartInfo(string? entryAssemblyPath, string processPath, string guidePath, string? dataDirectory)
    {
        var info = new ProcessStartInfo(processPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(processPath) ?? string.Empty,
        };
        info.Arguments = string.Join(' ', BuildArguments(entryAssemblyPath, processPath, guidePath, dataDirectory).Select(Quote));
        return info;
    }

    /// <summary>Quotes one argument the way the Windows C runtime parses it back (backslashes before a quote are doubled).</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            return argument;
        var text = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
                text.Append('\\', backslashes * 2 + 1).Append('"');
            else
                text.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        return text.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
