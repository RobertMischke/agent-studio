using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Per-job state for the auto-mode "stuck loop" - the user's auto run
/// where the agent keeps emitting <c>[[TASK_NEEDS_INPUT]]</c>, the
/// orchestrator keeps replying, and we want the loop to terminate
/// somewhere short of "burn the entire token budget". Lives in memory
/// only; on backend restart the loop counter resets, which is fine
/// because a restart is itself a recovery boundary.
/// </summary>
public sealed record StuckLoopState(
    int IterationCount,
    long CumulativeOrchestratorTokens,
    DateTime FirstAt,
    DateTime LastAt,
    string? LastQuestion,
    string? LastReply,
    string? LastError);

/// <summary>
/// Configurable ceiling for one auto-loop on a single job. Defaults are
/// deliberately generous so a normal multi-turn back-and-forth runs to
/// completion, but bounded so a stuck conversation cannot pin the
/// project's CLI quota indefinitely. Loaded once per runner from
/// <c>StuckLoop:*</c> configuration keys; defaults apply when nothing
/// is configured.
/// </summary>
public sealed record StuckLoopBudget(int MaxIterations, long MaxOrchestratorTokens)
{
    public static readonly StuckLoopBudget Default = new(MaxIterations: 5, MaxOrchestratorTokens: 200_000);
}

public enum StuckLoopVerdict
{
    /// <summary>Allow another orchestrator decision call.</summary>
    Continue,
    /// <summary>Budget exhausted; stop auto-looping and surface to the user.</summary>
    CircuitBreak
}

/// <summary>
/// Pure decision function for the auto-loop circuit breaker. Separated
/// from <see cref="ProjectRunner"/> so the rule can be unit-tested in
/// isolation: same input always produces the same verdict, no I/O.
///
/// <para>
/// Rule of thumb: loop closes when EITHER iteration cap OR cumulative
/// token budget is exceeded. Both are checked because either one can
/// blow up alone - a tight loop with cheap calls hits the iteration
/// cap first; a verbose model with one giant reply per turn hits the
/// token budget first. Belt-and-braces so neither failure mode burns
/// the user's quota.
/// </para>
/// </summary>
public static class StuckLoopGuard
{
    /// <summary>
    /// Returns the empty starting state for a freshly-detected
    /// NEEDS_INPUT loop (zero iterations, zero tokens, all timestamps
    /// at <paramref name="now"/>). Caller updates this with
    /// <see cref="Next"/> after each orchestrator call.
    /// </summary>
    public static StuckLoopState Empty(DateTime now)
        => new(0, 0L, now, now, null, null, null);

    /// <summary>
    /// Build the next state after an orchestrator decision call. Increments
    /// the iteration counter and adds the call's billable tokens (input +
    /// output + cache-creation; cache-read is excluded because the user's
    /// subscription quota does not count it).
    /// </summary>
    public static StuckLoopState Next(
        StuckLoopState? prior,
        OrchestratorTokenUsage? usage,
        string? question,
        string? reply,
        string? error,
        DateTime now)
    {
        var basis = prior ?? Empty(now);
        var addedTokens = usage == null
            ? 0L
            : (long)usage.InputTokens + usage.OutputTokens + usage.CacheCreationTokens;
        return basis with
        {
            IterationCount = basis.IterationCount + 1,
            CumulativeOrchestratorTokens = basis.CumulativeOrchestratorTokens + addedTokens,
            LastAt = now,
            LastQuestion = question ?? basis.LastQuestion,
            LastReply = reply ?? basis.LastReply,
            LastError = error
        };
    }

    /// <summary>
    /// Decide whether another orchestrator decision call is allowed for
    /// the current loop. Pure: only depends on the recorded state and
    /// the configured budget.
    /// </summary>
    public static StuckLoopVerdict Decide(StuckLoopState state, StuckLoopBudget budget)
    {
        if (state.IterationCount >= budget.MaxIterations) return StuckLoopVerdict.CircuitBreak;
        if (state.CumulativeOrchestratorTokens >= budget.MaxOrchestratorTokens) return StuckLoopVerdict.CircuitBreak;
        return StuckLoopVerdict.Continue;
    }

    /// <summary>
    /// Human-readable summary used as the meta-message line when the
    /// circuit breaker fires. Includes both ceilings so the user sees
    /// which limit hit.
    /// </summary>
    public static string FormatBreakerMessage(StuckLoopState state, StuckLoopBudget budget)
    {
        // Invariant formatting so the meta line and the regression tests
        // agree across machine locales (test machines have varied: en-US
        // gives "12,345"; de-DE gives "12.345"; invariant always uses "," so
        // the assertion locks the same string everywhere).
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var iters = $"{state.IterationCount}/{budget.MaxIterations}";
        var toks  = $"{state.CumulativeOrchestratorTokens.ToString("N0", ci)}/{budget.MaxOrchestratorTokens.ToString("N0", ci)}";
        return $"[circuit-breaker] Auto-loop reached iteration {iters} and orchestrator tokens {toks}; leaving the question for you.";
    }
}
