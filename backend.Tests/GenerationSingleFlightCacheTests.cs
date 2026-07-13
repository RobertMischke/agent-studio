using AgentStudio.Shared;

using Xunit;

namespace AgentStudio.Tests;

public sealed class GenerationSingleFlightCacheTests
{
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

    [Fact]
    public void Invalidate_StartsNewGenerationAndOldFlightCannotRepublish()
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
        Assert.Equal(1, old.GetAwaiter().GetResult());

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
}
