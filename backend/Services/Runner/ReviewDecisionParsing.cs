using System.Text.RegularExpressions;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Pure parsing for the review-decision orchestrator's outputs.
///
/// Two grammars live here, both kept static so the rules are
/// unit-testable without spinning up a CLI:
///
/// <list type="bullet">
///   <item>The agent-side <c>[[TASK_NEEDS_INPUT:&lt;reason&gt;]]</c>
///         sentinel that originally landed the job in 4-review.</item>
///   <item>The orchestrator-side
///         <c>[[ORCHESTRATOR_DECISION: action=&lt;reissue|escalate|accept-as-done&gt;; reason=&lt;short&gt;]]</c>
///         response sentinel produced by the fast-model session.</item>
/// </list>
/// </summary>
public static class ReviewDecisionParsing
{
    private static readonly Regex NeedsInputRegex = new(
        @"\[\[TASK_NEEDS_INPUT(?::(?<reason>[^\]]*))?\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NoOpRegex = new(
        @"\[\[TASK_NOOP(?::(?<reason>[^\]]*))?\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BlockedRegex = new(
        @"\[\[TASK_BLOCKED(?::(?<reason>[^\]]*))?\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DoneRegex = new(
        @"\[\[TASK_DONE\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DecisionRegex = new(
        @"\[\[ORCHESTRATOR_DECISION:\s*(?<body>[^\]]+)\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Inspect a job's <c>cli-output.log</c> contents and return the
    /// 1-based line index of the latest unresolved <c>[[TASK_NEEDS_INPUT]]</c>
    /// sentinel, plus its reason. "Unresolved" means no orchestrator,
    /// supervisor, or user line appears after that sentinel; once any of
    /// those streams writes after a NEEDS_INPUT, the decision chain has
    /// been answered (by orchestrator follow-up or by the user) and the
    /// task should not be re-processed.
    /// </summary>
    public static NeedsInputState? FindUnresolvedNeedsInput(string log)
    {
        var hit = FindLatestUnresolvedSentinel(log, NeedsInputRegex);
        return hit == null ? null : new NeedsInputState(hit.Value.LineNumber, hit.Value.Reason);
    }

    /// <summary>
    /// Counterpart to <see cref="FindUnresolvedNeedsInput"/> for the
    /// <c>[[TASK_NOOP]]</c> sentinel. Returns the latest unresolved
    /// occurrence (no <c>[orchestrator]</c>, <c>[supervisor]</c>, or
    /// <c>[user]</c> line written after it) so the review-decision tick
    /// can treat NOOP as a recoverable signal rather than a terminal one.
    /// </summary>
    public static NoOpState? FindUnresolvedNoOp(string log)
    {
        var hit = FindLatestUnresolvedSentinel(log, NoOpRegex);
        return hit == null ? null : new NoOpState(hit.Value.LineNumber, hit.Value.Reason);
    }

    /// <summary>
    /// Counterpart to <see cref="FindUnresolvedNeedsInput"/> for the
    /// <c>[[TASK_BLOCKED]]</c> sentinel. The orchestrator picks these up
    /// from 4-review (StaleProgressArchiver hands BLOCKED jobs from
    /// 3-progress over there once the runner has gone idle); the
    /// review-decision tick then escalates them so the user sees one
    /// "this job needs your attention" intake rather than a quiet card.
    /// </summary>
    public static BlockedState? FindUnresolvedBlocked(string log)
    {
        var hit = FindLatestUnresolvedSentinel(log, BlockedRegex);
        return hit == null ? null : new BlockedState(hit.Value.LineNumber, hit.Value.Reason);
    }

    /// <summary>
    /// Counterpart to <see cref="FindUnresolvedNeedsInput"/> for the
    /// <c>[[TASK_DONE]]</c> terminal sentinel. The multi-aspect
    /// auto-review pipeline runs only when DONE is the latest unresolved
    /// signal: that is, the agent declared the work complete and no
    /// orchestrator/supervisor/user line has been written after it. Once
    /// the orchestrator records its multi-aspect decision the log gets a
    /// follow-up line and this helper stops returning a hit, preventing
    /// duplicate aspect runs across ticks.
    /// </summary>
    public static DoneState? FindUnresolvedDone(string log)
    {
        var hit = FindLatestUnresolvedSentinel(log, DoneRegex);
        return hit == null ? null : new DoneState(hit.Value.LineNumber);
    }

    /// <summary>
    /// True when the job's most recent run produced no terminal task
    /// sentinel at all. The "most recent run" is the slice of the log after
    /// the last orchestrator/supervisor/user follow-up line (the technical
    /// "Runner active state cleared" marker is bookkeeping, not a
    /// follow-up). Returns <c>false</c> when that slice is empty (nothing
    /// new since the last follow-up, so the orchestrator has already acted)
    /// or when it carries any of
    /// <c>[[TASK_DONE]] / [[TASK_NOOP]] / [[TASK_BLOCKED]] / [[TASK_NEEDS_INPUT]]</c>.
    ///
    /// <para>
    /// This is the deterministic-completion contract's "no signal arrived"
    /// detector. A run can land in 4-auto-review with no terminal sentinel -
    /// e.g. it exited 0 with only heuristic "done"-ish prose, or the
    /// terminal classifier force-routed an Unknown / committed-partial
    /// outcome there. Such a run must never be silently accepted as
    /// completed: the review-decision loop reissues it (demanding a
    /// sentinel) until the shared reissue budget is spent, then escalates to
    /// human review. Distinguishing "no sentinel ever" from "a sentinel that
    /// was already resolved on a prior tick" is exactly what the
    /// last-follow-up slice gives us, so an already-handled card is left
    /// untouched.
    /// </para>
    /// </summary>
    public static bool LacksTerminalSentinelInLatestRun(string log)
    {
        if (string.IsNullOrWhiteSpace(log)) return false;
        var lines = log.Split('\n');

        var lastFollowUp = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (LineHasFollowUpStream(lines[i])) lastFollowUp = i;
        }

        var sawContent = false;
        for (var i = lastFollowUp + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (LineIsTechnicalOrchestratorMarker(line)) continue;
            if (NeedsInputRegex.IsMatch(line) || NoOpRegex.IsMatch(line)
                || BlockedRegex.IsMatch(line) || DoneRegex.IsMatch(line))
            {
                // A terminal sentinel lives in the latest run; the typed
                // FindUnresolved* helpers own this case.
                return false;
            }
            sawContent = true;
        }

        return sawContent;
    }

