namespace OrchestratorApi.Services.Jobs;

/// <summary>
/// Central pub/sub fanout for fine-grained job mutation events. Replaces
/// the previous "every mutation invalidates the snapshot cache and the
/// frontend waits for the next 2 s poll" pattern with synchronous push to
/// the SignalR hub.
///
/// <para>Publishers: <see cref="JobMutationService"/> (Create / Update),
/// <see cref="JobTransitionService"/> (Move), <see cref="JobStateMachine"/>
/// (Delete + lane-wide reorder).</para>
///
/// <para>Subscribers: <see cref="OrchestratorApi.Hubs.JobHub"/> wiring in
/// <c>Program.cs</c>, which broadcasts the typed methods
/// <c>jobCreated</c> / <c>jobUpdated</c> / <c>jobMoved</c> / <c>jobDeleted</c>
/// / <c>jobsReordered</c> to all connected clients.</para>
///
/// <para>Subscribers must be cheap and side-effect-only; the disk write
/// is already past by the time these fire. Exceptions thrown by
/// subscribers are caught and logged so a single bad handler cannot
/// poison the mutation path.</para>
/// </summary>
public sealed class JobChangeNotifier
{
    private readonly ILogger<JobChangeNotifier> _logger;

    public JobChangeNotifier(ILogger<JobChangeNotifier> logger)
    {
        _logger = logger;
    }

    public event Action<JobChangeEvent>? JobCreated;
    public event Action<JobChangeEvent>? JobUpdated;
    public event Action<JobMoveEvent>? JobMoved;
    public event Action<JobChangeEvent>? JobDeleted;

    /// <summary>
    /// Lane-wide reorder: one or more jobs in a given project had their
    /// <c>order</c> field rewritten. The frontend should refresh the
    /// affected project's grouped view rather than try to patch individual
    /// rows.
    /// </summary>
    public event Action<JobsReorderedEvent>? JobsReordered;

    /// <summary>
    /// Coarse "something changed broadly, re-pull suggested" signal. Carries
    /// no payload. Fired by paths that touch many folders at once (bulk
    /// reorder, queue promotion, boot-time sweeps/backfill) where patching
    /// individual rows is not worth it - the frontend reacts with a single
    /// silent re-fetch of <c>/api/jobs/grouped</c>.
    /// </summary>
    public event Action? JobsBulkChanged;

    public void PublishCreated(string projectName, string jobId, string watchPath)
        => Invoke(JobCreated, new JobChangeEvent(projectName, jobId, watchPath), nameof(JobCreated));

    public void PublishUpdated(string projectName, string jobId, string watchPath)
        => Invoke(JobUpdated, new JobChangeEvent(projectName, jobId, watchPath), nameof(JobUpdated));

    public void PublishMoved(string projectName, string jobId, string watchPath, string fromState, string toState)
        => Invoke(JobMoved, new JobMoveEvent(projectName, jobId, watchPath, fromState, toState), nameof(JobMoved));

    public void PublishDeleted(string projectName, string jobId, string watchPath)
        => Invoke(JobDeleted, new JobChangeEvent(projectName, jobId, watchPath), nameof(JobDeleted));

    public void PublishReordered(string projectName, string watchPath, string? lane)
        => Invoke(JobsReordered, new JobsReorderedEvent(projectName, watchPath, lane), nameof(JobsReordered));

    public void PublishBulkChanged()
    {
        var handler = JobsBulkChanged;
        if (handler == null) return;
        try { handler(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JobsBulkChanged subscriber threw");
        }
    }

    private void Invoke<T>(Action<T>? handler, T evt, string name)
    {
        if (handler == null) return;
        try { handler(evt); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Event} subscriber threw for {Evt}", name, evt);
        }
    }
}

public readonly record struct JobChangeEvent(string ProjectName, string JobId, string WatchPath);

public readonly record struct JobMoveEvent(string ProjectName, string JobId, string WatchPath, string FromState, string ToState);

public readonly record struct JobsReorderedEvent(string ProjectName, string WatchPath, string? Lane);
