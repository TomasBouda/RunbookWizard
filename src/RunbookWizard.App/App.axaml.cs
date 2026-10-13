using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

            var viewModel = new MainWindowViewModel(store);
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
    /// generic endpoints: <c>/do/page?arg=overview|parameters|logs</c>, <c>/do/open?arg=&lt;path&gt;</c> and <c>/do/select?arg=&lt;step id or number&gt;</c> and <c>/do/choose?arg=&lt;option number or label&gt;</c> (answers the choice step on screen, like clicking its option).
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
