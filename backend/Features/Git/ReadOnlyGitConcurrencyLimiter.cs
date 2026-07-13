namespace AgentStudio.Git;

/// <summary>
/// Process-wide ceiling for the read-only git projections used to enrich board
/// cards. The board can cover many repositories, but it must not turn a cache
/// miss into an unbounded process fan-out.
/// </summary>
internal static class ReadOnlyGitConcurrencyLimiter
{
    internal const int MaxConcurrency = 4;
    private static readonly SemaphoreSlim Slots = new(MaxConcurrency, MaxConcurrency);

    public static TValue Run<TValue>(Func<TValue> operation)
    {
        Slots.Wait();
        try
        {
            return operation();
        }
        finally
        {
            Slots.Release();
        }
    }
}
