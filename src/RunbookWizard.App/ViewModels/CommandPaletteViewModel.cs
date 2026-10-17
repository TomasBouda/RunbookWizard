using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using RunbookWizard.App.Services;

namespace RunbookWizard.App.ViewModels;

/// <summary>One entry of the command palette: a step to jump to, a page, or an action.</summary>
public sealed record PaletteItem(string Group, string Icon, string Title, string Hint, string Shortcut, Action Run);

/// <summary>
/// Ctrl+K palette: fuzzy search across steps, pages, recent guides and actions.
/// Items are rebuilt every time the palette opens so they reflect the current guide.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly List<PaletteItem> _all = new();

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private ObservableCollection<PaletteItem> _items = new();

    [ObservableProperty]
    private PaletteItem? _selectedItem;

    private string _query = string.Empty;

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
                Filter();
        }
    }

    public CommandPaletteViewModel(MainWindowViewModel main)
    {
        _main = main;
    }

    public void Open()
    {
        BuildItems();
        Query = string.Empty;
        Filter();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
            return;
        var index = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        SelectedItem = Items[Math.Clamp(index + delta, 0, Items.Count - 1)];
    }

    public void RunSelected()
    {
        var item = SelectedItem ?? Items.FirstOrDefault();
        if (item is null)
            return;
        Close();
        item.Run();
    }

    private void BuildItems()
    {
        _all.Clear();
        var vm = _main;

        foreach (var step in vm.Steps)
        {
            var captured = step;
            _all.Add(new PaletteItem("Step", captured.StatusIcon, $"{captured.Number}  {captured.Title}", captured.Subtitle, "", () => vm.SelectedStep = captured));
        }

        if (vm.HasGuide)
        {
            _all.Add(new PaletteItem("Page", "fa-solid fa-circle-info", "Overview", vm.GuideTitle, "", () => vm.GoTo(Page.Overview)));
            if (vm.HasParameters)
                _all.Add(new PaletteItem("Page", "fa-solid fa-sliders", "Parameters", "values used in the commands", "", () => vm.GoTo(Page.Parameters)));
            if (vm.IsRunnable)
                _all.Add(new PaletteItem("Guide", "fa-solid fa-play", "Start or continue", "opens the first step that needs attention", "", () => vm.StartCommand.Execute(null)));
            _all.Add(new PaletteItem("Guide", "fa-solid fa-rotate", "Reload the guide", vm.SourcePath ?? string.Empty, "", () => vm.ReloadCommand.Execute(null)));
        }

        foreach (var guide in vm.RecentGuides)
        {
            var captured = guide;
            _all.Add(new PaletteItem("Recent", "fa-solid fa-clock-rotate-left", captured.Title, captured.Path, "", () => vm.Open(captured.Path)));
        }
        foreach (var guide in vm.ExampleGuides)
        {
            var captured = guide;
            _all.Add(new PaletteItem("Example", "fa-solid fa-book-open", captured.Title, captured.Hint, "", () => vm.Open(captured.Path)));
        }

        _all.Add(new PaletteItem("App", "fa-solid fa-folder-open", "Open a guide…", ".runbook.md or .runbook.json", "Ctrl+O", () => vm.OpenFileCommand.Execute(null)));
        _all.Add(new PaletteItem("App", "fa-solid fa-circle-half-stroke", "Cycle theme", "system → light → dark", "Ctrl+Shift+L", () => vm.CycleThemeCommand.Execute(null)));
        _all.Add(new PaletteItem("App", ThemeModes.Icon(ThemeModes.System), "Theme: system", "follows Windows", "", () => vm.SetThemeCommand.Execute(ThemeModes.System)));
        _all.Add(new PaletteItem("App", ThemeModes.Icon(ThemeModes.Light), "Theme: light", "remembered", "", () => vm.SetThemeCommand.Execute(ThemeModes.Light)));
        _all.Add(new PaletteItem("App", ThemeModes.Icon(ThemeModes.Dark), "Theme: dark", "remembered", "", () => vm.SetThemeCommand.Execute(ThemeModes.Dark)));
        _all.Add(new PaletteItem("App", "fa-solid fa-scroll", vm.ShowLogs ? "Close the logs" : "Show logs", "recent events and log files", "Ctrl+L", () => vm.ToggleLogsCommand.Execute(null)));
        _all.Add(new PaletteItem("App", "fa-solid fa-gear", vm.ShowSettings ? "Close the settings" : "Settings", "MCP server for agents", "Ctrl+,", () => vm.ToggleSettingsCommand.Execute(null)));

        if (TomLabs.AutoUpdate.Updater.Current is { } updater)
        {
            var build = $"{updater.Build.Version} · {updater.Channel.ToString().ToLowerInvariant()} channel";
            _all.Add(new PaletteItem("App", "fa-solid fa-cloud-arrow-down", "Check for updates", build, "", () => _ = updater.CheckAsync()));
        }
    }

    private void Filter()
    {
        var query = Query.Trim();
        IEnumerable<PaletteItem> result = string.IsNullOrEmpty(query)
            ? _all
            : _all.Select(i => (item: i, score: Score(i, query)))
                  .Where(t => t.score > 0)
                  .OrderByDescending(t => t.score)
                  .Select(t => t.item);

        Items = new ObservableCollection<PaletteItem>(result.Take(14));
        SelectedItem = Items.FirstOrDefault();
    }

    /// <summary>Simple fuzzy score: prefix beats substring beats subsequence; the hint counts half.</summary>
    private static int Score(PaletteItem item, string query) => Math.Max(Score(item.Title, query), Score(item.Hint, query) / 2);

    private static int Score(string text, string query)
    {
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 300;
        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 200;

        var qi = 0;
        foreach (var ch in text)
        {
            if (qi < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[qi]))
                qi++;
        }
        return qi == query.Length ? 100 : 0;
    }
}
