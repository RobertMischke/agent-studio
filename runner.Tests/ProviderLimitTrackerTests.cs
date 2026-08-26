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
}
