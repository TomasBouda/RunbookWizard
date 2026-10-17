using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RunbookWizard.App.Mcp;
using RunbookWizard.App.Services;

namespace RunbookWizard.App.ViewModels;

public enum McpState { Starting, Listening, Off, Failed }

/// <summary>
/// The Settings page (ADR 0015): the switch of the in-app MCP server, its state and the command that registers it with Claude Code.
/// The switch takes effect at once; the application listens to <see cref="McpEnabledChanged"/> and restarts the server.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _save;

    public SettingsViewModel(AppSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
        _mcpEnabled = settings.McpEnabled;
        _port = McpOptions.Resolve(settings).Port;
        _state = settings.McpEnabled ? McpState.Starting : McpState.Off;
    }

    /// <summary>Raised after the person switched the server on or off (already saved).</summary>
    public event Action<bool>? McpEnabledChanged;

    /// <summary>Set by the view: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyText { get; set; }

    [ObservableProperty]
    private bool _mcpEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpCommand), nameof(McpUrl))]
    private int _port;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(IsListening), nameof(IsFailed), nameof(IsOff))]
    private McpState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    private string? _failure;

    [ObservableProperty]
    private string _copyHint = string.Empty;

    public string McpUrl => $"http://127.0.0.1:{Port}/mcp";

    public string McpCommand => McpOptions.RegisterCommand(Port);

    public bool IsListening => State == McpState.Listening;

    public bool IsFailed => State == McpState.Failed;

    public bool IsOff => State is McpState.Off or McpState.Starting;

    public string StateLabel => State switch
    {
        McpState.Listening => $"LISTENING · 127.0.0.1:{Port}",
        McpState.Failed => (Failure ?? $"Port {Port} could not be opened").ToUpperInvariant(),
        McpState.Starting => "STARTING",
        _ => "OFF",
    };

    partial void OnMcpEnabledChanged(bool value)
    {
        _settings.McpEnabled = value;
        _save();
        AppLog.For("mcp").Information("MCP server switched {State} in the settings", value ? "on" : "off");
        if (!value)
            State = McpState.Off;
        McpEnabledChanged?.Invoke(value);
    }

    public void SetListening(int port)
    {
        Port = port;
        Failure = null;
        State = McpState.Listening;
    }

    public void SetFailed(int port, string message)
    {
        Port = port;
        Failure = message;
        State = McpState.Failed;
    }

    public void SetOff() => State = McpState.Off;

    [RelayCommand]
    private async Task CopyCommandAsync()
    {
        if (CopyText is null)
            return;
        await CopyText(McpCommand);
        CopyHint = "Copied.";
    }
}
