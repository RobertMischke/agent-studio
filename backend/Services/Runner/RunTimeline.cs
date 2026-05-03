using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// One CLI invocation between two user inputs - the unit of conversation
/// in the runs/sessions model documented in
/// <c>docs/design-principles.md</c>. Produced by
/// <see cref="RunTimelineBuilder.Build"/> from <c>session-events.jsonl</c>
/// + <c>cli-output.log</c>; consumed by the
/// <c>/api/jobs/{id}/runs</c> endpoint and the protocol-pane run
/// timeline.
///
/// All fields are derived; nothing here is the source of truth on its
/// own. <see cref="LineStart"/> / <see cref="LineEnd"/> are 1-based
/// indices into the cli-output.log file so the frontend can fetch the
/// run's slice without re-parsing the whole file.
/// </summary>
public sealed record RunRecord
{
    /// <summary>1-based chronological index in the session.</summary>
    public int Index { get; init; }
    /// <summary><c>start</c> | <c>continue</c> | <c>recovery</c> | <c>restart</c>.</summary>
    public string Intent { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    /// <summary><c>running</c> | <c>completed</c> | <c>failed</c> | <c>cancelled</c> | <c>unknown</c>.</summary>
    public string Status { get; init; } = "unknown";
    public string? Cli { get; init; }
    public int? ExitCode { get; init; }
    public double? DurationSeconds { get; init; }
    public string? InputSessionId { get; init; }
    public string? CapturedSessionId { get; init; }
    public bool Resumed { get; init; }
    public string? Reason { get; init; }
    /// <summary>The user follow-up that triggered this run (the most recent <c>[user]</c>-stream line before the run started). Null when the run was an auto-pickup or fresh start.</summary>
    public string? UserFollowup { get; init; }
    /// <summary>1-based line number in <c>cli-output.log</c> where this run begins (the <c>[taskboard] Started ... CLI</c> marker).</summary>
    public int? LineStart { get; init; }
    /// <summary>1-based line number in <c>cli-output.log</c> where this run ends (the <c>[taskboard] ... CLI exited</c> marker, or the file's last line for the still-running tail).</summary>
    public int? LineEnd { get; init; }
}

/// <summary>
/// Top-level session shape the <c>/api/jobs/{id}/runs</c> endpoint
/// returns. The runs list is the primary surface; the aggregates above
/// it (<see cref="RunCount"/>, <see cref="LastActivityAt"/>) are derived
/// once on the backend so every consumer renders the same numbers.
/// </summary>
public sealed record RunTimeline
{
    public int RunCount { get; init; }
    public DateTime? FirstStartedAt { get; init; }
    public DateTime? LastActivityAt { get; init; }
    /// <summary>True when the last run is still running (no end marker yet).</summary>
    public bool HasActiveRun { get; init; }
    public List<RunRecord> Runs { get; init; } = [];
}

/// <summary>
/// Pure builder. Takes the raw session-events list + cli-output.log
/// lines, returns a fully-populated <see cref="RunTimeline"/>. No I/O,
/// no DI, no dates relative to "now" - the caller passes
/// <paramref name="nowUtc"/> so the still-running tail's
/// <c>EndedAt</c> stays deterministic in tests.
///
/// <para>
/// <b>Why pure.</b> The timeline is the load-bearing aggregation
/// behind the run-list UI; it is also the input to per-run summaries
/// and per-run commit lookups. A pure builder lets us lock the entire
/// shape with fixture-based xUnit tests, the same pattern
/// <see cref="RunPlanner"/> and <see cref="RunOutcomePolicy"/> follow.
/// </para>
/// </summary>
public static class RunTimelineBuilder
{
    private static readonly Regex StartedRegex = new(
        @"^\[taskboard\]\s+Started\s+(?<cli>\S+)\s+CLI\b",
        RegexOptions.Compiled);

    private static readonly Regex ExitedRegex = new(
        @"^\[taskboard\]\s+(?<cli>\S+)\s+CLI\s+exited:\s*status=(?<status>\w+)(?:,\s*exitCode=(?<code>-?\d+|\?))?(?:,\s*duration=(?<dur>[\d.]+)s)?",
        RegexOptions.Compiled);

