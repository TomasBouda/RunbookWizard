using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RunbookWizard.Core.Execution;
using RunbookWizard.App.Services;
using RunbookWizard.App.ViewModels;
using RunbookWizard.App.Views;
using TomLabs.AutoUpdate;
using TomLabs.UiAutomation;

namespace RunbookWizard.App;

public partial class App : Application
{
    private SettingsStore? _store;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        _store = new SettingsStore(Environment.GetCommandLineArgs());
        var settings = _store.Load();
        AppLog.Initialize(settings, _store.Directory);
        // System ("Default") leaves the variant at Default, so the palette follows Windows live.
        ThemeModes.Apply(settings.Theme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.For("app").Error(e.Exception, "Unhandled exception");
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = _store ?? new SettingsStore();
            StartUpdater(desktop, store);

            var viewModel = new MainWindowViewModel(store) { ExitApplication = () => desktop.Shutdown() };
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            var automation = StartAutomation(desktop, viewModel);
            desktop.Exit += (_, _) =>
            {
                automation?.Dispose();
                viewModel.Shutdown();
                AppLog.Shutdown();
            };

            if (Program.StartupGuide is { } guide)
                viewModel.Open(guide);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Scripted UI channel (RUNBOOKWIZARD_AUTOMATION=&lt;port&gt;, see CLAUDE.md) with the player's own shortcuts on top of the
    /// generic endpoints: <c>/do/page?arg=overview|parameters|logs</c>, <c>/do/open?arg=&lt;path&gt;</c> and <c>/do/select?arg=&lt;step id or number&gt;</c> and <c>/do/choose?arg=&lt;option number or label&gt;</c> (answers the choice step on screen, like clicking its option), <c>/do/tick?arg=&lt;item number|all|none&gt;</c> (toggles checklist items of the selected manual step).
    /// Running a step is not a shortcut: scripts click the Run button like a person does.
    /// </summary>
    private static UiAutomationServer? StartAutomation(IClassicDesktopStyleApplicationLifetime desktop, MainWindowViewModel viewModel)
    {
        var server = UiAutomationServer.StartIfEnabled(desktop, AppInfo.Name, AppInfo.Version, message => AppLog.For("automation").Information("{Message}", message));
        if (server is null)
            return null;

        server.Actions["page"] = (window, arg) =>
        {
            viewModel.GoTo(Enum.TryParse<Page>(arg, true, out var page) ? page : throw new ArgumentException($"Unknown page '{arg}' (overview, parameters, logs)"));
            return new { page = viewModel.Page.ToString() };
        };
        server.Actions["open"] = (window, arg) =>
        {
            viewModel.Open(arg ?? throw new ArgumentException("A guide path is required"));
            return new { title = viewModel.GuideTitle, steps = viewModel.Steps.Count, runnable = viewModel.IsRunnable };
        };
        server.Actions["select"] = (window, arg) =>
        {
            viewModel.SelectStep(arg ?? throw new ArgumentException("A step id or number is required"));
            return new { selected = viewModel.SelectedStep?.Id };
        };
        server.Actions["choose"] = (window, arg) =>
        {
            var step = viewModel.SelectedStep ?? throw new InvalidOperationException("No step is selected");
            var option = (int.TryParse(arg, out var n) && n >= 1 && n <= step.Options.Count ? step.Options[n - 1] : null)
                ?? step.Options.FirstOrDefault(o => string.Equals(o.Label, arg, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"The selected step has no option '{arg}' (use its number or label)");
            option.ChooseCommand.Execute(null);
            return new { chosen = option.Label, selected = viewModel.SelectedStep?.Id };
        };
        // Pretends the player is (not) elevated so both states can be shown headless; "real" asks the process again. The relaunch itself is not scripted (it would show a real UAC prompt).
        server.Actions["elevation"] = (window, arg) =>
        {
            IElevationProbe probe = arg?.ToLowerInvariant() switch
            {
                "on" => new FixedElevationProbe(true),
                "off" => new FixedElevationProbe(false),
                "real" => new ProcessElevationProbe(),
                _ => throw new ArgumentException($"Unknown value '{arg}' (on, off, real)"),
            };
            viewModel.SetElevationProbe(probe);
            return new { elevated = viewModel.IsElevated, banner = viewModel.ShowElevationBanner };
        };
        server.Actions["tick"] =(window, arg) =>
        {
            var step = viewModel.SelectedStep ?? throw new InvalidOperationException("No step is selected");
            if (!step.CanTick)
                throw new InvalidOperationException("The selected step has no checklist that can be changed now");
            if (string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "none", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in step.ChecklistItems)
                    item.IsChecked = string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var item = int.TryParse(arg, out var n) && n >= 1 && n <= step.ChecklistItems.Count
                    ? step.ChecklistItems[n - 1]
                    : throw new ArgumentException($"The selected step has no checklist item '{arg}' (use its number, 'all' or 'none')");
                item.IsChecked = !item.IsChecked;
            }
            return new { ticked = step.ChecklistItems.Count(i => i.IsChecked), complete = step.ChecklistComplete };
        };
        return server;
    }

    /// <summary>
    /// Self-update through TomLabs.AutoUpdate. It reads a latest.json manifest (settings.json "updateManifestUrl", or
    /// RUNBOOKWIZARD_UPDATE_MANIFEST); without a manifest the updater stays off (ADR 0005).
    /// </summary>
    private static void StartUpdater(IClassicDesktopStyleApplicationLifetime desktop, SettingsStore store)
    {
        var settings = store.Load();
        var manifest = Environment.GetEnvironmentVariable("RUNBOOKWIZARD_UPDATE_MANIFEST");
        if (string.IsNullOrWhiteSpace(manifest))
            manifest = settings.UpdateManifestUrl;
        if (!Uri.TryCreate(manifest, UriKind.Absolute, out var manifestUri))
        {
            AppLog.For("update").Debug("No update manifest configured; self-update is off");
            return;
        }

        Updater.Start(new UpdateOptions("RunbookWizard", new ManifestSource(manifestUri))
        {
            Channel = Enum.TryParse<UpdateChannel>(settings.UpdateChannel, out var channel) ? channel : null,
            ChannelChanged = ch =>
            {
                var s = store.Load();
                s.UpdateChannel = ch.ToString();
                store.Save(s);
            },
            ExitApplication = () => desktop.Shutdown(),
            Log = message => AppLog.For("update").Information("{Message}", message),
        });
    }
}