    private static (int LineNumber, string? Reason)? FindLatestUnresolvedSentinel(string log, Regex regex)
    {
        if (string.IsNullOrEmpty(log)) return null;
        var lines = log.Split('\n');
        int? lastAt = null;
        string? reason = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var match = regex.Match(lines[i]);
            if (match.Success)
            {
                lastAt = i;
                var raw = match.Groups["reason"].Success ? match.Groups["reason"].Value.Trim() : null;
                reason = string.IsNullOrWhiteSpace(raw) ? null : raw;
            }
        }
        if (lastAt == null) return null;

        for (int j = lastAt.Value + 1; j < lines.Length; j++)
        {
            if (LineHasFollowUpStream(lines[j])) return null;
        }
        return (lastAt.Value + 1, reason);
    }

    /// <summary>
    /// Returns the last <c>[[ORCHESTRATOR_DECISION]]</c> sentinel parsed
    /// from a model response, or <c>null</c> when none is present or the
    /// action keyword is unknown. Tolerant of field order, whitespace,
    /// and case so a fast-model reply with the sentinel on its own line
    /// or buried in narrative still resolves.
    /// </summary>
    public static OrchestratorDecisionVerdict? ParseDecision(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var matches = DecisionRegex.Matches(output);
        if (matches.Count == 0) return null;
        var last = matches[^1];
        var body = last.Groups["body"].Value;
        var fields = ParseFields(body);
        if (fields == null) return null;

        var actionRaw = fields.GetValueOrDefault("action")?.Trim().ToLowerInvariant();
        var reason = fields.GetValueOrDefault("reason")?.Trim() ?? string.Empty;
        var action = actionRaw switch
        {
            "reissue" => OrchestratorDecisionAction.Reissue,
            "escalate" => OrchestratorDecisionAction.Escalate,
            "accept-as-done" or "accept_as_done" or "accept" => OrchestratorDecisionAction.AcceptAsDone,
            _ => (OrchestratorDecisionAction?)null
        };
        if (action == null) return null;

        return new OrchestratorDecisionVerdict(action.Value, reason);
    }

    private static bool LineHasFollowUpStream(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (LineIsTechnicalOrchestratorMarker(line)) return false;
        // Lines persisted by OrchestratorChatLog look like
        //   [HH:mm:ss.fff] [orchestrator] ...
        // and the runner's own user-input lines use the [user] stream tag.
        return line.Contains("[orchestrator]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[supervisor]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[user]", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LineIsTechnicalOrchestratorMarker(string line)
    {
        // This is written after the job has already moved from
        // 3-progress to 4-auto-review. It is bookkeeping, not an answer
        // to the agent's terminal sentinel. Treating it as a resolution
        // makes every freshly completed job invisible to auto-review.
        return line.Contains("[orchestrator]", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Runner active state cleared:", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string>? ParseFields(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();
            if (key.Length == 0) continue;
            dict[key] = value;
        }
        return dict.Count == 0 ? null : dict;
    }
}

public sealed record NeedsInputState(int LineNumber, string? Reason);

public sealed record NoOpState(int LineNumber, string? Reason);

public sealed record BlockedState(int LineNumber, string? Reason);

public sealed record DoneState(int LineNumber);

public enum OrchestratorDecisionAction
{
    Reissue,
    Escalate,
    AcceptAsDone
}

public sealed record OrchestratorDecisionVerdict(OrchestratorDecisionAction Action, string Reason);
