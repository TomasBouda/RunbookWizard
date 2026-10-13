using System.Text;
using RunbookWizard.Core.Model;

namespace RunbookWizard.Core.Execution;

/// <summary>How a shell is started: executable, argument list for a script file and the script file's encoding and extension.</summary>
public sealed record ShellInfo(ShellKind Kind, string Executable, string Extension, Encoding Encoding, Func<string, IReadOnlyList<string>> Arguments, string DisplayName)
{
    /// <summary>PowerShell steps run inside a wrapper (ADR 0004); others run the script file as it is.</summary>
    public bool IsPowerShell => Kind is ShellKind.Powershell or ShellKind.Pwsh or ShellKind.Winps;
}

public static class ShellCatalog
{
    /// <summary>Finds the executable of a shell on this machine; null when it is not installed.</summary>
    public static ShellInfo? Resolve(ShellKind kind)
    {
        switch (kind)
        {
            case ShellKind.Powershell:
                return Resolve(ShellKind.Pwsh) is { } pwsh
                    ? pwsh with { Kind = ShellKind.Powershell }
                    : Resolve(ShellKind.Winps) is { } winps ? winps with { Kind = ShellKind.Powershell } : null;
            case ShellKind.Pwsh:
                return Find("pwsh", @"C:\Program Files\PowerShell\7\pwsh.exe") is { } p ? PowerShell(kind, p, "PowerShell 7") : null;
            case ShellKind.Winps:
                return Find("powershell", Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")) is { } w
                    ? PowerShell(kind, w, "Windows PowerShell 5.1") : null;
            case ShellKind.Bash:
                return Find("bash", @"C:\Program Files\Git\bin\bash.exe", preferFirst: true) is { } b ? Posix(kind, b, "bash") : null;
            case ShellKind.Sh:
                return Find("sh", @"C:\Program Files\Git\bin\sh.exe", preferFirst: true) is { } s ? Posix(kind, s, "sh") : null;
            case ShellKind.Cmd:
                return OperatingSystem.IsWindows()
                    ? new ShellInfo(kind, Path.Combine(Environment.SystemDirectory, "cmd.exe"), ".cmd", new UTF8Encoding(false),
                        file => new[] { "/d", "/q", "/c", file }, "cmd.exe")
                    : null;
            default:
                return null;
        }
    }

    private static ShellInfo PowerShell(ShellKind kind, string exe, string name) =>
        new(kind, exe, ".ps1", new UTF8Encoding(true),
            file => new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }, name);

    private static ShellInfo Posix(ShellKind kind, string exe, string name) =>
        new(kind, exe, ".sh", new UTF8Encoding(false), file => new[] { file }, name);

    /// <summary>Looks in the well-known location first or on PATH first. On Windows the WSL launcher in System32 is not a usable bash and is skipped.</summary>
    private static string? Find(string name, string knownWindowsPath, bool preferFirst = false)
    {
        if (OperatingSystem.IsWindows() && preferFirst && File.Exists(knownWindowsPath))
            return knownWindowsPath;

        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { string.Empty };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (OperatingSystem.IsWindows() && directory.TrimEnd('\\').EndsWith(@"\System32", StringComparison.OrdinalIgnoreCase) && name is "bash" or "sh")
                continue;
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return OperatingSystem.IsWindows() && File.Exists(knownWindowsPath) ? knownWindowsPath : null;
    }
}
