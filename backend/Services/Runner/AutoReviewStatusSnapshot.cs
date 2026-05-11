namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Rolling status snapshot of the multi-aspect auto-review tick.
/// Surfaced in the kanban <c>4-auto-review</c> lane header so the user
/// sees that the orchestrator is alive and forming opinions, not
/// silently waving jobs through. The snapshot is intentionally tiny:
/// last tick time, last tick's per-outcome counts, and the job slug
/// that the current tick is mid-way through (if any). The header polls
/// it via a small REST endpoint; nothing pushes.
/// </summary>
public sealed class AutoReviewStatusSnapshot
{
    private readonly object _lock = new();
    private DateTime _lastTickAt = DateTime.MinValue;
    private int _accept;
    private int _reissue;
    private int _escalate;
    private int _aspectsRun;
    private int _pending;
    private string? _currentJob;
    private string? _currentProject;

    /// <summary>Read-only view used by the API endpoint.</summary>
    public AutoReviewStatusView Read()
    {
        lock (_lock)
        {
            return new AutoReviewStatusView(
                LastTickAt: _lastTickAt == DateTime.MinValue ? null : _lastTickAt,
                Accept: _accept,
                Reissue: _reissue,
                Escalate: _escalate,
                AspectsRun: _aspectsRun,
                Pending: _pending,
                CurrentJob: _currentJob,
                CurrentProject: _currentProject);
        }
    }

    public void BeginTick()
    {
        lock (_lock)
        {
            _accept = 0;
            _reissue = 0;
            _escalate = 0;
            _aspectsRun = 0;
            _pending = 0;
            _currentJob = null;
            _currentProject = null;
        }
    }

    public void EndTick()
    {
        lock (_lock)
        {
            _lastTickAt = DateTime.UtcNow;
            _currentJob = null;
            _currentProject = null;
        }
    }

    public void SetCurrent(string project, string jobId)
    {
        lock (_lock)
        {
            _currentProject = project;
            _currentJob = jobId;
        }
    }

    public void RecordAccept() { lock (_lock) _accept++; }
    public void RecordReissue() { lock (_lock) _reissue++; }
    public void RecordEscalate() { lock (_lock) _escalate++; }
    public void RecordAspectsRun(int count) { lock (_lock) _aspectsRun += count; }
    public void RecordPending() { lock (_lock) _pending++; }
}

public sealed record AutoReviewStatusView(
    DateTime? LastTickAt,
    int Accept,
    int Reissue,
    int Escalate,
    int AspectsRun,
    int Pending,
    string? CurrentJob,
    string? CurrentProject);
