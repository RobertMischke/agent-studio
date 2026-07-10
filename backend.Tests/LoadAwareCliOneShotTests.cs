using AgentStudio.Cli;
using AgentStudio.Runner;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentTaskboard.Tests;

public sealed class LoadAwareCliOneShotTests
{
    [Fact]
    public async Task RecentLoad_TriplesTimeout()
    {
        var inner = new FakeOneShot(Ok());
        var sut = new LoadAwareCliOneShot(inner, new FakeGate(recent: true), NullLogger<LoadAwareCliOneShot>.Instance);
        await sut.RunAsync(new("claude", "haiku", "prompt") { Timeout = TimeSpan.FromSeconds(60) });
        Assert.Equal(TimeSpan.FromSeconds(180), Assert.Single(inner.Requests).Timeout);
    }

    [Fact]
    public async Task LoadRelatedTimeout_RetriesExactlyOnce()
    {
        var inner = new FakeOneShot(Failed("timeout after 180s"), Ok());
        var gate = new FakeGate(recent: true);
        var sut = new LoadAwareCliOneShot(inner, gate, NullLogger<LoadAwareCliOneShot>.Instance);
        var result = await sut.RunAsync(new("claude", "haiku", "prompt") { Timeout = TimeSpan.FromSeconds(60) });
        Assert.True(result.Ok);
        Assert.Equal(2, inner.Requests.Count);
        Assert.Equal(2, gate.WaitCount);
    }

    [Fact]
    public async Task OrdinaryFailure_IsNotRetried()
    {
        var inner = new FakeOneShot(Failed("exit 1"));
        var sut = new LoadAwareCliOneShot(inner, new FakeGate(recent: true), NullLogger<LoadAwareCliOneShot>.Instance);
        await sut.RunAsync(new("claude", "haiku", "prompt"));
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task ExhaustedLoadRetry_UsesEnvironmentalLoadCategory()
    {
        var inner = new FakeOneShot(Failed("timeout after 180s"), Failed("timeout after 180s"));
        var sut = new LoadAwareCliOneShot(inner, new FakeGate(recent: true), NullLogger<LoadAwareCliOneShot>.Instance);
        var result = await sut.RunAsync(new("claude", "haiku", "prompt"));
        Assert.StartsWith("environmental-load:", result.Error);
        Assert.Equal(2, inner.Requests.Count);
    }

    private static CliOneShotResult Ok() => Result(true, null);
    private static CliOneShotResult Failed(string error) => Result(false, error);
    private static CliOneShotResult Result(bool ok, string? error)
    {
        var now = DateTime.UtcNow;
        return new(ok, ok ? 0 : -1, "", "", TimeSpan.Zero, "", null, null,
            new(now, null, now, null, 0), error);
    }

    private sealed class FakeGate(bool recent) : ILoadThrottleGate
    {
        public LoadThrottleDecision Current { get; set; } = new(false, 50, TimeSpan.Zero);
        public bool WasRecentlyActive => recent;
        public int WaitCount { get; private set; }
        public Task WaitUntilReadyAsync(string reason, CancellationToken ct) { WaitCount++; return Task.CompletedTask; }
    }

    private sealed class FakeOneShot(params CliOneShotResult[] results) : ICliOneShot
    {
        private int _index;
        public string CliType => "claude";
        public List<CliOneShotRequest> Requests { get; } = new();
        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(results[Math.Min(_index++, results.Length - 1)]);
        }
    }
}
