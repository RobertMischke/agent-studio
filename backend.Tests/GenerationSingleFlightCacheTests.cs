using AgentStudio.Shared;

using Xunit;

namespace AgentStudio.Tests;

public sealed class GenerationSingleFlightCacheTests
{
    // MachineBound 20.07.: Single-Flight-Cache-Concurrency-Timing flaked unter Gate-Parallellast (AGT-2192 Gate-11), solo gruen.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task ConcurrentMisses_RunFactoryOnceAndShareResult()
    {
        var cache = new GenerationSingleFlightCache<int>();
        using var ready = new CountdownEvent(12);
        using var start = new ManualResetEventSlim();
        using var factoryEntered = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        var factoryCalls = 0;

        var callers = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            ready.Signal();
            start.Wait();
            return cache.GetOrCreate("repo", TimeSpan.FromMinutes(1), () =>
            {
                Interlocked.Increment(ref factoryCalls);
                factoryEntered.Set();
                releaseFactory.Wait();
                return 42;
            });
        })).ToArray();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        start.Set();
        Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref factoryCalls));

        releaseFactory.Set();
        var results = await Task.WhenAll(callers);

        Assert.All(results, value => Assert.Equal(42, value));
        Assert.Equal(1, factoryCalls);
    }

    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task Invalidate_StartsNewGenerationAndOldFlightCannotRepublish()
    {
        var cache = new GenerationSingleFlightCache<int>();
        using var oldEntered = new ManualResetEventSlim();
        using var releaseOld = new ManualResetEventSlim();

        var old = Task.Run(() => cache.GetOrCreate("repo", TimeSpan.FromMinutes(1), () =>
        {
            oldEntered.Set();
            releaseOld.Wait();
            return 1;
        }));

        Assert.True(oldEntered.Wait(TimeSpan.FromSeconds(5)));
        cache.Invalidate();

        Assert.Equal(2, cache.GetOrCreate("repo", TimeSpan.FromMinutes(1), () => 2));
        releaseOld.Set();
        Assert.Equal(1, await old);

        Assert.Equal(2, cache.GetOrCreate("repo", TimeSpan.FromMinutes(1), () => 3));
    }

    [Fact]
    public void FailedFlightDoesNotPoisonLaterRetry()
    {
        var cache = new GenerationSingleFlightCache<int>();

        Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrCreate("repo", TimeSpan.FromMinutes(1),
                () => throw new InvalidOperationException("fixture")));

        Assert.Equal(7, cache.GetOrCreate("repo", TimeSpan.FromMinutes(1), () => 7));
    }

    [Fact]
    public void VersionChangeReplacesLogicalValueInsteadOfRetainingEveryRefRevision()
    {
        var cache = new GenerationSingleFlightCache<int>();

        Assert.Equal(1, cache.GetOrCreateVersioned(
            "repo", "ref-a", TimeSpan.FromHours(1), () => 1));
        Assert.Equal(2, cache.GetOrCreateVersioned(
            "repo", "ref-b", TimeSpan.FromHours(1), () => 2));

        Assert.Equal(1, cache.ValueCount);
        Assert.Equal(2, cache.GetOrCreateVersioned(
            "repo", "ref-b", TimeSpan.FromHours(1), () => 3));
    }

    [Fact]
    public void ValueDependentZeroTtl_RetriesAndDoesNotRetainLogicalKeys()
    {
        var cache = new GenerationSingleFlightCache<int>(maxEntries: 2);
        var calls = 0;

        for (var i = 0; i < 20; i++)
        {
            var value = cache.GetOrCreateVersioned(
                "repo-" + i,
                "v1",
                _ => TimeSpan.Zero,
                () => Interlocked.Increment(ref calls));
            Assert.Equal(i + 1, value);
        }

        Assert.Equal(0, cache.ValueCount);
        Assert.Equal(0, cache.TrackedKeyCount);
        Assert.Equal(21, cache.GetOrCreateVersioned(
            "repo-0", "v1", _ => TimeSpan.Zero, () => Interlocked.Increment(ref calls)));
    }

    [Fact]
    public void BoundedCache_EvictsLeastRecentlyUsedLogicalKey()
    {
        var cache = new GenerationSingleFlightCache<int>(maxEntries: 2);

        Assert.Equal(1, cache.GetOrCreate("a", TimeSpan.FromHours(1), () => 1));
        Assert.Equal(2, cache.GetOrCreate("b", TimeSpan.FromHours(1), () => 2));
        Assert.Equal(1, cache.GetOrCreate("a", TimeSpan.FromHours(1), () => 99));
        Assert.Equal(3, cache.GetOrCreate("c", TimeSpan.FromHours(1), () => 3));

        Assert.Equal(2, cache.ValueCount);
        Assert.Equal(2, cache.TrackedKeyCount);
        Assert.Equal(20, cache.GetOrCreate("b", TimeSpan.FromHours(1), () => 20));
    }

    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task InvalidateWhileFactoryRuns_LeavesNoOldGenerationValueOrTracking()
    {
        var cache = new GenerationSingleFlightCache<int>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var old = Task.Run(() => cache.GetOrCreate("repo", TimeSpan.FromHours(1), () =>
        {
            entered.Set();
            release.Wait();
            return 1;
        }));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        cache.Invalidate();
        release.Set();
        Assert.Equal(1, await old);

        Assert.Equal(0, cache.ValueCount);
        Assert.Equal(0, cache.TrackedKeyCount);
    }
}
