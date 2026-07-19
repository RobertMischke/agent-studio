using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// What the post-abort review (the "Abbruch-Review" LLM step) recommends
/// doing after a non-clean CLI run end (watchdog timeout, non-zero exit,
/// unexpected stop). This is the agent's <em>opinion</em>; the rule engine
/// (<see cref="PostAbortReviewDecider"/>) owns the final decision per ADR-0032
/// ("agent classifies, rule engine decides").
/// </summary>
public enum PostAbortRecommendation
{
    /// <summary>
    /// Re-run the same intent. The abort was not a real failure - e.g. a
    /// legitimate long-running op (ng serve / build / test-server wait /
    /// poll-loop) tripped the silence watchdog while still alive.
    /// </summary>
    Rerun,

    /// <summary>
    /// Re-run, but with a sharper framing. The run was drifting, looping, or
    /// mis-reading the task; a plain re-run would likely repeat the problem.
    /// </summary>
    StrongerReissue,

    /// <summary>
    /// Stop and route to human review. The abort looks legitimate or is
    /// unrecoverable by another automated pass.
    /// </summary>
    HumanReview,

    /// <summary>
    /// Accept the run as-is and let the pipeline continue. Enough useful work
    /// landed (commits / diff) that re-running would be churn.
    /// </summary>
    Accept,
}

/// <summary>
/// Structured verdict the abort-review LLM emits, parsed by
/// <see cref="PostAbortReviewVerdictParsing"/>. Mirrors the four fields the
/// feature spec mandates: <c>legitimer_abbruch</c>, <c>empfehlung</c>,
/// <c>begruendung</c>, <c>confidence</c>.
/// </summary>
/// <param name="LegitimateAbort">
/// True when the model judged the abort a genuine dead end (the run should
/// not simply be re-run). Informational for the operator; the decider keys
/// off <see cref="Recommendation"/>, not this flag.
/// </param>
/// <param name="Recommendation">The model's recommended next action.</param>
/// <param name="Reasoning">One short human-readable justification.</param>
/// <param name="Confidence">Model self-rated confidence in [0, 1].</param>
public sealed record PostAbortReviewVerdict(
    bool LegitimateAbort,
    PostAbortRecommendation Recommendation,
    string Reasoning,
    double Confidence);

