using System.Reflection;

namespace RunbookWizard.App;

/// <summary>Build-derived identity shown in the header and reported by the health flag.</summary>
public static class AppInfo
{
    public const string Name = "runbookwizard";

    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the "+<commit hash>" source-link suffix the SDK appends.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
