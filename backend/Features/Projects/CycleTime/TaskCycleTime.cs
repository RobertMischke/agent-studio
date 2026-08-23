using System.Globalization;

namespace AgentStudio.Projects;

/// <summary>Seconds spent in each additive stage. The sum equals the lead time.</summary>
public sealed record CycleTimeStageSeconds
{
    public double Preparation { get; init; }
    public double QueueWait { get; init; }
    public double Coding { get; init; }
    public double ReviewWait { get; init; }
    public double TestGate { get; init; }
    public double ReviewOther { get; init; }
    public double Integration { get; init; }
    public double HumanReview { get; init; }
    public double Unattributed { get; init; }

    public double Get(string stage) => stage switch
    {
        CycleTimeStages.Preparation => Preparation,
        CycleTimeStages.QueueWait => QueueWait,
        CycleTimeStages.Coding => Coding,
        CycleTimeStages.ReviewWait => ReviewWait,
        CycleTimeStages.TestGate => TestGate,
        CycleTimeStages.ReviewOther => ReviewOther,
        CycleTimeStages.Integration => Integration,
        CycleTimeStages.HumanReview => HumanReview,
        CycleTimeStages.Unattributed => Unattributed,
        _ => 0,
    };

    public double Sum =>
        Preparation + QueueWait + Coding + ReviewWait + TestGate + ReviewOther + Integration + HumanReview + Unattributed;
}

/// <summary>One completed task's cycle-time row (drill-down behind the aggregates).</summary>
public sealed record TaskCycleTime(
    string TaskId,
    string TaskKey,
    string Title,
    string TerminalState,
    DateTime CreatedAt,
    DateTime? FirstClaimedAt,
    DateTime CompletedAt,
    /// <summary><c>ledger</c> when the completion time comes from the lane-change ledger, <c>lane-entry</c> for the legacy EnteredLaneAt fallback.</summary>
    string CompletionSource,
    CycleTimeStageSeconds Stages,
    double ReviewRunSeconds,
    double LeadTimeSeconds,
    double? CycleTimeSeconds,
    int CodingRuns,
    int ReviewRounds,
    int BounceRounds,
    int IntegrationAttempts,
    string? IntegrationOutcome,
    /// <summary><c>pre-human-review</c> (integrate-on-delivery) or <c>acceptance</c> (human accept), for the last integration outcome.</summary>
    string? IntegrationStage,
    IReadOnlyList<string> DataGaps);

/// <summary>Outcome of analysing one task: either a row or the reason it was excluded.</summary>
public sealed record TaskCycleAnalysis(TaskCycleTime? Row, string? ExclusionReason)
{
    public const string ExcludedNoCompletion = "no-completion-timestamp";
    public const string ExcludedNotCompleted = "not-completed";
    public const string ExcludedEpic = "epic";
    /// <summary>Terminal before the requested window; the service skips its files and only counts it.</summary>
    public const string ExcludedBeforeWindow = "before-window";
}

/// <summary>
/// Pure per-task computation. Reads the unified ledger (<c>logs/timeline.jsonl</c>)
/// as the primary source, <c>pipeline-execution.json</c> for local pipeline step
/// timings, and <c>task.json</c> fields as fallbacks. Tolerates partial data: a
/// missing timestamp degrades one stage, never the whole row.
/// </summary>
public static class TaskCycleTimeAnalyzer
{
    private static readonly HashSet<string> PreparationLanes = new(StringComparer.Ordinal)
    {
        TaskStates.Backlog, TaskStates.Preparation, TaskStates.OrchestratorPrep,
    };

    private static readonly HashSet<string> CodingLanes = new(StringComparer.Ordinal)
    {
        TaskStates.Progress, TaskStates.FailedPickup, TaskStates.CodeNotComplete,
    };

    private static readonly HashSet<string> ReviewLanes = new(StringComparer.Ordinal)
    {
        TaskStates.AutoReview, TaskStates.HumanReview, TaskStates.Escalated, TaskStates.Completed,
    };

    private static readonly HashSet<string> WorkLanes = new(StringComparer.Ordinal)
    {
        TaskStates.Backlog, TaskStates.Preparation, TaskStates.OrchestratorPrep, TaskStates.Ready, TaskStates.Progress,
    };

