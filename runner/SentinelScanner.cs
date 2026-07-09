using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>The terminal outcome an agent signs its run off with.</summary>
public enum RunOutcomeKind { Done, Blocked, NeedsInput, NoOp, Unknown }

public sealed record RunOutcome(RunOutcomeKind Kind, string? Reason)
{
    /// <summary>Lane the completion should target, matching the server's TaskStates values.</summary>
    public string TargetState => Kind switch
    {
        // Even a Done remote run gets a quick human confirmation on the board,
        // matching the external-completion default. Non-Done outcomes are honest
        // about needing attention.
        RunOutcomeKind.Done => "5-human-review",
        _ => "5-human-review",
    };

    public string SummaryPrefix => Kind switch
    {
        RunOutcomeKind.Done => "Remote run completed",
        RunOutcomeKind.Blocked => "Remote run blocked",
        RunOutcomeKind.NeedsInput => "Remote run needs input",
        RunOutcomeKind.NoOp => "Remote run was a no-op",
        _ => "Remote run ended without a terminal sentinel",
    };
}

/// <summary>
/// Recognises the canonical terminal sentinel the agent emits
/// (<c>[[TASK_DONE]]</c> / <c>[[TASK_BLOCKED:reason]]</c> / ...), mirroring the
/// server's authoritative <c>AgentOutcomeAnalyzer.SentinelRegex</c>. The last
/// match in the output wins, matching server semantics.
/// </summary>
public static class SentinelScanner
{
    private static readonly Regex Sentinel = new(
        @"\[\[\s*TASK[\s_-]*(?<keyword>DONE|BLOCKED|NEEDS[\s_-]*INPUT|NOOP)\s*(?::\s*(?<reason>[^\]]*?))?\s*\]\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static RunOutcome Scan(string text)
    {
        if (string.IsNullOrEmpty(text)) return new RunOutcome(RunOutcomeKind.Unknown, null);

        Match? last = null;
        foreach (Match m in Sentinel.Matches(text)) last = m;
        if (last is null) return new RunOutcome(RunOutcomeKind.Unknown, null);

        var keyword = Regex.Replace(last.Groups["keyword"].Value, @"[\s_-]+", "_").ToUpperInvariant();
        var reason = last.Groups["reason"].Success && last.Groups["reason"].Value.Length > 0
            ? last.Groups["reason"].Value.Trim()
            : null;

        var kind = keyword switch
        {
            "DONE" => RunOutcomeKind.Done,
            "BLOCKED" => RunOutcomeKind.Blocked,
            "NEEDS_INPUT" => RunOutcomeKind.NeedsInput,
            "NOOP" => RunOutcomeKind.NoOp,
            _ => RunOutcomeKind.Unknown,
        };
        return new RunOutcome(kind, reason);
    }
}
