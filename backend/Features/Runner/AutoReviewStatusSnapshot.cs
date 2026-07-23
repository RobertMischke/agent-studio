namespace AgentStudio.Runner;

/// <summary>
/// Rolling status snapshot of the multi-aspect auto-review tick.
/// Surfaced in compact board indicators so the user sees that the
/// orchestrator is alive and forming opinions, not silently waving jobs
/// through. The snapshot is intentionally tiny: last tick time, last
/// tick's per-outcome counts, and the job slug that the current tick is
/// mid-way through (if any). Consumers poll it via a small REST endpoint;
/// nothing pushes.
/// </summary>
public sealed class AutoReviewStatusSnapshot
{
    public const double DefaultEscalationRateAlertThreshold = 0.5;
    public const int DefaultEscalationRateMinimumDecisions = 3;

    private readonly object _lock = new();
    private DateTime _lastTickAt = DateTime.MinValue;
    private int _accept;
    private int _reissue;
    private int _escalate;
    private int _aspectsRun;
    private int _pending;
    private double _escalationRateAlertThreshold = DefaultEscalationRateAlertThreshold;
    private int _escalationRateMinimumDecisions = DefaultEscalationRateMinimumDecisions;
    private string? _currentJob;
    private string? _currentProject;
    private readonly Dictionary<string, AutoReviewActivityView> _activeJobs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Read-only view used by the API endpoint.</summary>
    public AutoReviewStatusView Read()
    {
        lock (_lock)
        {
            var decisionCount = _accept + _escalate;
            var escalationRate = decisionCount == 0 ? 0 : (double)_escalate / decisionCount;
            var escalationRateAlert = decisionCount >= _escalationRateMinimumDecisions
                && escalationRate > _escalationRateAlertThreshold;
            return new AutoReviewStatusView(
                LastTickAt: _lastTickAt == DateTime.MinValue ? null : _lastTickAt,
                Accept: _accept,
                Reissue: _reissue,
                Escalate: _escalate,
                AspectsRun: _aspectsRun,
                Pending: _pending,
                EscalationRate: escalationRate,
                EscalationRateDecisionCount: decisionCount,
                EscalationRateAlertThreshold: _escalationRateAlertThreshold,
                EscalationRateAlert: escalationRateAlert,
                CurrentJob: _currentJob,
                CurrentProject: _currentProject,
                ActiveJobs: _activeJobs.Values
                    .OrderBy(activity => activity.StartedAt)
                    .ToList());
        }
    }

    public void ConfigureEscalationRateAlert(double threshold, int minimumDecisions)
    {
        lock (_lock)
        {
            if (threshold is >= 0 and <= 1)
                _escalationRateAlertThreshold = threshold;
            if (minimumDecisions > 0)
                _escalationRateMinimumDecisions = minimumDecisions;
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
            _activeJobs.Clear();
        }
    }

    public void EndTick()
    {
        lock (_lock)
        {
            _lastTickAt = DateTime.UtcNow;
            _currentJob = null;
            _currentProject = null;
            _activeJobs.Clear();
        }
    }

    public void SetCurrent(string project, string jobId)
    {
        lock (_lock)
        {
            _currentProject = project;
            _currentJob = jobId;
            var key = ActivityKey(project, jobId);
            if (!_activeJobs.ContainsKey(key))
            {
                _activeJobs[key] = new AutoReviewActivityView(
                    project, jobId, AutoReviewActivitySteps.Processing, DateTime.UtcNow);
            }
        }
    }

    public void SetCurrentStep(string project, string jobId, string step)
    {
        lock (_lock)
        {
            _currentProject = project;
            _currentJob = jobId;
            _activeJobs[ActivityKey(project, jobId)] = new AutoReviewActivityView(
                project, jobId, step, DateTime.UtcNow);
        }
    }

    public void ClearCurrent(string project, string jobId)
    {
        lock (_lock)
        {
            _activeJobs.Remove(ActivityKey(project, jobId));
            if (string.Equals(_currentProject, project, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_currentJob, jobId, StringComparison.OrdinalIgnoreCase))
            {
                var next = _activeJobs.Values.LastOrDefault();
                _currentProject = next?.Project;
                _currentJob = next?.JobId;
            }
        }
    }

    private static string ActivityKey(string project, string jobId) => $"{project}\n{jobId}";

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
    double EscalationRate,
    int EscalationRateDecisionCount,
    double EscalationRateAlertThreshold,
    bool EscalationRateAlert,
    string? CurrentJob,
    string? CurrentProject,
    IReadOnlyList<AutoReviewActivityView> ActiveJobs);

public sealed record AutoReviewActivityView(
    string Project,
    string JobId,
    string Step,
    DateTime StartedAt);

public static class AutoReviewActivitySteps
{
    public const string Processing = "processing";
    public const string Gate = "gate";
    public const string GateQueued = "gate-queued";
    public const string Aspects = "aspects";
    public const string Grade = "grade";
    public const string Decision = "decision";
}