    private static readonly HashSet<string> HumanLanes = new(StringComparer.Ordinal)
    {
        TaskStates.HumanReview, TaskStates.Escalated, TaskStates.Completed,
    };

    private const string DeliveryGateFailedOutcome = "delivery-gate-failed";
    private const string NoLedgerGap = "no-ledger";
    private const string CompletionFromLaneEntryGap = "completion-from-lane-entry";
    private const string ReviewStartUnknownGap = "review-start-unknown";
    private const string IntegrationDurationUnknownGap = "integration-duration-unknown";
    private const string ClockSkewGap = "clock-skew";

    /// <summary>Maximum distance between a pipeline merge step end and the ledger integration outcome to pair them.</summary>
    private static readonly TimeSpan MergeStepPairingTolerance = TimeSpan.FromMinutes(3);

    public static TaskCycleAnalysis Analyze(
        TaskInfo task,
        IReadOnlyList<TimelineEvent> events,
        PipelineExecutionRecord? pipeline)
    {
        if (string.Equals(task.Kind, TaskKinds.Epic, StringComparison.OrdinalIgnoreCase))
            return new TaskCycleAnalysis(null, TaskCycleAnalysis.ExcludedEpic);

        var isTerminal = string.Equals(task.State, TaskStates.Completed, StringComparison.Ordinal)
                         || string.Equals(task.State, TaskStates.Archive, StringComparison.Ordinal);
        if (!isTerminal)
            return new TaskCycleAnalysis(null, TaskCycleAnalysis.ExcludedNotCompleted);

        var ledger = events
            .Where(e => e is not null && e.Ts != default)
            .Select(e => e with { Ts = Utc(e.Ts) })
            .OrderBy(e => e.Ts)
            .ToList();
        var laneChanges = ledger.Where(e => e.Kind == TimelineEventKinds.LaneChanged).ToList();
        var gaps = new List<string>();

        // ---- completion anchor ----
        DateTime? completedAt = laneChanges
            .Where(e => Detail(e, "to") == TaskStates.Completed)
            .Select(e => (DateTime?)e.Ts)
            .LastOrDefault();
        var completionSource = "ledger";
        if (completedAt is null)
        {
            if (string.Equals(task.State, TaskStates.Completed, StringComparison.Ordinal) && task.EnteredLaneAt != default)
            {
                completedAt = Utc(task.EnteredLaneAt);
                completionSource = "lane-entry";
                gaps.Add(CompletionFromLaneEntryGap);
            }
            else
            {
                return new TaskCycleAnalysis(null, TaskCycleAnalysis.ExcludedNoCompletion);
            }
        }

        // ---- creation anchor ----
        var created = ledger.FirstOrDefault(e => e.Kind == TimelineEventKinds.PromptCreated);
        DateTime? createdAt = created?.Ts;
        if (createdAt is null && task.CreatedAt != default) createdAt = Utc(task.CreatedAt);
        if (createdAt is null && ledger.Count > 0) createdAt = ledger[0].Ts;
        if (createdAt is null) createdAt = completedAt;
        if (createdAt > completedAt)
        {
            // Clock skew or a task.json rewritten after completion: clamp so the
            // lead time is never negative, and say so.
            gaps.Add(ClockSkewGap);
            createdAt = completedAt;
        }

        var taskKey = !string.IsNullOrWhiteSpace(task.Key) ? task.Key! :
            !string.IsNullOrWhiteSpace(task.TaskKey) ? task.TaskKey : task.Id;
        var leadTime = (completedAt.Value - createdAt.Value).TotalSeconds;

        if (ledger.Count == 0)
        {
            gaps.Add(NoLedgerGap);
            return new TaskCycleAnalysis(new TaskCycleTime(
                task.Id, taskKey, task.Title, task.State, createdAt.Value, null, completedAt.Value, completionSource,
                new CycleTimeStageSeconds { Unattributed = leadTime },
                0, leadTime, null, 0, 0, 0, 0, null, null, gaps), null);
        }

        // ---- lane intervals ----
        var steps = CollectSteps(pipeline);
        var intervals = new List<LaneInterval>();
        var currentLane = Detail(created, "targetState")
                          ?? laneChanges.Select(e => Detail(e, "from")).FirstOrDefault(v => v is not null)
                          ?? TaskStates.Backlog;
        var cursor = createdAt.Value;
        DateTime? firstClaimedAt = null;
        int codingRuns = 0, reviewRounds = 0, bounceRounds = 0;

        foreach (var change in laneChanges)
        {
            if (change.Ts > completedAt.Value) break;
            var from = Detail(change, "from") ?? currentLane;
            var to = Detail(change, "to") ?? currentLane;
            var at = change.Ts < cursor ? cursor : change.Ts;
            intervals.Add(new LaneInterval(from, cursor, at));
            cursor = at;
            currentLane = to;

            if (to == TaskStates.Progress)
            {
                codingRuns++;
                firstClaimedAt ??= change.Ts;
            }
            if (to == TaskStates.AutoReview) reviewRounds++;
            if (ReviewLanes.Contains(from) && WorkLanes.Contains(to)) bounceRounds++;
        }
        if (cursor < completedAt.Value)
            intervals.Add(new LaneInterval(currentLane, cursor, completedAt.Value));
        if (intervals.Count > 0)
            intervals[^1] = intervals[^1] with { IncludeEnd = true };

        if (firstClaimedAt is null)
        {
            firstClaimedAt = ledger.FirstOrDefault(e => e.Kind == TimelineEventKinds.AgentRunStarted)?.Ts;
            if (firstClaimedAt is null && task.Provenance?.Transitions is { Count: > 0 } transitions)
            {
                var t = transitions.FirstOrDefault(x => string.Equals(x.Lane, TaskStates.Progress, StringComparison.Ordinal));
                if (t is not null && t.AtUtc != default) firstClaimedAt = Utc(t.AtUtc);
            }
            if (firstClaimedAt is not null && codingRuns == 0) codingRuns = 1;
        }

        // ---- stage attribution ----
        double preparation = 0, queueWait = 0, coding = 0, reviewWait = 0, testGate = 0, reviewOther = 0,
            integration = 0, humanReview = 0, unattributed = 0, reviewRun = 0;
        int integrationAttempts = 0;
        string? integrationOutcome = null, integrationStage = null;
        var integrationOutcomeEvents = ledger
            .Where(e => e.Kind is TimelineEventKinds.IntegrationSucceeded
                or TimelineEventKinds.IntegrationFailed
                or TimelineEventKinds.IntegrationOverridden)
            .ToList();
        var integrationStarts = ledger.Where(e => e.Kind == TimelineEventKinds.IntegrationStarted).ToList();
        var postStepEvents = ledger
            .Where(e => e.Kind is TimelineEventKinds.PostStepStarted or TimelineEventKinds.PostStepFinished)
            .ToList();

        foreach (var interval in intervals)
        {
            var length = interval.Seconds;
            if (length <= 0) continue;

            if (PreparationLanes.Contains(interval.Lane))
            {
                preparation += length;
            }
            else if (interval.Lane == TaskStates.Ready)
            {
                queueWait += length;
            }
            else if (CodingLanes.Contains(interval.Lane))
            {
                coding += length;
            }
            else if (interval.Lane == TaskStates.AutoReview)
            {
                var split = SplitAutoReview(interval, postStepEvents, steps, integrationOutcomeEvents, integrationStarts, gaps);
                reviewWait += split.Wait;
                reviewRun += split.Run;
                testGate += split.TestGate;
                reviewOther += split.Other;
                integration += split.Integration;
                integrationAttempts += split.IntegrationAttempts;
                if (split.LastOutcome is not null)
                {
                    integrationOutcome = split.LastOutcome;
                    integrationStage = split.LastOutcomeStage;
                }
            }
            else if (HumanLanes.Contains(interval.Lane))
            {
                var spans = IntegrationSpans(interval, integrationOutcomeEvents, integrationStarts, steps, gaps);
                var inReview = Math.Min(length, spans.Seconds);
                humanReview += length - inReview;
                integration += inReview;
                integrationAttempts += spans.Attempts;
                if (spans.LastOutcome is not null)
                {
                    integrationOutcome = spans.LastOutcome;
                    integrationStage = spans.LastOutcomeStage;
                }
            }
            else
            {
                unattributed += length;
            }
        }

        // Outcome events that fall outside any interval we attributed (for
        // example after completion) still carry the latest outcome fact.
        if (integrationOutcome is null)
        {
            var last = integrationOutcomeEvents.LastOrDefault();
            if (last is not null)
            {
                integrationOutcome = OutcomeOf(last);
                integrationStage = StageOf(last, intervals);
            }
        }

        // Lead time is the authoritative total; anything the lane ladder did not
        // cover (events before creation, missing rows) lands in Unattributed
        // rather than silently vanishing or inflating a named stage.
        var attributed = preparation + queueWait + coding + reviewWait + testGate + reviewOther + integration + humanReview + unattributed;
        var drift = leadTime - attributed;
        if (drift > 1) unattributed += drift;

        var stages = new CycleTimeStageSeconds
        {
            Preparation = Round(preparation),
            QueueWait = Round(queueWait),
            Coding = Round(coding),
            ReviewWait = Round(reviewWait),
            TestGate = Round(testGate),
            ReviewOther = Round(reviewOther),
            Integration = Round(integration),
            HumanReview = Round(humanReview),
            Unattributed = Round(unattributed),
        };

        double? cycleTime = firstClaimedAt is null
            ? null
            : Math.Max(0, (completedAt.Value - firstClaimedAt.Value).TotalSeconds);

        return new TaskCycleAnalysis(new TaskCycleTime(
            task.Id,
            taskKey,
            task.Title,
            task.State,
            createdAt.Value,
            firstClaimedAt,
            completedAt.Value,
            completionSource,
            stages,
            Round(reviewRun),
            Round(leadTime),
            cycleTime is null ? null : Round(cycleTime.Value),
            codingRuns,
            reviewRounds,
            bounceRounds,
            integrationAttempts,
            integrationOutcome,
            integrationStage,
            gaps.Distinct(StringComparer.Ordinal).ToList()), null);
    }

