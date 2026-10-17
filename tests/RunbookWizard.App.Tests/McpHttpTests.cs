using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RunbookWizard.App.Mcp;

namespace RunbookWizard.App.Tests;

/// <summary>The MCP server (ADR 0015) on a real Kestrel instance on a free port, in front of a real player.</summary>
public sealed class McpHttpTests : IAsyncLifetime
{
    private readonly Harness _h = new();
    private readonly HttpClient _http = new();
    private McpServerHost _host = null!;

    public async Task InitializeAsync() => _host = await McpServerHost.StartAsync(_h.Context, 0, "9.9.9");

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _http.Dispose();
        _h.Dispose();
    }

    private HttpRequestMessage Post(object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _host.McpUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    private async Task<JsonElement> CallAsync(string tool, object arguments)
    {
        using var request = Post(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        var data = string.Join("\n", text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()));
        return JsonDocument.Parse(data.Length > 0 ? data : text).RootElement.GetProperty("result").Clone();
    }

    private static string TextOf(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;

    private static bool IsError(JsonElement result) => result.TryGetProperty("isError", out var error) && error.GetBoolean();

    [Fact]
    public async Task Health_IsTheStandardDocument()
    {
        var json = JsonDocument.Parse(await _http.GetStringAsync($"{_host.BaseUrl}/health")).RootElement;

        Assert.Equal("Healthy", json.GetProperty("status").GetString());
        Assert.Equal("9.9.9", json.GetProperty("version").GetString());
        Assert.Equal("RunbookWizard", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ARequestWithAnOriginHeader_IsRefused()
    {
        using var request = Post(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Origin", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthWithAnOriginHeader_IsRefusedToo()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_host.BaseUrl}/health");
        request.Headers.Add("Origin", "http://localhost:3000");

        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AForeignHostHeader_IsRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_host.BaseUrl}/health");
        request.Headers.Host = "attacker.example";

        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownField_IsRejectedNamingTheAcceptedOnes()
    {
        _h.Player.Open(_h.GuidePath);

        var result = await CallAsync("run_step", new { stepid = "hello" });

        Assert.True(IsError(result));
        Assert.Contains("Unknown field(s) for run_step: stepid", TextOf(result));
        Assert.Contains("Accepted fields: stepId", TextOf(result));
        Assert.Null(_h.Player.AgentRequest);
    }

    [Fact]
    public async Task ARefusal_IsAToolErrorWithTheReason()
    {
        var result = await CallAsync("validate_runbook", new { });

        Assert.True(IsError(result));
        Assert.Contains("exactly one of 'path' and 'content'", TextOf(result));
    }

    [Fact]
    public async Task ARealClient_InitializesListsTheToolsAndListsRunbooks()
    {
        _h.Player.Open(_h.GuidePath);
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_host.McpUrl) }));

        Assert.Equal("RunbookWizard", client.ServerInfo.Name);
        Assert.Equal("9.9.9", client.ServerInfo.Version);
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["fill_placeholders", "get_runbook", "list_runbooks", "open_runbook", "run_step", "validate_runbook"], tools);

        var listed = await client.CallToolAsync("list_runbooks");
        Assert.NotEqual(true, listed.IsError);
        var json = JsonDocument.Parse(((TextContentBlock)listed.Content[0]).Text).RootElement;
        Assert.Contains(json.GetProperty("runbooks").EnumerateArray(), r => r.GetProperty("source").GetString() == "open");
    }

    [Fact]
    public async Task TheClientsNameFromItsClientInfo_MarksWhatItDid()
    {
        var options = new McpClientOptions { ClientInfo = new Implementation { Name = "test-agent", Title = "Test Agent", Version = "1.0" } };
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_host.McpUrl) }), options);

        var opened = await client.CallToolAsync("open_runbook", new Dictionary<string, object?> { ["path"] = _h.GuidePath });
        Assert.NotEqual(true, opened.IsError);
        var filled = await client.CallToolAsync("fill_placeholders", new Dictionary<string, object?> { ["values"] = new Dictionary<string, object?> { ["Target"] = "web09" } });
        Assert.NotEqual(true, filled.IsError);

        Assert.StartsWith("Opened by Test Agent at ", _h.Player.AgentOpenedNote);
        Assert.Equal("Filled in by Test Agent", _h.Parameter("Target").FilledByText);
    }
}
