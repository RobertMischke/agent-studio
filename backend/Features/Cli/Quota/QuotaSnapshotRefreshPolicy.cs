namespace AgentStudio.Cli;

/// <summary>
/// Pure refresh-result policy for quota snapshots. A failed live probe adds
/// failure metadata to the last-good snapshot instead of replacing usable
/// values with an exception-shaped empty result.
/// </summary>
public static class QuotaSnapshotRefreshPolicy
{
    public static bool Failed(QuotaSnapshot snapshot)
        => !string.IsNullOrWhiteSpace(snapshot.Error);

    public static QuotaSnapshot Apply(
        QuotaSnapshot? previous,
        QuotaSnapshot candidate,
        DateTime observedAtUtc)
    {
        if (!Failed(candidate))
        {
            return candidate with
            {
                Error = null,
                ProbeFailedAt = null,
                CliVersion = candidate.CliVersion ?? previous?.CliVersion
            };
        }

        var error = NormalizeError(candidate.CliType, candidate.Error!);
        var hasLastGood = previous is not null
            && (previous.Windows.Count > 0 || !string.IsNullOrWhiteSpace(previous.Plan));
        if (!hasLastGood)
        {
            return candidate with
            {
                Error = error,
                ProbeFailedAt = observedAtUtc,
                CliVersion = candidate.CliVersion ?? previous?.CliVersion
            };
        }

        return previous! with
        {
            Error = error,
            ProbeFailedAt = observedAtUtc,
            CliVersion = candidate.CliVersion ?? previous!.CliVersion
        };
    }

    private static string NormalizeError(string cliType, string error)
    {
        if (error.Contains("task was canceled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("operation was cancelled", StringComparison.OrdinalIgnoreCase))
        {
            var label = string.Equals(cliType, CliTypes.Codex, StringComparison.OrdinalIgnoreCase)
                ? "Codex /status"
                : string.Equals(cliType, CliTypes.Claude, StringComparison.OrdinalIgnoreCase)
                    ? "Claude /usage"
                    : $"{cliType} quota";
            return $"{label} probe timed out before it produced a result.";
        }

        return error;
    }
}