    // ---- auto review split ----

    private readonly record struct AutoReviewSplit(
        double Wait, double Run, double TestGate, double Other, double Integration,
        int IntegrationAttempts, string? LastOutcome, string? LastOutcomeStage);

    private static AutoReviewSplit SplitAutoReview(
        LaneInterval interval,
        List<TimelineEvent> postStepEvents,
        List<PipelineStepTiming> steps,
        List<TimelineEvent> outcomes,
        List<TimelineEvent> starts,
        List<string> gaps)
    {
        var inWindowEvents = postStepEvents.Where(e => interval.Contains(e.Ts)).ToList();
        var inWindowSteps = steps.Where(s => interval.Contains(s.StartedAt)).ToList();

        // Review activity grouped by attempt: every remote ReviewAttempt carries
        // its attemptId on the projected step rows; local pipeline steps group by
        // pipeline attempt. A stay in Post Processing can hold several attempts
        // (infrastructure retries re-claim the same subject), and the idle time
        // between them is queue wait, not review work. The last attempt owns the
        // tail up to the lane change (decision, merge, lane move).
        var activity = new List<(DateTime Start, DateTime End)>();
        foreach (var attempt in inWindowEvents.GroupBy(e => Detail(e, "attemptId") ?? e.RunId ?? string.Empty))
            activity.Add((attempt.Min(e => e.Ts), attempt.Max(e => e.Ts)));
        foreach (var attempt in inWindowSteps.Where(s => s.IsReviewStep).GroupBy(s => s.Attempt))
            activity.Add((attempt.Min(s => s.StartedAt), attempt.Max(s => s.CompletedAt ?? s.StartedAt)));
        var spans = IntegrationSpans(interval, outcomes, starts, steps, gaps);

        if (activity.Count == 0)
        {
            // No review evidence at all: the card sat in Post Processing and was
            // moved on by a human or a sweep. That is waiting, not reviewing.
            if (interval.Seconds > 0) gaps.Add(ReviewStartUnknownGap);
            var integrationOnly = Math.Min(interval.Seconds, spans.Seconds);
            return new AutoReviewSplit(interval.Seconds - integrationOnly, 0, 0, 0, integrationOnly,
                spans.Attempts, spans.LastOutcome, spans.LastOutcomeStage);
        }

        var merged = MergeSpans(activity, interval.Start, interval.End);
        var run = Math.Min(interval.Seconds, merged.Sum(span => Math.Max(0, (span.End - span.Start).TotalSeconds)));
        var wait = Math.Max(0, interval.Seconds - run);

        // Gate duration: ledger rows win (they exist for every remote review
        // attempt, including superseded ones); pipeline steps are the local
        // fallback and carry previous attempts as well.
        double gate = 0;
        var gateFinished = inWindowEvents
            .Where(e => e.Kind == TimelineEventKinds.PostStepFinished
                        && Detail(e, "pipelineStepId") == PipelineCatalogue.BuildTestGateStepId)
            .ToList();
        if (gateFinished.Count > 0)
        {
            foreach (var finished in gateFinished)
            {
                var ms = ParseLong(Detail(finished, "durationMs"));
                if (ms is > 0) { gate += ms.Value / 1000.0; continue; }
                var started = inWindowEvents.LastOrDefault(e =>
                    e.Kind == TimelineEventKinds.PostStepStarted
                    && e.Ts <= finished.Ts
                    && Detail(e, "stepId") == Detail(finished, "stepId")
                    && Detail(e, "attemptId") == Detail(finished, "attemptId"));
                if (started is not null) gate += Math.Max(0, (finished.Ts - started.Ts).TotalSeconds);
            }
        }
        else
        {
            gate = inWindowSteps
                .Where(s => s.StepId == PipelineCatalogue.BuildTestGateStepId)
                .Sum(s => s.Seconds);
        }

        gate = Math.Min(gate, run);
        var integration = Math.Min(spans.Seconds, Math.Max(0, run - gate));
        var other = Math.Max(0, run - gate - integration);
        return new AutoReviewSplit(wait, run, gate, other, integration,
            spans.Attempts, spans.LastOutcome, spans.LastOutcomeStage);
    }

