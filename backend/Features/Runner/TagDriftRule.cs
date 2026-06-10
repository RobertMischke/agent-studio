using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// Deterministic consistency rule for a job's aspect-concern tags. The
/// multi-aspect pipeline hangs <c>{namespace}:concerns</c> (and the special
/// <c>review:unparseable</c>) tags on a card when an auto-review pass had
/// concerns. Those tags are only legitimate while something is actually open:
/// an active runner-outcome issue, a reissue/escalate verdict, or a current
/// aspect that still reports concerns.
///
/// <para>This rule flags the inverse - a concern tag that survives on a card
/// whose verdict is <c>accept</c> with no active outcome issue and no current
/// justification - as <b>drift</b>. It is a pure function (no IO) so it can be
/// unit-tested directly and reused by the orchestrator's boot-sweep backfill.
/// See the "concern tags bleiben kleben" bug.</para>
/// </summary>
public static class TagDriftRule
{
    private static readonly Regex ConcernTagPattern = new(
        @"[a-z][a-z0-9-]*:(?:concerns|unparseable)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A tag the aspect pipeline owns and may therefore remove during
    /// reconciliation: the <c>{namespace}:concerns</c> chips plus the special
    /// <c>review:unparseable</c> marker. Provenance tags (<c>orchestrator-moved</c>),
    /// reissue markers, and user/registry tags are NOT aspect-concern tags and
    /// always survive.
    /// </summary>
    public static bool IsAspectConcernTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        return tag.EndsWith(":concerns", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tag, "review:unparseable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the subset of <paramref name="tags"/> that are drifting concern
    /// tags: aspect-concern tags with no remaining justification. A concern tag
    /// is justified - and therefore NOT drift - when any of these hold:
    /// <list type="bullet">
    ///   <item>the card has an active runner-outcome issue
    ///         (<paramref name="hasOutcomeIssue"/>), or</item>
    ///   <item>the latest verdict is a still-open one (reissue / escalate /
    ///         reject), or</item>
    ///   <item>the tag is in <paramref name="justifiedConcernTags"/> - the set
    ///         of concerns the most recent pass actually raised (so an
    ///         accept-with-concerns card keeps exactly those).</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> FindDriftingConcernTags(
        IReadOnlyList<string> tags,
        IReadOnlyCollection<string> justifiedConcernTags,
        string? verdict,
        bool hasOutcomeIssue)
    {
        if (tags.Count == 0) return Array.Empty<string>();

        // An open verdict or an active outcome issue legitimately keeps every
        // concern tag - the human still has something to act on.
        if (hasOutcomeIssue || VerdictKeepsConcerns(verdict)) return Array.Empty<string>();

        var justified = new HashSet<string>(justifiedConcernTags, StringComparer.OrdinalIgnoreCase);
        return tags
            .Where(IsAspectConcernTag)
            .Where(t => !justified.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Extracts the concern tag ids (<c>{namespace}:concerns</c> /
    /// <c>review:unparseable</c>) embedded in a <see cref="Jobs.ReviewDecisionRecord"/>
    /// reason string. The orchestrator records the accepted concern set verbatim
    /// (e.g. <c>"accept with concerns (quality:concerns, requirement:concerns)"</c>),
    /// which is the authoritative justified set for a card already parked in
    /// human-review where the aspects can no longer be re-run.
    /// </summary>
    public static IReadOnlyList<string> ExtractConcernTagIds(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Array.Empty<string>();
        return ConcernTagPattern.Matches(reason)
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool VerdictKeepsConcerns(string? verdict) =>
        string.Equals(verdict, "reissue", StringComparison.OrdinalIgnoreCase)
        || string.Equals(verdict, "escalate", StringComparison.OrdinalIgnoreCase)
        || string.Equals(verdict, "reject", StringComparison.OrdinalIgnoreCase);
}
