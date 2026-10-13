using System;
using System.Text.Json;
using Avalonia;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;
using TomLabs.UiAutomation;

namespace RunbookWizard.App;

internal static class Program
{
    /// <summary>The guide given on the command line (<c>RunbookWizard guide.runbook.md</c>), opened after the window appears.</summary>
    public static string? StartupGuide { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // A desktop application has no HTTP surface, so the health document is available on the command line
        // instead: `RunbookWizard --health` prints the same JSON a web application would serve on GET /health.
        if (Array.Exists(args, a => a.Equals("--health", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "Healthy", version = AppInfo.Version, name = AppInfo.Name }));
            return 0;
        }

        StartupGuide = FindGuide(args);
        return BuildAvaloniaApp(args).StartWithClassicDesktopLifetime(args);
    }

    private static string? FindGuide(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                return args[i];
        }
        return null;
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(Array.Empty<string>());

    /// <summary>
    /// `--headless` renders into memory instead of a real window, so a script can drive and screenshot the UI through
    /// the automation channel (RUNBOOKWIZARD_AUTOMATION=port, TomLabs.UiAutomation) without touching the user's desktop.
    /// </summary>
    private static AppBuilder BuildAvaloniaApp(string[] args)
    {
        IconProvider.Current.Register<FontAwesomeIconProvider>();

        return AppBuilder.Configure<App>()
            .UsePlatformDetectOrHeadless(args)
            .LogToTrace();
    }
}
