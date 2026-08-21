using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TunnelSupervisionEndpointTests : IDisposable
{
    private readonly string _watchPath = Path.Combine(Path.GetTempPath(), "tunnel-supervision-" + Guid.NewGuid().ToString("N"));
    private readonly string _statusPath;

    public TunnelSupervisionEndpointTests()
    {
        Directory.CreateDirectory(_watchPath);
        _statusPath = Path.Combine(_watchPath, "supervision-status.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { }
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _watchPath,
            ["TunnelSupervision:StatusFilePath"] = _statusPath,
        }));
    });

    [Fact]
    public async Task NoStatusFileWritten_ReportsNotConfigured()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/system/tunnel-supervision");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TunnelSupervisionResponse>();

        Assert.NotNull(body);
        Assert.Equal(TunnelSupervisionStatuses.NotConfigured, body!.Overall);
        Assert.Null(body.Snapshot);
    }

    [Fact]
    public async Task StatusFileFromTheGuidedScript_RoundTripsAsHealthy()
    {
        var generatedAt = DateTime.UtcNow;
        File.WriteAllText(_statusPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            generatedAt,
            keeper = new
            {
                taskName = "AgentRunner-TunnelKeeper",
                registered = true,
                state = "Running",
                lastStatus = "healthy",
                lastObservedAt = generatedAt,
                lastMessage = "Replacement forward passed the remote functional probe.",
            },
            watchdog = new
            {
                taskName = "AgentRunner-TunnelWatchdog",
                registered = true,
                state = "Running",
                lastProbeAt = generatedAt,
                lastProbeResult = "ok",
                lastHealAt = generatedAt.AddMinutes(-30),
                lastHealResult = "succeeded",
                consecutiveProbeFailures = 0,
            },
        }));

        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/system/tunnel-supervision");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TunnelSupervisionResponse>();

        Assert.NotNull(body);
        Assert.Equal(TunnelSupervisionStatuses.Healthy, body!.Overall);
        Assert.NotNull(body.Snapshot);
        Assert.True(body.Snapshot!.Keeper.Registered);
        Assert.True(body.Snapshot.Watchdog.Registered);
        Assert.Equal("succeeded", body.Snapshot.Watchdog.LastHealResult);
    }

    [Fact]
    public async Task CorruptStatusFile_ReportsNotConfigured_InsteadOfFailingTheRequest()
    {
        File.WriteAllText(_statusPath, "{ not valid json");

        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/system/tunnel-supervision");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TunnelSupervisionResponse>();

        Assert.NotNull(body);
        Assert.Equal(TunnelSupervisionStatuses.NotConfigured, body!.Overall);
    }

    /// <summary>
    /// The watchdog's shell writer emits `"lastHealAt": ""` (unset) rather
    /// than omitting the field or writing null - this is the steady-state
    /// shape for a watchdog that has never needed to heal, i.e. the common
    /// case of a healthy tunnel. The reader must not choke on it.
    /// </summary>
    [Fact]
    public async Task ShellWriterShapedFile_WithEmptyStringTimestamps_StillReportsHealthy()
    {
        var generatedAt = DateTime.UtcNow;
        File.WriteAllText(_statusPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            generatedAt,
            keeper = new
            {
                taskName = "AgentRunner-TunnelKeeper",
                registered = true,
                state = "Running",
                lastStatus = "healthy",
                lastObservedAt = generatedAt,
                lastMessage = (string?)null,
            },
            watchdog = new
            {
                taskName = "AgentRunner-TunnelWatchdog",
                registered = true,
                state = "Running",
                lastProbeAt = generatedAt,
                lastProbeResult = "ok",
                lastHealAt = "",
                lastHealResult = "",
                consecutiveProbeFailures = 0,
            },
        }));

        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/system/tunnel-supervision");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TunnelSupervisionResponse>();

        Assert.NotNull(body);
        Assert.Equal(TunnelSupervisionStatuses.Healthy, body!.Overall);
        Assert.NotNull(body.Snapshot);
        Assert.Null(body.Snapshot!.Watchdog.LastHealAt);
    }
}
