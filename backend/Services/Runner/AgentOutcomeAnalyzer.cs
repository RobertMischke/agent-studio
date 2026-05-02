using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Typed classification of how a single CLI run ended. Produced by
/// <see cref="AgentOutcomeAnalyzer.Analyze"/> from the run's exit status,
/// duration, and output buffer. Consumed by <see cref="RunOutcomePolicy"/>
/// to decide whether the orchestrator should accept the agent's report,
/// re-issue with stronger framing, or surface a meta message to the user.
/// </summary>
public enum AgentOutcomeKind
{
    /// <summary>Agent reports the task is complete.</summary>
    Done,
    /// <summary>Agent reports it cannot proceed.</summary>
    Blocked,
    /// <summary>Agent is waiting for user input or asking a question.</summary>
    NeedsInput,
    /// <summary>Agent is mid-task (run was cut short while still working).</summary>
    Progress,
    /// <summary>The CLI exited without producing user-visible work (no agent text, very short duration).</summary>
    NoOp,
    /// <summary>Could not classify - no sentinel match and no clear heuristic signal.</summary>
    Unknown
}

/// <summary>
/// Deterministic, side-effect-free description of how a CLI run ended.
/// <see cref="MatchedSentinel"/> is the load-bearing flag: when it is true
/// the orchestrator treats the result as authoritative; when it is false
/// the orchestrator falls back to heuristics and is required to surface a
/// warning so the user can see that the deterministic contract did not match.
/// </summary>
public sealed record AgentOutcome(
    AgentOutcomeKind Kind,
    string? Summary,
    bool MatchedSentinel,
    string? SentinelKeyword,
    string? Reason,
    int AgentTextChars,
    int OutputLineCount,
    double DurationSeconds);

/// <summary>
/// Pure analyzer that turns a finished CLI run into an <see cref="AgentOutcome"/>.
///
/// <para>
/// <b>Why this exists.</b> The product previously relied on prompt wording
/// to steer recovery and continuation behavior, then trusted whatever the
/// agent said back. When the agent silently no-op'd a follow-up after a
/// session loss (4.6 s exit, no real work, "task done"), the orchestrator
/// had no way to disagree. The analyzer pulls that decision into hardcoded
/// signal extraction so post-run policy can react deterministically.
/// </para>
///
/// <para>
/// <b>Signal hierarchy.</b>
/// <list type="number">
///   <item>Hard sentinels: bracket-tagged tokens the agent contract asks for
///   (<c>[[TASK_DONE]]</c>, <c>[[TASK_BLOCKED:&lt;reason&gt;]]</c>,
///   <c>[[TASK_NEEDS_INPUT:&lt;reason&gt;]]</c>, <c>[[TASK_NOOP]]</c>).
///   These are authoritative. The agent contract is documented in
///   <c>docs/agent-task-contract.md</c>.</item>
///   <item>Structural no-op: empty output buffer or no agent text plus a
///   sub-threshold duration. The CLI exited cleanly but produced nothing
///   the user can review.</item>
///   <item>Heuristic regex: same shape as the frontend's
///   <c>agent-outcome.util.ts</c>. Used as a fallback so we never return
///   <see cref="AgentOutcomeKind.Unknown"/> when the text is informative.
///   Fallback matches must set <see cref="AgentOutcome.MatchedSentinel"/>
///   to false so the policy layer can warn.</item>
/// </list>
/// </para>
/// </summary>
public static class AgentOutcomeAnalyzer
{
    /// <summary>Sub-threshold duration below which a run with no agent text is treated as no-op.</summary>
    public const double NoOpDurationThresholdSeconds = 10.0;

    /// <summary>
    /// Analyze a completed run. <paramref name="lines"/> is the full output
    /// buffer (the same shape <c>cli-output.log</c> persists). Status is the
    /// final <see cref="CliExecution.Status"/>; duration is the wall-clock
    /// run time in seconds.
    /// </summary>
    public static AgentOutcome Analyze(
        IReadOnlyList<CliOutputLine> lines,
        string status,
        double durationSeconds)
    {
        lines ??= Array.Empty<CliOutputLine>();
        var agentText = JoinAgentText(lines);
        var lineCount = lines.Count;

        // 1) Hard sentinels - authoritative. Walk from the end so a final
        //    sentinel beats earlier transient ones.
        var sentinel = FindLastSentinel(agentText);
        if (sentinel != null)
        {
            return new AgentOutcome(
                Kind: sentinel.Value.Kind,
                Summary: sentinel.Value.Summary,
                MatchedSentinel: true,
                SentinelKeyword: sentinel.Value.Keyword,
                Reason: sentinel.Value.Reason,
                AgentTextChars: agentText.Length,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds);
        }

        // 2) Structural no-op - the CLI exited cleanly without producing
        //    anything the user can review. Subscriber-side proof the agent
        //    didn't actually attempt the task.
        var failed = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);
        if (!failed && agentText.Length == 0 && durationSeconds < NoOpDurationThresholdSeconds)
        {
            return new AgentOutcome(
                Kind: AgentOutcomeKind.NoOp,
                Summary: "CLI exited without producing agent output.",
                MatchedSentinel: false,
                SentinelKeyword: null,
                Reason: $"duration {durationSeconds:F1}s, no agent text",
                AgentTextChars: 0,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds);
        }

