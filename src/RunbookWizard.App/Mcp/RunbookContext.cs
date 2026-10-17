using System;
using RunbookWizard.App.ViewModels;

namespace RunbookWizard.App.Mcp;

/// <summary>What the tools need from the application: the player, its UI thread, and bringing the window forward.</summary>
public sealed class RunbookContext(MainWindowViewModel player, IUiThread ui, Action bringToFront, TimeSpan? confirmTimeout = null)
{
    /// <summary>How long run_step waits for the person's Run or Decline (ADR 0015).</summary>
    public static readonly TimeSpan DefaultConfirmTimeout = TimeSpan.FromSeconds(120);

    public MainWindowViewModel Player { get; } = player;

    public IUiThread Ui { get; } = ui;

    public Action BringToFront { get; } = bringToFront;

    public TimeSpan ConfirmTimeout { get; } = confirmTimeout ?? DefaultConfirmTimeout;
}
