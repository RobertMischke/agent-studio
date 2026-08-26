using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProviderLimitTrackerTests
{
    [Fact]
    public async Task Due_probe_clears_durable_limit_and_resumes_provider()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"provider-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var now = new DateTimeOffset(2026, 8, 23, 22, 0, 0, TimeSpan.Zero);
            var tracker = new ProviderLimitTracker(directory, () => now);
            tracker.Record(new ProviderLimitInfo(
                "claude",
                now,
                now.AddHours(2),
                "session limit; resets 12:00am",
                ResetTimeReported: true));

            var probes = 0;
            Task<ProviderLimitProbeResult> Probe(string _, CancellationToken __)
            {
                probes++;
                return Task.FromResult(new ProviderLimitProbeResult(true, "probe succeeded"));
            }

            Assert.Null(await tracker.ProbeIfDueAsync("claude", Probe, default));
            Assert.True(tracker.IsLimited("claude"));
            Assert.Equal(0, probes);

            now = now.AddHours(2);
            var result = await tracker.ProbeIfDueAsync("claude", Probe, default);

            Assert.True(result!.Recovered);
            Assert.Equal(1, probes);
            Assert.False(tracker.IsLimited("claude"));
            Assert.False(new ProviderLimitTracker(directory, () => now).IsLimited("claude"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Failed_probe_keeps_provider_closed_and_schedules_next_probe()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"provider-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var now = new DateTimeOffset(2026, 8, 24, 0, 20, 0, TimeSpan.Zero);
            var tracker = new ProviderLimitTracker(directory, () => now);
            tracker.Record(new ProviderLimitInfo(
                "claude",
                now.AddHours(-2),
                now,
                "session limit; resets 12:20am",
                ResetTimeReported: true));

            var result = await tracker.ProbeIfDueAsync(
                "claude",
                (_, _) => Task.FromResult(new ProviderLimitProbeResult(
                    false,
                    "provider request still rejected")),
                default);

            Assert.False(result!.Recovered);
            var limited = Assert.IsType<ProviderLimitState>(tracker.Get("claude"));
            Assert.Equal(1, limited.ProbeAttempts);
            Assert.Equal(now.AddMinutes(15), limited.RetryAt);
            Assert.Equal("provider request still rejected", limited.Reason);
            Assert.True(new ProviderLimitTracker(directory, () => now).IsLimited("claude"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
