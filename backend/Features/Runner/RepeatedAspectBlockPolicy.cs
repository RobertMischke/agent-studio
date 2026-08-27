using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

public sealed record RepeatedAspectBlockDiagnosis(
    string Fingerprint,
    string Finding,
    int ConsecutiveRounds,
    int MaximumRounds,
    IReadOnlyList<string>? CurrentFingerprints = null)
{
    public bool MustEscalate => ConsecutiveRounds >= MaximumRounds;
}

/// <summary>
/// Detects one semantic aspect returning the same blocking reason across
/// consecutive review rounds. It reads only structured decision fields in the
/// active operator epoch; prose and old epochs never replenish or contaminate
/// the bounded retry chain.
/// </summary>
public static partial class RepeatedAspectBlockPolicy
{
    public const string FailureKind = "aspect-block";

    public static RepeatedAspectBlockDiagnosis? Diagnose(
        AspectRunReport report,
        IEnumerable<ReviewDecisionRecord> records,
        string jobId,
        int attemptEpoch,
        int maximumRounds)
    {
        var blocked = report.Verdicts
            .Where(verdict => verdict.Status == AspectStatus.Block)
            .OrderBy(verdict => verdict.Aspect, StringComparer.OrdinalIgnoreCase)
            .Select(verdict =>
            {
                var finding = $"{verdict.Aspect}: {verdict.Summary.Trim()}";
                return (Fingerprint: Fingerprint(finding), Finding: finding);
            })
            .ToList();
        if (blocked.Count == 0) return null;

        var relevantRecords = records
            .Where(record => record.JobId == jobId
                             && ReviewDecisionOrchestrator.IsInAttemptEpoch(record, attemptEpoch))
            .Reverse()
            .ToList();
        var candidates = new List<(string Fingerprint, string Finding, int Rounds)>();
        foreach (var block in blocked)
        {
            var consecutivePrior = 0;
            foreach (var record in relevantRecords)
            {
                if (record.Kind == ReviewDecisionKind.Skipped) continue;
                var fingerprints = record.FailureFingerprints ?? [];
                var matches = fingerprints.Contains(block.Fingerprint, StringComparer.Ordinal)
                              || string.Equals(
                                  record.FailureFingerprint,
                                  block.Fingerprint,
                                  StringComparison.Ordinal);
                if (record.Kind != ReviewDecisionKind.Reissue
                    || !string.Equals(record.FailureKind, FailureKind, StringComparison.Ordinal)
                    || !matches)
                    break;
                consecutivePrior++;
            }
            candidates.Add((block.Fingerprint, block.Finding, consecutivePrior + 1));
        }

        var selected = candidates
            .OrderByDescending(candidate => candidate.Rounds)
            .ThenBy(candidate => candidate.Finding, StringComparer.OrdinalIgnoreCase)
            .First();
        return new RepeatedAspectBlockDiagnosis(
            selected.Fingerprint,
            selected.Finding,
            selected.Rounds,
            Math.Max(1, maximumRounds),
            blocked.Select(block => block.Fingerprint).ToList());
    }

    internal static string Fingerprint(string value)
    {
        var normalized = Whitespace().Replace(value.Trim().ToLowerInvariant(), " ");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
