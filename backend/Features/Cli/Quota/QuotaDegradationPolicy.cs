namespace AgentStudio.Cli;

/// <summary>
/// Pure decision for "the probe just failed - what should the operator see?".
///
/// AGT-2679: a failed probe used to replace the cached snapshot with a bare
/// error record, so the quota display lost its numbers and rendered the raw
/// exception text ("A task was canceled."). Two things were wrong with that: the
/// operator lost information they already had, and a .NET exception message is
/// not an operator-facing sentence.
///
/// The rule this policy enforces: a failure never destroys a good reading. The
/// last-good windows/plan are carried forward, flagged <see cref="QuotaSnapshot.Stale"/>
/// with the measurement time in <see cref="QuotaSnapshot.LastGoodAt"/>, and the
/// failure is recorded in <see cref="QuotaSnapshot.Error"/> for the tooltip.
/// </summary>
public static class QuotaDegradationPolicy
{
    /// <summary>
    /// Fold a failed probe into the previous snapshot.
    /// </summary>
    /// <param name="previous">Last cached snapshot for this CLI, if any.</param>
    /// <param name="cliType">CLI the probe was for.</param>
    /// <param name="error">Operator-facing failure description (see <see cref="DescribeFailure"/>).</param>
    /// <param name="cliVersion">CLI version observed at probe time, when known.</param>
    /// <param name="nowUtc">Time of the failed probe.</param>
    public static QuotaSnapshot Degrade(
        QuotaSnapshot? previous,
        string cliType,
        string error,
        string? cliVersion,
        DateTime nowUtc)
    {
        // Nothing worth preserving: no prior snapshot, or a prior one that never
        // carried numbers. Report the failure plainly rather than inventing a
        // "stale" marker for data that never existed.
        if (previous == null || previous.Windows.Count == 0)
        {
            return new QuotaSnapshot
            {
                CliType = cliType,
                FetchedAt = nowUtc,
                Error = error,
                CliVersion = cliVersion ?? previous?.CliVersion,
                // A failure right after a ground-truth invalidation must not drop the
                // block and re-open the admission gate (AGT-2064).
                Suspicious = previous?.Suspicious ?? false,
                SuspiciousReason = previous?.Suspicious == true ? previous.SuspiciousReason : null
            };
        }

        return previous with
        {
            CliType = cliType,
            FetchedAt = nowUtc,
            Error = error,
            Stale = true,
            // Chain through an already-stale snapshot so repeated failures keep
            // pointing at the original measurement, not at the previous failure.
            LastGoodAt = previous.LastGoodAt ?? previous.FetchedAt,
            CliVersion = cliVersion ?? previous.CliVersion
        };
    }

    /// <summary>
    /// Turn a probe exception into an operator-facing sentence. Cancellation is
    /// the common case and its stock message ("A task was canceled.") is useless
    /// to a human, so it is named for what it actually is: the probe outran its
    /// budget while driving the CLI's TUI.
    /// </summary>
    public static string DescribeFailure(Exception ex, string cliType, string? cliVersion)
    {
        var version = string.IsNullOrWhiteSpace(cliVersion) ? cliType : cliVersion.Trim();
        return ex is OperationCanceledException
            ? $"Quota probe timed out driving the {cliType} TUI ({version}). "
              + "The CLI may have changed its startup or /status screen."
            : $"Quota probe failed for {cliType} ({version}): {ex.Message}";
    }
}
