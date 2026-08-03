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

    // MachineBound 20.07.: Zeitbasierte Expiry-/Downsampling-Kompaktierung flaked unter Gate-Parallellast (AGT-2177 Gate-22), solo gruen.
    [Trait("Category", "MachineBound")]
    [Fact]
    public void Compact_DropsExpiredPoints_AndDownsamplesHistoricPoints()
    {
        var now = DateTime.UtcNow;
        var fiveMinuteTicks = TimeSpan.FromMinutes(5).Ticks;
        var historicBase = new DateTime(
            now.AddHours(-72).Ticks - now.AddHours(-72).Ticks % fiveMinuteTicks,
            DateTimeKind.Utc);
        var source = new List<HostTelemetrySample>
        {
            Sample(now.AddDays(-15)),
            Sample(historicBase.AddMinutes(1), cpu: 20),
            Sample(historicBase.AddMinutes(2), cpu: 40),
            Sample(now.AddMinutes(-1), cpu: 60),
        };

        var compacted = HostTelemetryStore.Compact(source, now);

        Assert.Equal(2, compacted.Count);
        Assert.Equal(30, compacted[0].CpuPercent);
        Assert.Equal(60, compacted[1].CpuPercent);
    }

    [Fact]
    public void Compact_preserves_the_latest_task_server_route_state_in_each_bucket()
    {
        var now = DateTime.UtcNow;
        var fiveMinuteTicks = TimeSpan.FromMinutes(5).Ticks;
        var bucket = new DateTime(
            now.AddHours(-72).Ticks - now.AddHours(-72).Ticks % fiveMinuteTicks,
            DateTimeKind.Utc);
        var source = new List<HostTelemetrySample>
        {
            Sample(bucket.AddMinutes(1)) with
            {
                TaskServerConnectionStatus = "reachable",
                TaskServerConnectionObservedAt = bucket.AddMinutes(1),
            },
            Sample(bucket.AddMinutes(2)) with
            {
                TaskServerConnectionStatus = "unreachable",
                TaskServerConnectionObservedAt = bucket.AddMinutes(2),
                TaskServerConnectionFailureStartedAt = bucket.AddMinutes(1.5),
                TaskServerConnectionConsecutiveFailures = 3,
            },
        };

        var compacted = Assert.Single(HostTelemetryStore.Compact(source, now));

        Assert.Equal("unreachable", compacted.TaskServerConnectionStatus);
        Assert.Equal(3, compacted.TaskServerConnectionConsecutiveFailures);
        Assert.Equal(bucket.AddMinutes(2), compacted.TaskServerConnectionObservedAt);
    }

    [Fact]
    public void Findings_CoalescesFlappingLoadAcrossShortSampleGaps()
    {
        var start = DateTime.UtcNow.AddMinutes(-5);
        var points = new[] { true, true, true, false, true, false, true, true }
            .Select((pressured, index) => Sample(
                start.AddSeconds(index * 30),
                load: pressured ? 20 : 12,
                ioWait: pressured ? 12 : 2))
            .ToList();

        var finding = Assert.Single(HostTelemetryStore.Findings(points),
            candidate => candidate.Kind == "oversubscribed");

        Assert.True(finding.IsActive);
        Assert.Equal(1, finding.Occurrences);
        Assert.Equal(points[0].Timestamp, finding.Since);
        Assert.Equal(points[^1].Timestamp, finding.Until);
    }

    [Fact]
    public void Findings_AggregatesEndedPhasesPerKindWithinWindow()
    {
        var start = DateTime.UtcNow.AddMinutes(-10);
        var pressured = new[]
        {
            true, true, true, false, false, false,
            true, true, true, false, false, false,
            true, true, true, false, false, false,
        };
        var points = pressured.Select((isPressured, index) => Sample(
            start.AddSeconds(index * 30),
            load: isPressured ? 20 : 12,
            ioWait: isPressured ? 12 : 2)).ToList();

        var finding = Assert.Single(HostTelemetryStore.Findings(points),
            candidate => candidate.Kind == "oversubscribed");

        Assert.False(finding.IsActive);
        Assert.Equal(3, finding.Occurrences);
        Assert.Equal(points[0].Timestamp, finding.Since);
        Assert.Equal(points[14].Timestamp, finding.Until);
    }

    [Fact]
    public void Findings_RequiresLoadHeadroomAndDamageSignalForOversubscription()
    {
        var start = DateTime.UtcNow.AddMinutes(-2);
        var quotaBoundReviewLoad = Enumerable.Range(0, 4)
            .Select(index => Sample(start.AddSeconds(index * 30), load: 24, steal: 0, ioWait: 2))
            .ToList();
        var belowHeadroomWithDamage = Enumerable.Range(0, 4)
            .Select(index => Sample(start.AddSeconds(index * 30), load: 17, steal: 0, ioWait: 12))
            .ToList();
        var displaced = Enumerable.Range(0, 4)
            .Select(index => Sample(start.AddSeconds(index * 30), load: 20, steal: 0, ioWait: 12))
            .ToList();

        Assert.DoesNotContain(HostTelemetryStore.Findings(quotaBoundReviewLoad),
            candidate => candidate.Kind == "oversubscribed");
        Assert.DoesNotContain(HostTelemetryStore.Findings(belowHeadroomWithDamage),
            candidate => candidate.Kind == "oversubscribed");
        Assert.Contains(HostTelemetryStore.Findings(displaced),
            candidate => candidate.Kind == "oversubscribed");
    }

    private static HostTelemetrySample Sample(
        DateTime at,
        double? cpu = 50,
        double? steal = 0,
        double? load = 6.4,
        double? ioWait = 2) =>
        new(at, cpu, load, 6, 5, 32_000_000_000, 64_000_000_000, 0, 0, steal, ioWait, 12, 6);
}
