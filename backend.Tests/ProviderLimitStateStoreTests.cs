using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProviderLimitStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "provider-limit-state-" + Guid.NewGuid().ToString("N"));

    public ProviderLimitStateStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ClaudeSessionLimit_ParsesReportedClockReset()
    {
        var observed = new DateTime(2026, 8, 23, 22, 0, 0, DateTimeKind.Utc);
        var berlin = TimeZoneInfo.CreateCustomTimeZone(
            "test-berlin",
            TimeSpan.FromHours(2),
            "test-berlin",
            "test-berlin");

        var result = ProviderLimitParser.Parse(
            CliTypes.Claude,
            [Line("You've hit your session limit · resets 12:20am")],
            observed,
            berlin);

        Assert.True(result.ResetTimeReported);
        Assert.Equal(new DateTime(2026, 8, 23, 22, 20, 0, DateTimeKind.Utc), result.RetryAt);
    }

    [Fact]
    public void ClaudeSessionLimit_UsesProviderReportedTimeZone()
    {
        var observed = new DateTime(2026, 8, 23, 20, 0, 0, DateTimeKind.Utc);
        var deliberatelyWrongLocalZone = TimeZoneInfo.Utc;

        var result = ProviderLimitParser.Parse(
            CliTypes.Claude,
            [Line("You've hit your session limit · resets 12:20am (Europe/Berlin)")],
            observed,
            deliberatelyWrongLocalZone);

        Assert.True(result.ResetTimeReported);
        Assert.Equal(new DateTime(2026, 8, 23, 22, 20, 0, DateTimeKind.Utc), result.RetryAt);
    }

    [Fact]
    public void StructuredRateLimit_UsesEpochReset()
    {
        var observed = new DateTime(2026, 8, 23, 20, 0, 0, DateTimeKind.Utc);
        var reset = observed.AddHours(2);

        var result = ProviderLimitParser.Parse(
            CliTypes.Claude,
            [Line($"Rate limit · rejected [resetsAt={new DateTimeOffset(reset).ToUnixTimeSeconds()}]")],
            observed);

        Assert.Equal(reset, result.RetryAt);
    }

    [Fact]
    public void Limit_PausesOnlyMatchingCli_ThenSingleProbeSelfResumes()
    {
        var store = Store();
        var now = new DateTime(2026, 8, 23, 22, 0, 0, DateTimeKind.Utc);
        store.Record(new ProviderLimitObservation(
            CliTypes.Claude,
            now,
            now.AddMinutes(20),
            "session limit",
            ResetTimeReported: true));

        Assert.Equal(ProviderLimitAdmission.Limited,
            store.Evaluate(CliTypes.Claude, now.AddMinutes(1), mayProbe: true));
        Assert.Equal(ProviderLimitAdmission.Ready,
            store.Evaluate(CliTypes.Codex, now.AddMinutes(1), mayProbe: true));

        Assert.Equal(ProviderLimitAdmission.Probe,
            store.Evaluate(CliTypes.Claude, now.AddMinutes(20), mayProbe: true));
        Assert.Equal(ProviderLimitAdmission.Limited,
            store.Evaluate(CliTypes.Claude, now.AddMinutes(20), mayProbe: true));
        Assert.True(store.MarkProbeHealthy(CliTypes.Claude));
        Assert.Equal(ProviderLimitAdmission.Ready,
            store.Evaluate(CliTypes.Claude, now.AddMinutes(20), mayProbe: true));
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void DurableLimit_RestartsAsLimitedAndNotAsAnOrphanProbe()
    {
        var now = DateTime.UtcNow;
        var first = Store();
        first.Record(new ProviderLimitObservation(
            CliTypes.Claude,
            now,
            now.AddHours(1),
            "session limit",
            ResetTimeReported: true));

        var restarted = Store();
        var limit = Assert.Single(restarted.Snapshot());
        Assert.Equal("limited", limit.Status);
        Assert.False(limit.ProbeInFlight);
        Assert.Equal(ProviderLimitAdmission.Limited,
            restarted.Evaluate(CliTypes.Claude, now.AddMinutes(1), mayProbe: true));
    }

    [Fact]
    public void DueProbe_IsReservedOnlyAtPickupCommit_AndCanBeReleasedAfterSpawnFailure()
    {
        var store = Store();
        var now = DateTime.UtcNow;
        store.Record(new ProviderLimitObservation(
            CliTypes.Claude,
            now.AddMinutes(-10),
            now.AddMinutes(-5),
            "session limit",
            ResetTimeReported: true));

        Assert.Equal(ProviderLimitAdmission.Probe, store.Peek(CliTypes.Claude, now));
        Assert.Equal(ProviderLimitAdmission.Probe, store.Peek(CliTypes.Claude, now));
        Assert.False(Assert.Single(store.Snapshot()).ProbeInFlight);

        Assert.True(store.TryBeginProbe(CliTypes.Claude, now, "probe-job"));
        Assert.Equal("probe-job", Assert.Single(store.Snapshot()).ProbeJobId);
        Assert.False(store.MarkProbeHealthy(CliTypes.Claude, "other-job"));
        store.ReleaseProbe(CliTypes.Claude, now.AddMinutes(1), "other-job");
        Assert.True(Assert.Single(store.Snapshot()).ProbeInFlight);
        store.ReleaseProbe(CliTypes.Claude, now.AddMinutes(1), "probe-job");

        var released = Assert.Single(store.Snapshot());
        Assert.False(released.ProbeInFlight);
        Assert.Equal(now.AddMinutes(1), released.LimitedUntil);
    }

    private ProviderLimitStateStore Store()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
            })
            .Build();
        return new ProviderLimitStateStore(
            configuration,
            NullLogger<ProviderLimitStateStore>.Instance);
    }

    private static CliOutputLine Line(string text) => new()
    {
        Timestamp = DateTime.UtcNow,
        Stream = "stderr",
        Text = text,
    };
}
