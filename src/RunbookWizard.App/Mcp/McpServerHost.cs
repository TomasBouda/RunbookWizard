using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RunbookWizard.App.Services;

namespace RunbookWizard.App.Mcp;

/// <summary>The MCP port could not be bound; the message is what the status bar shows.</summary>
public sealed class McpStartException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Kestrel on 127.0.0.1 inside the desktop process (ADR 0015): the MCP endpoint at <c>/mcp</c> (streamable HTTP) and
/// <c>GET /health</c>. Loopback only. There is no token; instead a request that carries an <c>Origin</c> header (every
/// browser cross-origin request does, no MCP client does) or a Host other than loopback (DNS rebinding) is refused.
/// Hosting it in the app is what lets an agent open a guide in the player and the person confirm its runs there.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string DisplayName = "RunbookWizard";

    private readonly WebApplication _app;

    private McpServerHost(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    /// <summary>The port actually bound (the requested one, or the free one chosen for port 0).</summary>
    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public string McpUrl => $"{BaseUrl}/mcp";

    /// <summary>Starts the server; throws <see cref="McpStartException"/> when the port is taken or cannot be bound.</summary>
    public static async Task<McpServerHost> StartAsync(RunbookContext context, int port, string version, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "RunbookWizard",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Production",
        });
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // One MB of guide text can expand sixfold as Unicode escapes, plus the JSON-RPC envelope.
            // The tools separately enforce the decoded UTF-8 limits.
            kestrel.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
            kestrel.Listen(System.Net.IPAddress.Loopback, port);
        });
        // The tools and the guard log through AppLog; the framework's own logging stays quiet.
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(context);
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = DisplayName, Version = version, Title = DisplayName };
                options.ServerInstructions =
                    "RunbookWizard is a step-by-step player for runbooks (guides) on the user's desktop. list_runbooks, get_runbook and " +
                    "validate_runbook read guides through the player's parser and change nothing; validate a draft with 'content' before " +
                    "writing it. open_runbook opens a guide in the player, fill_placeholders fills its non-secret placeholders, and " +
                    "run_step asks the person to run the current script step: nothing runs until they press Run in the app (120 s, " +
                    "no answer = declined). Secrets, manual, info and choice steps, and restarting as administrator stay with the person.";
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<RunbookTools>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(RunbookTools.RejectUnknownFields));

        var app = builder.Build();
        app.Use(LoopbackGuard);
        app.MapMcp("/mcp");
        app.MapGet("/health", () =>
        {
            Serilog.Log.Verbose("GET /health");
            return Results.Json(new { status = "Healthy", version, name = DisplayName }, Json);
        });

        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or UnauthorizedAccessException)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            var inUse = ex is IOException or SocketException || ex.InnerException is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse };
            throw new McpStartException(inUse ? $"Port {port} is in use" : $"Port {port} could not be opened: {ex.Message}", ex);
        }

        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(a => new Uri(a).Port).FirstOrDefault() ?? port;
        Serilog.Log.ForContext("SourceContext", "mcp").Information("MCP server listening on http://127.0.0.1:{Port}/mcp", bound);
        return new McpServerHost(app, bound);
    }

    /// <summary>
    /// ADR 0015: browsers are not allowed to talk to this server. Any <c>Origin</c> header is refused (a web page the
    /// user visits could otherwise open guides or request runs), and so is a Host that is not a loopback name.
    /// </summary>
    private static async Task LoopbackGuard(HttpContext context, Func<Task> next)
    {
        var host = context.Request.Host.Host;
        if (!(host is "127.0.0.1" or "localhost" or "[::1]" or "::1"))
        {
            await Refuse(context, "Only loopback hosts are served.").ConfigureAwait(false);
            return;
        }

        if (context.Request.Headers.ContainsKey("Origin"))
        {
            await Refuse(context, "Requests with an Origin header (browsers) are not accepted.").ConfigureAwait(false);
            return;
        }

        await next().ConfigureAwait(false);
    }

    private static async Task Refuse(HttpContext context, string message)
    {
        Serilog.Log.ForContext("SourceContext", "mcp").Warning("Refused a request: {Message}", message);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync(message).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.ForContext("SourceContext", "mcp").Debug("Server stop: {Message}", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
