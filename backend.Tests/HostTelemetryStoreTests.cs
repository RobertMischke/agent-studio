using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class HostTelemetryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "host-telemetry-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private HostTelemetryStore Store() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build(),
        NullLogger<HostTelemetryStore>.Instance);

    [Fact]
    public void Append_PersistsAcrossStoreRestart_AndEmitsSustainedFinding()
    {
        var now = DateTime.UtcNow;
        var store = Store();
        for (var i = 3; i >= 0; i--)
            store.Append("runner-01", Sample(now.AddSeconds(-i * 30), steal: 7));

        var restarted = Store().Query("runner-01", "1h");

        Assert.Equal(4, restarted.Points.Count);
        Assert.Contains(restarted.Findings, finding => finding.Kind == "vm-throttled");
        Assert.True(File.Exists(Path.Combine(_root, "telemetry", "runner-01.json")));
    }

    [Fact]
    public void Compact_DropsExpiredPoints_AndDownsamplesHistoricPoints()
    {
        var now = DateTime.UtcNow;
        var source = new List<HostTelemetrySample>
        {
            Sample(now.AddDays(-15)),
            Sample(now.AddHours(-72).AddMinutes(1), cpu: 20),
            Sample(now.AddHours(-72).AddMinutes(2), cpu: 40),
            Sample(now.AddMinutes(-1), cpu: 60),
        };

        var compacted = HostTelemetryStore.Compact(source, now);

        Assert.Equal(2, compacted.Count);
        Assert.Equal(30, compacted[0].CpuPercent);
        Assert.Equal(60, compacted[1].CpuPercent);
    }

    private static HostTelemetrySample Sample(DateTime at, double? cpu = 50, double? steal = 0) =>
        new(at, cpu, 6.4, 6, 5, 32_000_000_000, 64_000_000_000, 0, 0, steal, 2, 12, 6);
}