    // ---- integration spans ----

    private readonly record struct IntegrationSpanSummary(double Seconds, int Attempts, string? LastOutcome, string? LastOutcomeStage);

    private static IntegrationSpanSummary IntegrationSpans(
        LaneInterval interval,
        List<TimelineEvent> outcomes,
        List<TimelineEvent> starts,
        List<PipelineStepTiming> steps,
        List<string> gaps)
    {
        double seconds = 0;
        var attempts = 0;
        string? lastOutcome = null, lastStage = null;
        foreach (var outcome in outcomes.Where(o => interval.Contains(o.Ts)))
        {
            var value = OutcomeOf(outcome);
            lastOutcome = value;
            lastStage = Detail(outcome, "stage") ?? (interval.Lane == TaskStates.AutoReview ? "pre-human-review" : "acceptance");
            if (string.Equals(value, DeliveryGateFailedOutcome, StringComparison.OrdinalIgnoreCase))
                continue; // the review failed; no merge was attempted.

            attempts++;
            var start = starts
                .Where(s => s.Ts <= outcome.Ts && interval.Contains(s.Ts))
                .Where(s => !outcomes.Any(o => o.Ts > s.Ts && o.Ts < outcome.Ts))
                .LastOrDefault();
            if (start is not null)
            {
                seconds += Math.Max(0, (outcome.Ts - start.Ts).TotalSeconds);
                continue;
            }

            var merge = steps
                .Where(s => s.IsMergeStep && s.CompletedAt is not null
                            && (outcome.Ts - s.CompletedAt.Value).Duration() <= MergeStepPairingTolerance)
                .OrderBy(s => (outcome.Ts - s.CompletedAt!.Value).Duration())
                .FirstOrDefault();
            if (merge is not null)
            {
                var push = steps.FirstOrDefault(s => s.StepId == PipelineCatalogue.MergeIntoDevelopPushStepId
                                                     && s.StartedAt >= merge.StartedAt
                                                     && s.StartedAt <= merge.StartedAt.AddMinutes(10));
                seconds += merge.Seconds + (push?.Seconds ?? 0);
                continue;
            }

            gaps.Add(IntegrationDurationUnknownGap);
        }
        return new IntegrationSpanSummary(seconds, attempts, lastOutcome, lastStage);
    }