        // 3) Heuristic regex fallback over the tail of the agent text. Mirrors
        //    the frontend's classifier so the orchestrator and the UI agree
        //    on what "done" / "blocked" / "needs-input" mean today.
        var (heuristicKind, heuristicSummary) = HeuristicClassify(agentText);
        return new AgentOutcome(
            Kind: heuristicKind,
            Summary: heuristicSummary,
            MatchedSentinel: false,
            SentinelKeyword: null,
            Reason: heuristicKind == AgentOutcomeKind.Unknown
                ? "no sentinel matched, heuristic also inconclusive"
                : "no sentinel matched, heuristic fallback",
            AgentTextChars: agentText.Length,
            OutputLineCount: lineCount,
            DurationSeconds: durationSeconds);
    }

    // Sentinel format: [[TASK_<KEYWORD>]] or [[TASK_<KEYWORD>:reason text]].
    // Kept loose on whitespace and case so a model that emits the marker on
    // its own line, indented, or in mixed case still matches. The actual
    // keyword set is small and explicit.
    private static readonly Regex SentinelRegex = new(
        @"\[\[TASK_(?<keyword>DONE|BLOCKED|NEEDS_INPUT|NOOP)(?::(?<reason>[^\]]*))?\]\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (AgentOutcomeKind Kind, string Keyword, string? Reason, string Summary)? FindLastSentinel(string agentText)
    {
        if (string.IsNullOrEmpty(agentText)) return null;
        var matches = SentinelRegex.Matches(agentText);
        if (matches.Count == 0) return null;
        var last = matches[^1];
        var keyword = last.Groups["keyword"].Value.ToUpperInvariant();
        var reason = last.Groups["reason"].Success ? last.Groups["reason"].Value.Trim() : null;
        if (string.IsNullOrWhiteSpace(reason)) reason = null;
        return keyword switch
        {
            "DONE"        => (AgentOutcomeKind.Done, keyword, reason, "Agent emitted [[TASK_DONE]]."),
            "BLOCKED"     => (AgentOutcomeKind.Blocked, keyword, reason, $"Agent emitted [[TASK_BLOCKED]]{(reason != null ? $": {reason}" : "")}."),
            "NEEDS_INPUT" => (AgentOutcomeKind.NeedsInput, keyword, reason, $"Agent emitted [[TASK_NEEDS_INPUT]]{(reason != null ? $": {reason}" : "")}."),
            "NOOP"        => (AgentOutcomeKind.NoOp, keyword, reason, "Agent emitted [[TASK_NOOP]]."),
            _             => null
        };
    }

    private static readonly Regex DonePattern = new(
        @"\b(committ?ed|merged|landed|shipped|deployed|fixed|resolved|implemented|completed|finished|done|ready for review)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BlockedPattern = new(
        @"\b(cannot\s+(?:proceed|continue|find|access|determine)|blocked\s+by|unable\s+to|do(?:\s+not|n'?t)\s+have\s+(?:access|permission))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NeedsInputPattern = new(
        @"\b(?:please\s+(?:provide|share|paste|attach|specify|clarify)|which\s+(?:one|file|option)|do\s+you\s+want|should\s+I|would\s+you\s+like|what\s+would\s+you\s+like|i'?ll\s+wait\s+for)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProgressPattern = new(
        @"\b(starting|working|investigating|reading|searching|exploring|analy[sz]ing|building|running)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (AgentOutcomeKind Kind, string Summary) HeuristicClassify(string agentText)
    {
        if (string.IsNullOrWhiteSpace(agentText))
            return (AgentOutcomeKind.Unknown, "No agent text to classify.");

        var tail = TailLines(agentText, 6);
        var endsWithQuestion = tail.TrimEnd().EndsWith("?", StringComparison.Ordinal);
        if (endsWithQuestion || NeedsInputPattern.IsMatch(tail))
            return (AgentOutcomeKind.NeedsInput, "Agent appears to be waiting for input (heuristic).");
        if (DonePattern.IsMatch(TailLines(agentText, 2)))
            return (AgentOutcomeKind.Done, "Agent text suggests the task is done (heuristic).");
        if (BlockedPattern.IsMatch(tail))
            return (AgentOutcomeKind.Blocked, "Agent text suggests the task is blocked (heuristic).");
        if (ProgressPattern.IsMatch(tail))
            return (AgentOutcomeKind.Progress, "Agent text suggests it is mid-task (heuristic).");
        return (AgentOutcomeKind.Unknown, "Agent text did not match any known shape.");
    }

    /// <summary>
    /// Joins the parts of the buffer that look like agent (assistant) text.
    /// We exclude lines from the <c>system</c> stream (taskboard markers and
    /// orchestrator meta messages) and the <c>user</c> stream (the user's own
    /// follow-ups echoed into the log) so the analysis only sees what the
    /// agent itself produced.
    /// </summary>
    private static string JoinAgentText(IReadOnlyList<CliOutputLine> lines)
    {
        var parts = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (line == null) continue;
            var stream = line.Stream ?? string.Empty;
            if (string.Equals(stream, "system", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(stream, "user", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(stream, "orchestrator", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(line.Text)) parts.Add(line.Text);
        }
        return string.Join("\n", parts).Trim();
    }

    private static string TailLines(string text, int count)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var lines = text.Split('\n');
        var startIndex = Math.Max(0, lines.Length - count);
        return string.Join("\n", lines, startIndex, lines.Length - startIndex);
    }
}