/// <summary>
/// Pure parser for the abort-review reply. Prefers the canonical
/// <c>[[ABORT_REVIEW: ...]]</c> sentinel; falls back to tolerant per-line
/// scanning when the model drops the sentinel. Returns null when no verdict
/// can be recovered so the caller fails closed (escalate to human) rather
/// than silently waving the run through - the same no-silent-pass discipline
/// <see cref="AspectVerdictParsing"/> follows.
/// </summary>
public static class PostAbortReviewVerdictParsing
{
    // Canonical sentinel. `.+?` lazy + Singleline so a reason that wraps or
    // contains a single `]` still parses; the doubled-bracket terminator
    // remains the anchor.
    private static readonly Regex SentinelRegex = new(
        @"\[\[ABORT_REVIEW:\s*(?<body>.+?)\s*\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Tolerant fallback: a model that drops the sentinel often still writes
    // "recommendation: rerun" on its own line.
    private static readonly Regex LineRecommendationRegex = new(
        @"^\s*\**\s*(?:recommendation|empfehlung)\s*\**\s*[:=]\s*\**\s*[`'""]?(?<rec>rerun|retry|stronger[-_ ]?reissue|stronger|reissue|human[-_ ]?review|human|escalate|accept|accept[-_ ]?as[-_ ]?done)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Parse a reply for the last <c>[[ABORT_REVIEW]]</c> sentinel, then a
    /// tolerant line fallback. Returns null when neither yields a recognised
    /// recommendation token.
    /// </summary>
    public static PostAbortReviewVerdict? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var cleaned = StripFences(output);

        var matches = SentinelRegex.Matches(cleaned);
        if (matches.Count > 0)
        {
            var fields = ParseFields(matches[^1].Groups["body"].Value);
            if (fields != null)
            {
                var rec = ParseRecommendation(fields.GetValueOrDefault("recommendation")
                                              ?? fields.GetValueOrDefault("empfehlung"));
                if (rec != null)
                {
                    var legitimate = ParseBool(fields.GetValueOrDefault("legitimate")
                                               ?? fields.GetValueOrDefault("legitimer_abbruch")
                                               ?? fields.GetValueOrDefault("legitimate_abort"));
                    var confidence = ParseConfidence(fields.GetValueOrDefault("confidence"));
                    var reason = (fields.GetValueOrDefault("reason")
                                  ?? fields.GetValueOrDefault("begruendung")
                                  ?? string.Empty).Trim();
                    return new PostAbortReviewVerdict(legitimate, rec.Value, reason, confidence);
                }
            }
        }

        // Fallback: scan for a "recommendation: <token>" line (last wins).
        var lineMatches = LineRecommendationRegex.Matches(cleaned);
        if (lineMatches.Count > 0)
        {
            var rec = ParseRecommendation(lineMatches[^1].Groups["rec"].Value);
            if (rec != null)
            {
                var legitimate = rec.Value is PostAbortRecommendation.HumanReview;
                return new PostAbortReviewVerdict(
                    legitimate,
                    rec.Value,
                    ExtractFallbackReason(cleaned),
                    ParseConfidence(null));
            }
        }

        return null;
    }

    private static PostAbortRecommendation? ParseRecommendation(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var t = Normalize(token);
        return t switch
        {
            "rerun" or "retry" => PostAbortRecommendation.Rerun,
            "strongerreissue" or "stronger" or "reissue" => PostAbortRecommendation.StrongerReissue,
            "humanreview" or "human" or "escalate" => PostAbortRecommendation.HumanReview,
            "accept" or "acceptasdone" or "done" => PostAbortRecommendation.Accept,
            _ => null,
        };
    }

    // Lowercase, strip surrounding quotes/backticks, and drop separators so
    // "stronger-reissue", "stronger_reissue", and "stronger reissue" collapse
    // to one token.
    private static string Normalize(string token)
    {
        var t = token.Trim().Trim('"', '\'', '`').ToLowerInvariant();
        return t.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
    }

    private static bool ParseBool(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var t = raw.Trim().Trim('"', '\'', '`').ToLowerInvariant();
        return t is "true" or "yes" or "1" or "ja";
    }

    // Confidence clamps to [0, 1]; an absent / unparseable value defaults to
    // 0.5 (neither a strong nor a weak signal). Accepts "0.8", "80%", "80".
    private static double ParseConfidence(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0.5;
        var hadPercent = raw.Contains('%');
        var t = raw.Trim().Trim('"', '\'', '`', '%');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0.5;
        // A bare "80" or "80%" is a percentage; an over-confident fraction
        // like "1.5" should clamp to 1, not be read as 1.5%. Only divide by
        // 100 when a percent sign was present or the value is clearly out of
        // fraction range (>= 2).
        if (hadPercent || value >= 2.0) value /= 100.0;
        return Math.Clamp(value, 0.0, 1.0);
    }

    private static string ExtractFallbackReason(string cleaned)
    {
        var explicitReason = Regex.Match(
            cleaned,
            @"^\s*(?:\*\*)?(?:reason|begruendung)(?:\*\*)?\s*[:=]\s*(?<text>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (explicitReason.Success)
            return Cap(explicitReason.Groups["text"].Value.Trim().Trim('"', '\''), 240);

        var paragraphs = cleaned.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("---"))
            .ToList();
        return paragraphs.Count == 0 ? string.Empty : Cap(paragraphs[^1], 240);
    }

    private static string Cap(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";

    private static string StripFences(string raw)
    {
        var withoutOpen = Regex.Replace(raw, "```[a-zA-Z0-9_-]*\\r?\\n?", string.Empty);
        return Regex.Replace(withoutOpen, "```", string.Empty);
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

/// <summary>
/// The concrete action the orchestrator takes after the abort review. This is
/// the rule-engine output (ADR-0032 decider zone): code, not the model, owns
/// the escalate-vs-rerun call so a confidently wrong model cannot spin the
/// pipeline past its budget.
/// </summary>
public enum PostAbortAction
{
    /// <summary>Re-issue the same run intent.</summary>
    Rerun,

    /// <summary>Re-issue with a stronger / sharper framing prompt.</summary>
    RerunWithStrongerFraming,

    /// <summary>Stop the automated path and route to human review.</summary>
    EscalateHuman,

    /// <summary>Accept the run outcome and let the pipeline continue.</summary>
    AcceptAndContinue,
}

/// <summary>
/// Pure decider that turns a parsed <see cref="PostAbortReviewVerdict"/> plus
/// the remaining rerun budget into a <see cref="PostAbortAction"/>. This is
/// the "smart hard rules" core of the feature: the orchestrator consumes this
/// verdict instead of the fixed terminal path that
/// <see cref="RunOutcomePolicy"/> takes for a watchdog timeout today.
///
/// <para>
/// <b>Rules.</b> Re-run up to N times (the per-job budget); escalate to human
/// review only when the model recommends it <em>or</em> the budget is
/// exhausted. Fail closed: a null verdict (no parseable reply / CLI failure)
/// escalates rather than guessing. <c>Accept</c> is honoured regardless of
/// budget - it does not consume a rerun.
/// </para>
/// </summary>
public static class PostAbortReviewDecider
{
    /// <summary>
    /// Default per-job rerun budget. Bounds the abort-review rerun loop so a
    /// misbehaving run cannot be re-issued forever; mirrored in
    /// <c>docs/system/contracts/loop-inventory.md</c> (<c>abort-review.rerun-per-job</c>) and
    /// asserted by <c>AbortReviewRerunBreakerTest</c>.
    /// </summary>
    public const int DefaultRerunBudget = 2;

    /// <summary>
    /// Decide the next action.
    /// </summary>
    /// <param name="verdict">Parsed model verdict, or null when unrecoverable.</param>
    /// <param name="rerunBudgetRemaining">
    /// How many automatic reruns this job has left. Zero or negative means the
    /// budget is spent and any rerun recommendation escalates instead.
    /// </param>
    public static PostAbortAction Decide(PostAbortReviewVerdict? verdict, int rerunBudgetRemaining)
    {
        // Fail closed: no trustworthy verdict -> human.
        if (verdict is null) return PostAbortAction.EscalateHuman;

        return verdict.Recommendation switch
        {
            PostAbortRecommendation.Accept => PostAbortAction.AcceptAndContinue,
            PostAbortRecommendation.HumanReview => PostAbortAction.EscalateHuman,
            PostAbortRecommendation.Rerun => rerunBudgetRemaining > 0
                ? PostAbortAction.Rerun
                : PostAbortAction.EscalateHuman,
            PostAbortRecommendation.StrongerReissue => rerunBudgetRemaining > 0
                ? PostAbortAction.RerunWithStrongerFraming
                : PostAbortAction.EscalateHuman,
            _ => PostAbortAction.EscalateHuman,
        };
    }
}
