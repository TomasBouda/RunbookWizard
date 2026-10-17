using System;
using RunbookWizard.App.Services;

namespace RunbookWizard.App.Mcp;

/// <summary>Whether the in-app MCP server runs and on which port (ADR 0015): settings, with an environment override for one run.</summary>
public sealed record McpOptions(bool Enabled, int Port)
{
    public const int DefaultPort = 5233;

    public const string PortVariable = "RUNBOOKWIZARD_MCP_PORT";

    /// <summary>
    /// <c>McpEnabled</c> and <c>McpPort</c> from the settings; <c>RUNBOOKWIZARD_MCP_PORT</c> wins over the port when it holds a
    /// valid number. A settings port outside 1-65535 falls back to the default; 0 (any free port, for tests) is accepted only
    /// from the environment.
    /// </summary>
    public static McpOptions Resolve(AppSettings settings, string? environmentPort = null)
    {
        environmentPort ??= Environment.GetEnvironmentVariable(PortVariable);
        var port = settings.McpPort is >= 1 and <= 65535 ? settings.McpPort : DefaultPort;
        if (int.TryParse(environmentPort?.Trim(), out var fromEnvironment) && fromEnvironment is >= 0 and <= 65535)
            port = fromEnvironment;
        return new McpOptions(settings.McpEnabled, port);
    }

    /// <summary>The command that registers this server with Claude Code.</summary>
    public static string RegisterCommand(int port) => $"claude mcp add --transport http runbookwizard http://127.0.0.1:{port}/mcp";
}
