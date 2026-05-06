using System.Diagnostics;
using System.Text.Json;

namespace AgentTaskboard.UpdateService;

/// <summary>
/// One-update-at-a-time choreographer:
///   1. snapshot the current per-project mode (so we can restore later);
///   2. PUT every project to "manual" so the next CLI pickup doesn't race
///      our restart;
///   3. shell out to update-stable.sh; capture exit code and stdout/stderr;
///   4. wait for the new backend's /healthz to come back;
///   5. restore each project's previous mode (or "auto-continuous" when the
///      caller says so);
///   6. write a history entry to logs/stable-updates.jsonl.
///
/// All status transitions go through one mutex-guarded path so the public
/// /update/status endpoint never observes torn state.
/// </summary>
public sealed class UpdateOrchestrator
{
    private readonly UpdateStatusStore _store;
    private readonly GitProbe _git;
    private readonly BackendProbe _backend;
    private readonly UpdateServiceOptions _options;
    private readonly ILogger<UpdateOrchestrator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateOrchestrator(
        UpdateStatusStore store,
        GitProbe git,
        BackendProbe backend,
        UpdateServiceOptions options,
        ILogger<UpdateOrchestrator> logger)
    {
        _store = store;
        _git = git;
        _backend = backend;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Kicks an update off in the background and returns immediately so the
    /// caller does not block on the ~30 s orchestration. The returned tuple
    /// reports the accepted runId and the phase the run is starting in;
    /// follow-up phase transitions are visible via /update/status.
    /// </summary>
    public (string RunId, string Phase, string Message) StartTrigger(string trigger, bool force, CancellationToken ct)
    {
        if (!_gate.Wait(0, CancellationToken.None))
        {
            var current = _store.Get();
            if (!force) return (current.CurrentRunId ?? "(unknown)", current.Phase, "already running");
            // force=true: spin up a waiter that takes the gate when it frees
            // up, then runs the same orchestration. We still return the new
            // runId so callers can poll for *its* progress.
            var queuedRunId = Guid.NewGuid().ToString("N").Substring(0, 8);
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync(ct);
                try { await RunOrchestrationAsync(queuedRunId, trigger, ct); }
                finally { _gate.Release(); }
            }, ct);
            return (queuedRunId, "preparing", "queued behind in-flight run");
        }

        var runId = Guid.NewGuid().ToString("N").Substring(0, 8);
        var startedAt = DateTime.UtcNow;
        // Stamp the store synchronously so the very next /update/status read
        // reflects the new run, even if the background Task hasn't entered
        // its first SetPhase yet.
        _store.SetPhase("preparing", $"run {runId}: queued", runId, startedAt);

        _ = Task.Run(async () =>
        {
            try { await RunOrchestrationAsync(runId, trigger, ct); }
            finally { _gate.Release(); }
        }, ct);

        return (runId, "preparing", "accepted");
    }

    private async Task RunOrchestrationAsync(string runId, string trigger, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var headBefore = _git.HeadShort();

        try
        {
            _store.SetPhase("preparing", $"run {runId}: preparing", runId, startedAt);

            // 1. Read current modes so we can restore them.
            var prevModes = await _backend.ReadProjectModesAsync(ct) ?? new Dictionary<string, string>();
            _logger.LogInformation("Run {RunId}: snapshot of modes = {Modes}", runId, string.Join(", ", prevModes.Select(kv => $"{kv.Key}={kv.Value}")));

            // 2. PUT every project to manual.
            _store.SetPhase("pausing-runners", $"pausing {prevModes.Count} project runner(s)", runId, startedAt);
            foreach (var p in prevModes.Keys)
            {
                if (prevModes[p] != "manual")
                {
                    var ok = await _backend.SetModeAsync(p, "manual", ct);
                    if (!ok) _logger.LogWarning("Run {RunId}: failed to set {Project} to manual; continuing anyway", runId, p);
                }
            }

            // 3. Shell out to update-stable.sh.
            _store.SetPhase("pulling", "update-stable.sh: pull + restart", runId, startedAt);
            var (rc, output) = await RunUpdateScriptAsync(ct);
            if (rc != 0)
            {
                var error = $"update-stable.sh exit={rc}; tail={Tail(output, 600)}";
                FinishHistory(runId, startedAt, headBefore, _git.HeadShort(), "failed", error, trigger);
                _store.SetPhase("failed", error, runId, startedAt, finishedAt: DateTime.UtcNow);
                return;
            }

            // 4. Wait for backend health.
            _store.SetPhase("restarting", "waiting for backend /healthz", runId, startedAt);
            var healthy = await _backend.WaitForHealthyAsync(TimeSpan.FromSeconds(_options.HealthWaitSeconds), ct);
            if (!healthy)
            {
                var error = $"backend did not return healthy within {_options.HealthWaitSeconds}s";
                FinishHistory(runId, startedAt, headBefore, _git.HeadShort(), "failed", error, trigger);
                _store.SetPhase("failed", error, runId, startedAt, finishedAt: DateTime.UtcNow);
                return;
            }

            // 5. Restore modes.
            _store.SetPhase("resuming", "restoring runner modes", runId, startedAt);
            foreach (var (project, prev) in prevModes)
            {
                if (prev == "manual") continue;
                var ok = await _backend.SetModeAsync(project, prev, ct);
                if (!ok) _logger.LogWarning("Run {RunId}: failed to restore {Project} to {Mode}", runId, project, prev);
            }

            // 6. Done.
            var headAfter = _git.HeadShort();
            // Refresh the cached HeadLocal immediately so the very next
            // /update/status read agrees with the message ("updated A -> B"),
            // without waiting on the next periodic probe.
            _store.SetHead(headAfter);
            FinishHistory(runId, startedAt, headBefore, headAfter, "ok", null, trigger);
            _store.SetPhase("done", $"updated {headBefore} -> {headAfter}", runId, startedAt, finishedAt: DateTime.UtcNow, lastSuccessAt: DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            var error = $"orchestration crashed: {ex.Message}";
            _logger.LogError(ex, "Run {RunId} crashed", runId);
            FinishHistory(runId, startedAt, headBefore, _git.HeadShort(), "failed", error, trigger);
            _store.SetPhase("failed", error, runId, startedAt, finishedAt: DateTime.UtcNow);
        }
    }

    private async Task<(int Rc, string Output)> RunUpdateScriptAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.BashPath,
            WorkingDirectory = _options.DevspaceDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(_options.UpdateScript);
        // Force=1 is intentionally NOT passed: we have already paused the
        // runner, so the script's quiescence wait should clear immediately.
        // If a project was somehow re-armed externally during the window,
        // refusing to update is the right answer.

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (p.ExitCode, stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr));
    }

    private void FinishHistory(string runId, DateTime startedAt, string headBefore, string headAfter, string status, string? error, string trigger)
    {
        var finishedAt = DateTime.UtcNow;
        var entry = new UpdateHistoryEntry(
            RunId: runId,
            StartedAt: startedAt,
            FinishedAt: finishedAt,
            Status: status,
            HeadBefore: headBefore,
            HeadAfter: headAfter,
            DurationSeconds: (int)(finishedAt - startedAt).TotalSeconds,
            Error: error,
            Trigger: trigger);
        _store.AppendHistory(entry);
    }

    private static string Tail(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= n ? s : s[^n..];
    }
}