    /// <summary>
    /// Build the timeline. <paramref name="lines"/> is the parsed
    /// <c>cli-output.log</c>; <paramref name="events"/> is the parsed
    /// <c>session-events.jsonl</c>. Both can be empty.
    /// </summary>
    public static RunTimeline Build(
        IReadOnlyList<SessionEvent> events,
        IReadOnlyList<CliOutputLine> lines,
        DateTime nowUtc)
    {
        events ??= [];
        lines ??= [];

        // Pre-index the log by [taskboard] marker so we can pair each
        // SessionEvent with the run boundary that came from the runner.
        // The marker carries the authoritative status / exit code /
        // duration, while the SessionEvent carries the intent / session
        // ids / user-visible reason.
        var startedMarkers = new List<(int LineIndex, DateTime Ts, string Cli)>();
        var exitedMarkers = new List<(int LineIndex, DateTime Ts, string Cli, string Status, int? ExitCode, double? Duration)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (!string.Equals(l.Stream, "system", StringComparison.OrdinalIgnoreCase)) continue;
            var text = l.Text ?? string.Empty;
            var sm = StartedRegex.Match(text);
            if (sm.Success)
            {
                startedMarkers.Add((i, l.Timestamp, sm.Groups["cli"].Value));
                continue;
            }
            var em = ExitedRegex.Match(text);
            if (em.Success)
            {
                int? exitCode = null;
                if (em.Groups["code"].Success && int.TryParse(em.Groups["code"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ec))
                    exitCode = ec;
                double? duration = null;
                if (em.Groups["dur"].Success && double.TryParse(em.Groups["dur"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    duration = d;
                exitedMarkers.Add((i, l.Timestamp, em.Groups["cli"].Value, em.Groups["status"].Value, exitCode, duration));
            }
        }

        var runs = new List<RunRecord>(events.Count);
        for (int idx = 0; idx < events.Count; idx++)
        {
            var evt = events[idx];
            var nextEvt = idx + 1 < events.Count ? events[idx + 1] : null;

            // Pair the event with the [taskboard] Started marker whose
            // timestamp is closest at-or-after the event ts. Recovery
            // events occasionally don't have a Started marker (e.g.
            // because the planner stopped before spawning a CLI), so a
            // missing pair is allowed.
            var startedMarker = FindFirstAfter(startedMarkers, evt.Ts);

            // The exit marker is the first one strictly after the run's
            // started marker (or after the event ts if no started marker
            // matched). Cap it at the next event's ts so concurrent
            // runs cannot leak into each other. (In this product runs
            // are sequential per project, so this is mostly defensive.)
            DateTime upperBound = nextEvt?.Ts ?? DateTime.MaxValue;
            var exitMarker = FindFirstAfter(exitedMarkers, startedMarker?.Ts ?? evt.Ts);
            if (exitMarker is { } em2 && em2.Ts >= upperBound) exitMarker = null;

            // User follow-up: the most recent [user] line strictly
            // before the run started. Keep it brief; the activity log
            // has the full text if the user wants to see it.
            var followup = FindLastUserBefore(lines, startedMarker?.LineIndex ?? FindLineNearTimestamp(lines, evt.Ts));

            var status = exitMarker?.Status ?? (startedMarker.HasValue ? "running" : "unknown");
            var endedAt = exitMarker?.Ts;
            var lineStart = startedMarker?.LineIndex is int li ? li + 1 : (int?)null;
            int? lineEnd;
            if (exitMarker.HasValue)
            {
                lineEnd = exitMarker.Value.LineIndex + 1;
            }
            else if (startedMarker.HasValue)
            {
                // Still running - the tail of the log belongs to this
                // run. Anchor on the next event's start (sequential
                // model) or the file's last line.
                lineEnd = nextEvt != null
                    ? Math.Max(lineStart ?? 1, FindLineNearTimestamp(lines, nextEvt.Ts) + 1)
                    : lines.Count;
            }
            else
            {
                lineEnd = null;
            }

            runs.Add(new RunRecord
            {
                Index = idx + 1,
                Intent = evt.Kind ?? "",
                StartedAt = evt.Ts,
                EndedAt = endedAt,
                Status = status,
                Cli = evt.Cli ?? startedMarker?.Cli,
                ExitCode = exitMarker?.ExitCode,
                DurationSeconds = exitMarker?.Duration,
                InputSessionId = evt.InputSessionId,
                CapturedSessionId = evt.CapturedSessionId,
                Resumed = evt.Resumed,
                Reason = evt.Reason,
                UserFollowup = followup,
                LineStart = lineStart,
                LineEnd = lineEnd
            });
        }

        DateTime? lastActivity =
            runs.Count == 0 ? null
            : runs[^1].EndedAt ?? (lines.Count > 0 ? lines[^1].Timestamp : runs[^1].StartedAt);

        return new RunTimeline
        {
            RunCount = runs.Count,
            FirstStartedAt = runs.Count > 0 ? runs[0].StartedAt : null,
            LastActivityAt = lastActivity,
            HasActiveRun = runs.Count > 0 && string.Equals(runs[^1].Status, "running", StringComparison.OrdinalIgnoreCase),
            Runs = runs
        };
    }

    private static (int LineIndex, DateTime Ts, string Cli)? FindFirstAfter(
        List<(int LineIndex, DateTime Ts, string Cli)> markers,
        DateTime threshold)
    {
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i].Ts >= threshold.AddSeconds(-2))
                return markers[i];
        }
        return null;
    }

    private static (int LineIndex, DateTime Ts, string Cli, string Status, int? ExitCode, double? Duration)? FindFirstAfter(
        List<(int LineIndex, DateTime Ts, string Cli, string Status, int? ExitCode, double? Duration)> markers,
        DateTime threshold)
    {
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i].Ts >= threshold)
                return markers[i];
        }
        return null;
    }

    private static string? FindLastUserBefore(IReadOnlyList<CliOutputLine> lines, int beforeIndex)
    {
        for (int i = Math.Min(beforeIndex - 1, lines.Count - 1); i >= 0; i--)
        {
            var l = lines[i];
            if (l == null) continue;
            if (string.Equals(l.Stream, "user", StringComparison.OrdinalIgnoreCase))
            {
                var text = (l.Text ?? string.Empty).Trim();
                return string.IsNullOrEmpty(text) ? null : Truncate(text, 280);
            }
        }
        return null;
    }

    private static int FindLineNearTimestamp(IReadOnlyList<CliOutputLine> lines, DateTime ts)
    {
        // Linear scan; the log files we deal with are small (thousands
        // of lines per job at the high end), so an index would be
        // overkill.
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Timestamp >= ts) return i;
        }
        return lines.Count;
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s.Substring(0, max - 1) + "…";
    }
}