    private static string OutcomeOf(TimelineEvent e) =>
        Detail(e, "outcome") ?? (e.Kind switch
        {
            TimelineEventKinds.IntegrationSucceeded => "integrated",
            TimelineEventKinds.IntegrationOverridden => "overridden",
            _ => "failed",
        });

    private static string? StageOf(TimelineEvent e, List<LaneInterval> intervals)
    {
        var explicitStage = Detail(e, "stage");
        if (explicitStage is not null) return explicitStage;
        var lane = intervals.FirstOrDefault(i => i.Contains(e.Ts))?.Lane;
        return lane == TaskStates.AutoReview ? "pre-human-review" : lane is null ? null : "acceptance";
    }

    /// <summary>
    /// Clips activity spans to the lane stay, sorts and merges overlapping ones,
    /// and extends the last span to <paramref name="tail"/> (the lane change).
    /// </summary>
    private static List<(DateTime Start, DateTime End)> MergeSpans(
        List<(DateTime Start, DateTime End)> spans, DateTime head, DateTime tail)
    {
        var merged = new List<(DateTime Start, DateTime End)>();
        var clipped = spans
            .Select(s => (Start: s.Start < head ? head : s.Start, End: s.End > tail ? tail : s.End))
            .Where(s => s.End >= s.Start);
        foreach (var span in clipped.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, span.End > last.End ? span.End : last.End);
            }
            else
            {
                merged.Add(span);
            }
        }
        if (merged.Count > 0 && merged[^1].End < tail)
            merged[^1] = (merged[^1].Start, tail);
        return merged;
    }

    // ---- pipeline steps ----

    private sealed record PipelineStepTiming(string StepId, int Attempt, DateTime StartedAt, DateTime? CompletedAt, double Seconds)
    {
        public bool IsReviewStep =>
            StepId.StartsWith("post-", StringComparison.Ordinal) || StepId.StartsWith("aspect-", StringComparison.Ordinal);
        public bool IsMergeStep => StepId == PipelineCatalogue.MergeIntoDevelopStepId;
    }

    private static List<PipelineStepTiming> CollectSteps(PipelineExecutionRecord? record)
    {
        var result = new List<PipelineStepTiming>();
        if (record is null) return result;
        void Add(PipelineExecutionRecord r, int attempt)
        {
            foreach (var step in r.Steps)
            {
                if (step.StartedAt is null) continue;
                var started = Utc(step.StartedAt.Value);
                DateTime? completed = step.CompletedAt is null ? null : Utc(step.CompletedAt.Value);
                var seconds = step.DurationMs > 0
                    ? step.DurationMs / 1000.0
                    : completed is null ? 0 : Math.Max(0, (completed.Value - started).TotalSeconds);
                result.Add(new PipelineStepTiming(step.StepId, attempt, started, completed, seconds));
            }
        }
        Add(record, record.Attempt);
        var previousIndex = 0;
        foreach (var previous in record.PreviousAttempts)
        {
            previousIndex++;
            // Previous attempts carry their own number; fall back to a synthetic
            // negative index when a legacy record left it at the default.
            Add(previous, previous.Attempt > 0 && previous.Attempt != record.Attempt ? previous.Attempt : -previousIndex);
        }
        return result;
    }

    // ---- helpers ----

    /// <summary>
    /// Half-open lane stay <c>[Start, End)</c> so an event stamped exactly at a
    /// lane change belongs to the lane it was emitted into, never to both. The
    /// final stay (ending at completion) also owns its end instant.
    /// </summary>
    private sealed record LaneInterval(string Lane, DateTime Start, DateTime End, bool IncludeEnd = false)
    {
        public double Seconds => Math.Max(0, (End - Start).TotalSeconds);
        public bool Contains(DateTime ts) => ts >= Start && (ts < End || (IncludeEnd && ts == End));
    }

    private static string? Detail(TimelineEvent? e, string key)
    {
        if (e?.Details is null) return null;
        return e.Details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static double Round(double seconds) => Math.Round(seconds, 1);
}
