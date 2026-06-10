namespace AgentStudio.Supervisor;

/// <summary>
/// Pure summariser for the periodic supervisor chat-note. Reads a window of
/// observation evidence and decides whether to emit a one-line summary into
/// the project's chat. Quiet beats spam: when nothing notable happened
/// inside the window the function returns <c>null</c> and the hosted
/// service stays silent.
/// </summary>
public static class ChatNoteSummary
{
    /// <summary>
    /// Hard cap on the persisted message body. Matches the contract in the
    /// chat-note task: a single line, &lt; 240 chars, so the activity-log
    /// renderer never has to wrap.
    /// </summary>
    public const int MaxLength = 240;

    /// <summary>
    /// Build a one-line summary for the window or return <c>null</c> if the
    /// window had no advisories, no cycles, and no review-lane arrivals.
    /// </summary>
    public static string? Build(ChatNoteWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var advisoryCount = window.Advisories.Count;
        var cycleCount = window.Cycles.Count;
        var reviewCount = window.JobsReachedReviewCount;

        if (advisoryCount == 0 && cycleCount == 0 && reviewCount == 0)
        {
            return null;
        }

        var advPhrase = BuildAdvisoryPhrase(window.Advisories);
        var cyclePhrase = BuildCyclePhrase(window.Cycles);
        var reviewPhrase = BuildReviewPhrase(reviewCount);

        var span = window.To - window.From;
        var minutes = Math.Max(1, (int)Math.Round(span.TotalMinutes));

        var msg = $"Supervisor: {advPhrase}, {cyclePhrase}, {reviewPhrase} in the last {minutes} min.";
        if (msg.Length > MaxLength)
        {
            msg = msg[..(MaxLength - 3)] + "...";
        }
        return msg;
    }

    private static string BuildAdvisoryPhrase(IReadOnlyList<SupervisorAdvisory> advisories)
    {
        if (advisories.Count == 0) return "0 advisories";

        var warnish = advisories
            .Where(a => (int)a.Severity >= (int)SupervisorSeverity.Warn)
            .ToList();
        var visible = warnish.Count > 0 ? warnish : advisories.ToList();

        var noun = visible.Count == 1
            ? (warnish.Count > 0 ? "warn advisory" : "advisory")
            : (warnish.Count > 0 ? "warn advisories" : "advisories");

        var topics = visible
            .Select(a => a.Topic)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        return topics.Count == 0
            ? $"{visible.Count} {noun}"
            : $"{visible.Count} {noun} on {string.Join("/", topics)}";
    }

    private static string BuildCyclePhrase(IReadOnlyList<ChatNoteCycleEntry> cycles)
    {
        if (cycles.Count == 0) return "0 cycles";

        var noun = cycles.Count == 1 ? "cycle" : "cycles";
        var verdict = cycles[^1].Verdict;
        if (string.IsNullOrWhiteSpace(verdict))
        {
            return $"{cycles.Count} {noun}";
        }
        return $"{cycles.Count} {noun} ({verdict.ToLowerInvariant()})";
    }

    private static string BuildReviewPhrase(int reviewCount)
    {
        var noun = reviewCount == 1 ? "job" : "jobs";
        return $"{reviewCount} {noun} reached review";
    }
}

/// <summary>
/// Pre-collected evidence for one chat-note window. Building this is the
/// hosted service's job; deciding whether to emit a message is pure.
/// </summary>
public sealed record ChatNoteWindow(
    DateTime From,
    DateTime To,
    IReadOnlyList<SupervisorAdvisory> Advisories,
    IReadOnlyList<ChatNoteCycleEntry> Cycles,
    int JobsReachedReviewCount);

/// <summary>
/// One row from <c>meta-cycle.log</c> projected for chat-note rendering.
/// We keep only the fields the summary actually shows so the parser stays
/// tolerant to future tail-log columns.
/// </summary>
public sealed record ChatNoteCycleEntry(
    DateTime CompletedAt,
    string CycleId,
    string Verdict,
    string ActionKind,
    string ActionReason);
