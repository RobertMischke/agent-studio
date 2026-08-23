using Microsoft.Extensions.Configuration;

namespace AgentStudio.Cli;

/// <summary>
/// Coordinates a bounded, visible <see cref="NpmShimHealer"/> repair pass:
/// applies <see cref="CliRepairCooldownPolicy"/> under a lock so concurrent
/// callers cannot both pass the check-then-act race, then journals every
/// real attempt to <c>logs/cli-repairs.jsonl</c> via <see cref="CliRepairLog"/>
/// so a fix - or a repeated failure - is never silent. A cooldown-suppressed
/// call is not itself journaled (that would flood the journal with one row
/// per job spawn during a sustained outage, defeating the point of the
/// cooldown); instead it returns the last real attempt's outcome verbatim
/// so the caller's diagnostic text still reflects reality instead of a bare
/// "suppressed" placeholder.
///
/// <para>
/// State is process-global and not keyed per call site or per project - two
/// concurrent job spawns racing the same broken <c>claude</c> install must
/// share one cooldown, not get one each - but IS keyed per <paramref
/// name="cli"/> name, so a future <c>gemini</c> (or other) caller through
/// this same gate gets its own cooldown and its own last-outcome instead of
/// suppressing on, or echoing, an unrelated CLI's state.
/// </para>
/// </summary>
public static class CliRepairGate
{
    private sealed class CooldownState
    {
        public DateTime? LastAttemptUtc;
        public HealOutcome? LastOutcome;
    }

    private static readonly object Lock = new();
    private static readonly Dictionary<string, CooldownState> StatesByCli = new(StringComparer.OrdinalIgnoreCase);

    private static CooldownState StateFor(string cli)
    {
        if (!StatesByCli.TryGetValue(cli, out var state))
        {
            state = new CooldownState();
            StatesByCli[cli] = state;
        }
        return state;
    }

    /// <summary>
    /// Runs <paramref name="heal"/> (in production, <see cref="NpmShimHealer.TryHealClaudeAsync"/>)
    /// at most once per <see cref="CliRepairCooldownPolicy.DefaultWindow"/>.
    /// A suppressed call returns the last real attempt's <see cref="HealOutcome"/>
    /// (its <c>Available</c>/<c>PackagePresent</c>/version fields verbatim, its
    /// <c>Error</c> annotated with the cooldown reason) rather than invoking
    /// <paramref name="heal"/> again, so callers keep their existing
    /// pass/fail branching unchanged and still see the real diagnostic.
    /// </summary>
    public static async Task<HealOutcome> TryHealWithCooldownAsync(
        string cli,
        Func<CancellationToken, Task<HealOutcome>> heal,
        IConfiguration configuration,
        ILogger logger,
        DateTime nowUtc,
        CancellationToken ct)
    {
        bool begin;
        HealOutcome? last;
        lock (Lock)
        {
            var state = StateFor(cli);
            begin = CliRepairCooldownPolicy.Decide(state.LastAttemptUtc, nowUtc, CliRepairCooldownPolicy.DefaultWindow)
                == CliRepairCooldownDecision.Allowed;
            if (begin) state.LastAttemptUtc = nowUtc;
            last = state.LastOutcome;
        }

        if (!begin)
        {
            logger.LogDebug("CliRepairGate: {Cli} repair suppressed, an attempt already ran within the cooldown window", cli);
            var suppressedReason = last?.Error is { Length: > 0 }
                ? $"repair suppressed by cooldown window (last attempt's diagnostic: {last.Error})"
                : "repair suppressed by cooldown window (an attempt already ran within the last hour)";
            return last is not null
                ? last with { Error = suppressedReason }
                : new HealOutcome(false, Array.Empty<string>(), suppressedReason);
        }

        HealOutcome outcome;
        try
        {
            outcome = await heal(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // heal() is expected to report failure via HealOutcome, not throw.
            // If it does throw anyway, the cooldown slot this call already
            // claimed must not vanish without a trace - callers still get a
            // failed outcome instead of an unhandled fault from a pre-spawn
            // health check.
            logger.LogError(ex, "CliRepairGate: {Cli} heal delegate threw", cli);
            outcome = new HealOutcome(false, Array.Empty<string>(), $"heal delegate threw: {ex.Message}");
        }

        lock (Lock) { StateFor(cli).LastOutcome = outcome; }

        try
        {
            new CliRepairLog(configuration, logger).Append(new CliRepairRecord
            {
                At = nowUtc,
                Cli = cli,
                PackagePresent = outcome.PackagePresent,
                Actions = outcome.Actions,
                Available = outcome.Available,
                Error = outcome.Error,
                VersionBefore = outcome.VersionBefore,
                VersionAfter = outcome.VersionAfter,
            });
        }
        catch (Exception ex)
        {
            // The journal is observability, not the repair. A failed
            // append must not turn a successful heal into a failed one.
            logger.LogWarning(ex, "CliRepairGate: failed to journal repair outcome for {Cli}", cli);
        }

        if (outcome.Available)
        {
            logger.LogInformation(
                "CliRepairGate: {Cli} repaired at {At:O} ({VersionBefore} -> {VersionAfter}); actions: {Actions}",
                cli, nowUtc, outcome.VersionBefore ?? "unknown", outcome.VersionAfter ?? "unknown",
                outcome.Actions.Count > 0 ? string.Join("; ", outcome.Actions) : "(none needed)");
        }
        else
        {
            logger.LogError(
                "CliRepairGate: {Cli} repair FAILED at {At:O} ({VersionBefore} -> {VersionAfter}): {Error}",
                cli, nowUtc, outcome.VersionBefore ?? "unknown", outcome.VersionAfter ?? "unknown", outcome.Error);
        }

        return outcome;
    }

    /// <summary>Test seam: clear cooldown state between test cases.</summary>
    internal static void ResetForTests()
    {
        lock (Lock) { StatesByCli.Clear(); }
    }
}
