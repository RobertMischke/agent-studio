using System.Globalization;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

public static class TerminalRunOutcomeKinds
{
    public const string Success = "success";
    public const string Failed = "failed";
    public const string NoOp = "noop";
    public const string Blocked = "blocked";
    public const string NeedsInput = "needs-input";
    public const string Interrupted = "interrupted";
    public const string Unknown = "unknown";

    /// <summary>
    /// A run that committed real work but then exited non-zero without a
    /// terminal sentinel (classically a watchdog-killed downstream test run,
    /// which on Windows surfaces as exitCode=-1). Honest middle ground: not a
    /// hard failure (the commit is real), not a clean success (no sentinel).
    /// Routes to review with a "Partial" verdict instead of re-looping the card.
    /// </summary>
    public const string CommittedPartial = "committed-partial";
}

/// <summary>
/// Single post-run classification consumed by lane routing, protocol summary,
/// and UI failure surfacing. It is derived from the deterministic sentinel
/// analyzer first, and only then from the process status.
/// </summary>
public sealed record TerminalRunOutcome(
    string Kind,
    string ProtocolResult,
    bool ShouldMoveToReview,
    bool ShouldShowFailureToast,
    string Reason);

public static class TerminalRunOutcomeClassifier
{
    private static readonly Regex RenderedLogLineRegex = new(
        @"^\[(?<time>[^\]]+)\]\s+\[(?<stream>[^\]]+)\]\s?(?<text>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex ExitLineRegex = new(
        @"\[taskboard\]\s+\S+\s+CLI\s+exited:\s*status=(?<status>\w+)(?:,\s*exitCode=(?<code>-?\d+|\?))?(?:,\s*duration=(?<duration>[\d.,]+)s)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static TerminalRunOutcome Classify(string? executionStatus, AgentOutcome agentOutcome, int commitsDuringRun = 0)
    {
        if (agentOutcome.MatchedSentinel)
        {
            return agentOutcome.Kind switch
            {
                AgentOutcomeKind.Done => new(
                    TerminalRunOutcomeKinds.Success,
                    "Success",
                    ShouldMoveToReview: true,
                    ShouldShowFailureToast: false,
                    Reason: "agent emitted TASK_DONE"),
                AgentOutcomeKind.NoOp => new(
                    TerminalRunOutcomeKinds.NoOp,
                    "NoOp",
                    ShouldMoveToReview: true,
                    ShouldShowFailureToast: false,
                    Reason: "agent emitted TASK_NOOP"),
                AgentOutcomeKind.Blocked => new(
                    TerminalRunOutcomeKinds.Blocked,
                    "Blocked",
                    ShouldMoveToReview: true,
                    ShouldShowFailureToast: false,
                    Reason: "agent emitted TASK_BLOCKED"),
                AgentOutcomeKind.NeedsInput => new(
                    TerminalRunOutcomeKinds.NeedsInput,
                    "NeedsInput",
                    ShouldMoveToReview: true,
                    ShouldShowFailureToast: false,
                    Reason: "agent emitted TASK_NEEDS_INPUT"),
                _ => UnknownTerminal("matched unknown sentinel")
            };
        }

        if (string.Equals(executionStatus, RunStatuses.Failed, StringComparison.OrdinalIgnoreCase))
        {
            // Reaching here means no terminal sentinel matched. If the run still
            // committed real work, the non-zero exit is almost always a killed
            // downstream step (e.g. the watchdog terminating a post-commit test
            // run, which on Windows reports exitCode=-1) rather than a genuine
            // crash. Treat it as an honest "partial": route to review so a human
            // sees it, but do not hard-fail, do not show a crash toast, and do
            // not feed the auto-failure circuit breaker that flips mode to manual.
            if (commitsDuringRun > 0)
            {
                return new TerminalRunOutcome(
                    TerminalRunOutcomeKinds.CommittedPartial,
                    "Partial",
                    ShouldMoveToReview: true,
                    ShouldShowFailureToast: false,
                    Reason: $"process exited without a terminal sentinel but committed {commitsDuringRun} change(s) during the run; routing to review instead of hard-failing");
            }

            return new TerminalRunOutcome(
                TerminalRunOutcomeKinds.Failed,
                "Failed",
                ShouldMoveToReview: false,
                ShouldShowFailureToast: true,
                Reason: agentOutcome.Reason ?? "process failed without terminal sentinel");
        }

        if (string.Equals(executionStatus, RunStatuses.Stopped, StringComparison.OrdinalIgnoreCase)
            || string.Equals(executionStatus, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return new TerminalRunOutcome(
                TerminalRunOutcomeKinds.Interrupted,
                "Failed",
                ShouldMoveToReview: false,
                ShouldShowFailureToast: false,
                Reason: agentOutcome.Reason ?? "process was deliberately stopped");
        }

        if (string.Equals(executionStatus, RunStatuses.Completed, StringComparison.OrdinalIgnoreCase))
        {
            return agentOutcome.Kind switch
            {
                AgentOutcomeKind.Done => new(TerminalRunOutcomeKinds.Success, "Success", true, false, agentOutcome.Reason ?? "completed"),
                AgentOutcomeKind.NoOp => new(TerminalRunOutcomeKinds.NoOp, "NoOp", true, false, agentOutcome.Reason ?? "no-op"),
                AgentOutcomeKind.Blocked => new(TerminalRunOutcomeKinds.Blocked, "Blocked", true, false, agentOutcome.Reason ?? "blocked"),
                AgentOutcomeKind.NeedsInput => new(TerminalRunOutcomeKinds.NeedsInput, "NeedsInput", true, false, agentOutcome.Reason ?? "needs input"),
                _ => new(TerminalRunOutcomeKinds.Unknown, "Partial", true, false, agentOutcome.Reason ?? "completed but unclassified")
            };
        }

        return UnknownTerminal(agentOutcome.Reason ?? "unknown execution status");
    }

    public static TerminalRunOutcome Classify(string? executionStatus, IReadOnlyList<CliOutputLine> lines, double durationSeconds, int commitsDuringRun = 0)
    {
        var analyzed = AgentOutcomeAnalyzer.Analyze(lines, executionStatus ?? string.Empty, durationSeconds);
        return Classify(executionStatus, analyzed, commitsDuringRun);
    }

    public static (TerminalRunOutcome Outcome, AgentOutcome AgentOutcome)? TryClassifyRenderedLog(string rawLog)
    {
        if (string.IsNullOrWhiteSpace(rawLog)) return null;

        var lines = new List<CliOutputLine>();
        string? status = null;
        var durationSeconds = 0.0;

        foreach (var rawLine in rawLog.Split('\n'))
        {
            var trimmedEnd = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmedEnd)) continue;
            var match = RenderedLogLineRegex.Match(trimmedEnd);
            var stream = match.Success ? match.Groups["stream"].Value : "stdout";
            var text = match.Success ? match.Groups["text"].Value : trimmedEnd;
            lines.Add(new CliOutputLine
            {
                Timestamp = DateTime.UtcNow,
                Stream = stream,
                Text = text
            });

            var exit = ExitLineRegex.Match(text);
            if (!exit.Success) continue;
            status = exit.Groups["status"].Value;
            if (exit.Groups["duration"].Success)
            {
                var rawDuration = exit.Groups["duration"].Value.Replace(',', '.');
                if (double.TryParse(rawDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    durationSeconds = parsed;
            }
        }

        if (lines.Count == 0) return null;
        var analyzed = AgentOutcomeAnalyzer.Analyze(lines, status ?? string.Empty, durationSeconds);
        return (Classify(status, analyzed), analyzed);
    }

    public static string ExecutionStatusFor(TerminalRunOutcome outcome, string currentStatus)
    {
        if (outcome.ShouldMoveToReview) return RunStatuses.Completed;
        return currentStatus;
    }

    private static TerminalRunOutcome UnknownTerminal(string reason) => new(
        TerminalRunOutcomeKinds.Unknown,
        "Partial",
        ShouldMoveToReview: false,
        ShouldShowFailureToast: false,
        Reason: reason);
}
