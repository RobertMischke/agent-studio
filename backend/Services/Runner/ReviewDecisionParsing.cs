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
        if (string.IsNullOrEmpty(log)) return null;
        var lines = log.Split('\n');
        int? lastNeedsAt = null;
        string? reason = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var match = NeedsInputRegex.Match(lines[i]);
            if (match.Success)
            {
                lastNeedsAt = i;
                var raw = match.Groups["reason"].Success ? match.Groups["reason"].Value.Trim() : null;
                reason = string.IsNullOrWhiteSpace(raw) ? null : raw;
            }
        }
        if (lastNeedsAt == null) return null;

        for (int j = lastNeedsAt.Value + 1; j < lines.Length; j++)
        {
            var line = lines[j];
            if (LineHasFollowUpStream(line)) return null;
        }
        return new NeedsInputState(lastNeedsAt.Value + 1, reason);
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
        // Lines persisted by OrchestratorChatLog look like
        //   [HH:mm:ss.fff] [orchestrator] ...
        // and the runner's own user-input lines use the [user] stream tag.
        return line.Contains("[orchestrator]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[supervisor]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[user]", StringComparison.OrdinalIgnoreCase);
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

public enum OrchestratorDecisionAction
{
    Reissue,
    Escalate,
    AcceptAsDone
}

public sealed record OrchestratorDecisionVerdict(OrchestratorDecisionAction Action, string Reason);
