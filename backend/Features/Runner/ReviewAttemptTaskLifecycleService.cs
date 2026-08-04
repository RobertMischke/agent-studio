namespace AgentStudio.Runner;

/// <summary>
/// Joins task-lane authority to ReviewAttempt authority. Terminal lane moves,
/// boot recovery, and Remote Review claims all pass through the same lock so a
/// claim cannot escape after the owning task has landed outside Auto Review.
/// </summary>
public sealed class ReviewAttemptTaskLifecycleService
{
    private readonly object _gate = new();
    private readonly AttemptAuthorityService _authority;
    private readonly TaskScannerService _scanner;
    private readonly TimelineLog _timeline;
    private readonly ILogger<ReviewAttemptTaskLifecycleService> _logger;

    public ReviewAttemptTaskLifecycleService(
        AttemptAuthorityService authority,
        TaskScannerService scanner,
        TimelineLog timeline,
        ILogger<ReviewAttemptTaskLifecycleService> logger)
    {
        _authority = authority;
        _scanner = scanner;
        _timeline = timeline;
        _logger = logger;
    }

    /// <summary>
    /// Serializes a terminal lane mutation with ReviewAttempt revocation. The
    /// lane move lands first; only a successful move can revoke authority.
    /// </summary>
    public MoveJobOutcome ExecuteTerminalTransition(
        TaskInfo task,
        string targetState,
        Func<MoveJobOutcome> transition)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(transition);

        lock (_gate)
        {
            var outcome = transition();
            if (outcome.Status != MoveJobStatus.Success
                || string.Equals(task.State, targetState, StringComparison.Ordinal)
                || !IsTerminalTaskState(targetState))
            {
                return outcome;
            }

            var reason =
                $"Task entered terminal lane '{targetState}'; open ReviewAttempt authority was superseded by the lane transition.";
            var taskIndex = BuildTaskIndex([task]);
            var changed = _authority.SupersedeOpenReviewAttempts(taskKey =>
                taskIndex.ContainsKey(taskKey)
                    ? reason
                    : null);
            AppendJournalEntries(
                changed,
                taskIndex,
                "lane-transition",
                targetState,
                outcome.NewFolderPath);
            return outcome;
        }
    }

    /// <summary>
    /// Boot-time repair for ReviewAttempts that survived after their cards left
    /// Auto Review. The operation is idempotent because terminal attempts are
    /// excluded by the authority service.
    /// </summary>
    public int SweepUnclaimableAttempts(string source = "boot-sweep")
    {
        lock (_gate)
        {
            var tasks = BuildTaskIndex(_scanner.ScanAllJobsWithArchive());
            return SupersedeUnclaimableLocked(tasks, source).Count;
        }
    }

    /// <summary>
    /// Performs the card-state guard and the fenced selection under one
    /// lifecycle lock. Every non-Auto-Review candidate is superseded before the
    /// authority selects the next attempt.
    /// </summary>
    public AttemptWriteResult ClaimNextReview(
        string executorId,
        string hostId,
        string instanceId,
        int? requestedTtlSeconds)
    {
        lock (_gate)
        {
            var tasks = BuildTaskIndex(_scanner.ScanAllJobsWithArchive());
            SupersedeUnclaimableLocked(tasks, "claim-guard");
            return _authority.ClaimNextReview(
                executorId,
                hostId,
                instanceId,
                requestedTtlSeconds);
        }
    }

    /// <summary>
    /// Applies the same card-state guard to the attempt-addressed compatibility
    /// claim route.
    /// </summary>
    public AttemptWriteResult ClaimReview(
        string attemptId,
        string executorId,
        string hostId,
        int? requestedTtlSeconds,
        string idempotencyKey,
        string? instanceId = null)
    {
        lock (_gate)
        {
            var tasks = BuildTaskIndex(_scanner.ScanAllJobsWithArchive());
            SupersedeUnclaimableLocked(tasks, "claim-guard");
            return _authority.ClaimReview(
                attemptId,
                executorId,
                hostId,
                requestedTtlSeconds,
                idempotencyKey,
                instanceId);
        }
    }

    private IReadOnlyList<ReviewAttemptDto> SupersedeUnclaimableLocked(
        IReadOnlyDictionary<string, TaskInfo> tasks,
        string source)
    {
        var changed = _authority.SupersedeOpenReviewAttempts(taskKey =>
        {
            if (!tasks.TryGetValue(taskKey, out var task))
            {
                return
                    $"Task '{taskKey}' is not present in the task store; open ReviewAttempt authority was superseded by the {source}.";
            }

            return string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"Task is in lane '{task.State}', not '{TaskStates.AutoReview}'; open ReviewAttempt authority was superseded by the {source}.";
        });
        AppendJournalEntries(changed, tasks, source);
        return changed;
    }

    private void AppendJournalEntries(
        IReadOnlyList<ReviewAttemptDto> changed,
        IReadOnlyDictionary<string, TaskInfo> tasks,
        string source,
        string? laneOverride = null,
        string? folderOverride = null)
    {
        foreach (var review in changed)
        {
            if (!tasks.TryGetValue(review.TaskKey, out var task)
                && string.IsNullOrWhiteSpace(folderOverride))
            {
                _logger.LogWarning(
                    "review-attempt-superseded-journal-missing-task attempt={AttemptId} task={TaskKey} source={Source}",
                    review.AttemptId,
                    review.TaskKey,
                    source);
                continue;
            }

            var lane = laneOverride ?? task!.State;
            var folder = folderOverride ?? task!.FolderPath;
            _timeline.Append(
                folder,
                TimelineEventKinds.ReviewAttemptSuperseded,
                TimelineActors.System,
                $"ReviewAttempt {review.AttemptId} was superseded because the task is in {lane}.",
                runId: review.AttemptId,
                details: new Dictionary<string, string>
                {
                    ["attemptId"] = review.AttemptId,
                    ["authority"] = AttemptWriteStatus.Superseded.ToString(),
                    ["lane"] = lane,
                    ["source"] = source,
                });
        }
    }

    private static Dictionary<string, TaskInfo> BuildTaskIndex(IEnumerable<TaskInfo> tasks)
    {
        var result = new Dictionary<string, TaskInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            Add(task.TaskKey);
            Add(task.Key);
            Add(task.Id);

            void Add(string? key)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    result.TryAdd(key, task);
            }
        }
        return result;
    }

    private static bool IsTerminalTaskState(string state)
        => state is TaskStates.Completed or TaskStates.Archive;
}
