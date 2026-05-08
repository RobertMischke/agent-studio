using System.Diagnostics;
using System.Text;

namespace AgentTaskboard.UpdateService;

/// <summary>
/// ADR-0031 9-phase pipeline:
///   1. preparing               — write run folder + pre-snapshot
///   2. pausing-runners         — PUT mode=manual on every project
///   3. pulling                 — git fetch + pull --ff-only
///   4. building                — npm install when package-lock changed
///   5. restarting              — stop-stable.sh + start-stable.sh (DETACH=1)
///   6. verifying-after-restart — six-check matrix (ADR-0031)
///   7. resuming                — restore each project's pre mode
///   8. done                    — write post-snapshot + summary, append history
///   9. idle                    — steady state until the next trigger
///
/// Failure of any phase (3..7) flips to <c>phase=failed</c> with a
/// structured <see cref="VerificationFailure"/> array (when verification
/// caused the failure) plus a captured run folder. Auto-rollback is opt-in
/// via <see cref="UpdateServiceOptions.AutoRollback"/>.
/// </summary>
public sealed class UpdateOrchestrator
{
    private readonly UpdateStatusStore _store;
    private readonly GitProbe _git;
    private readonly BackendProbe _backend;
    private readonly UpdateVerifier _verifier;
    private readonly UpdateServiceOptions _options;
    private readonly ILogger<UpdateOrchestrator> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateOrchestrator(
        UpdateStatusStore store,
        GitProbe git,
        BackendProbe backend,
        UpdateVerifier verifier,
        UpdateServiceOptions options,
        ILogger<UpdateOrchestrator> logger,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _git = git;
        _backend = backend;
        _verifier = verifier;
        _options = options;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public (string RunId, string Phase, string Message) StartTrigger(string trigger, bool force, CancellationToken ct)
    {
        if (!_gate.Wait(0, CancellationToken.None))
        {
            var current = _store.Get();
            if (!force) return (current.CurrentRunId ?? "(unknown)", current.Phase, "already running");
            var queuedRunId = NewRunId();
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync(ct);
                try { await RunOrchestrationAsync(queuedRunId, trigger, ct); }
                finally { _gate.Release(); }
            }, ct);
            return (queuedRunId, "preparing", "queued behind in-flight run");
        }

        var runId = NewRunId();
        var startedAt = DateTime.UtcNow;
        SetPhase("preparing", $"run {runId}: queued", runId, startedAt);

        _ = Task.Run(async () =>
        {
            try { await RunOrchestrationAsync(runId, trigger, ct); }
            finally { _gate.Release(); }
        }, ct);

        return (runId, "preparing", "accepted");
    }

    /// <summary>
    /// Manual-rollback entry. The runId must match the most recent run; the
    /// snapshot HEAD is read from <c>pre-snapshot.json</c> in that run's
    /// folder, then we replay phases 5+6+7 against that SHA.
    /// </summary>
    public (string RunId, string Phase, string Message) StartManualRollback(string runId, CancellationToken ct)
    {
        if (!_gate.Wait(0, CancellationToken.None))
            return (runId, _store.Get().Phase, "already running");

        var startedAt = DateTime.UtcNow;
        SetPhase("rolling-back", $"manual rollback for run {runId}", runId, startedAt);
        _ = Task.Run(async () =>
        {
            try { await RunRollbackAsync(runId, manual: true, ct); }
            finally { _gate.Release(); }
        }, ct);
        return (runId, "rolling-back", "accepted");
    }

    // ─── pipeline ───────────────────────────────────────────────────────────

    private async Task RunOrchestrationAsync(string runId, string trigger, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var folder = new RunFolder(_options.RunsDirectory, runId, _loggerFactory.CreateLogger<RunFolder>());

        var headBefore = _git.HeadShort();
        UpdateRunSnapshot? preSnapshot = null;

        try
        {
            // PHASE 1 — preparing
            SetPhase("preparing", "snapshotting pre-state", runId, startedAt);
            var preModes = await _backend.ReadProjectModesAsync(ct) ?? new Dictionary<string, string>();
            preSnapshot = await CaptureSnapshotAsync("pre", runId, headBefore, preModes, ct);
            folder.WriteSnapshot(preSnapshot);

            // PHASE 2 — pausing-runners
            SetPhase("pausing-runners", $"pausing {preModes.Count} project runner(s)", runId, startedAt);
            foreach (var kv in preModes)
                if (kv.Value != "manual")
                    await _backend.SetModeAsync(kv.Key, "manual", ct);

            // PHASE 3 — pulling
            SetPhase("pulling", "git fetch + pull --ff-only", runId, startedAt);
            var (pullRc, pullOut) = await RunBashAsync(
                "-c",
                $"git fetch origin main && git pull --ff-only origin main",
                _options.StableCheckoutDir, ct);
            folder.WriteOutput("pull-output.txt", pullOut);
            if (pullRc != 0)
            {
                FinishFailed(runId, startedAt, headBefore, _git.HeadShort(), trigger,
                    $"git pull failed (rc={pullRc})", null, folder, preSnapshot);
                return;
            }
            var headAfterPull = _git.HeadShort();
            _store.SetHead(headAfterPull);

            // PHASE 4 — building
            SetPhase("building", "npm install if package-lock changed", runId, startedAt);
            var (buildRc, buildOut, buildRan) = await MaybeRunNpmInstallAsync(headBefore, headAfterPull, ct);
            if (buildRan) folder.WriteOutput("npm-install-output.txt", buildOut);
            if (buildRc != 0)
            {
                FinishFailed(runId, startedAt, headBefore, headAfterPull, trigger,
                    $"npm install failed (rc={buildRc})", null, folder, preSnapshot);
                return;
            }

            // PHASE 5 — restarting
            SetPhase("restarting", "stop + start stable backend", runId, startedAt);
            var (restartRc, restartOut) = await RunRestartAsync(ct);
            folder.WriteOutput("start-stable-output.txt", restartOut);
            if (restartRc != 0)
            {
                FinishFailed(runId, startedAt, headBefore, headAfterPull, trigger,
                    $"restart failed (rc={restartRc})", null, folder, preSnapshot);
                return;
            }

            // Wait until backend is reachable before starting the strict matrix.
            var healthy = await _backend.WaitForHealthyAsync(TimeSpan.FromSeconds(_options.HealthWaitSeconds), ct);
            if (!healthy)
            {
                var failure = new VerificationFailure("healthz-stable",
                    $"timeout after {_options.HealthWaitSeconds}s",
                    "/healthz=200");
                FinishFailed(runId, startedAt, headBefore, headAfterPull, trigger,
                    "backend did not come back healthy", new[] { failure }, folder, preSnapshot);

                if (_options.AutoRollback)
                    await RunRollbackAsync(runId, manual: false, ct);
                return;
            }

            // PHASE 6 — verifying-after-restart
            SetPhase("verifying-after-restart", "running 6-check matrix", runId, startedAt);
            var verification = await _verifier.RunAsync(runId, preModes, folder.AppendVerification, ct);
            if (!verification.AllPassed)
            {
                FinishFailed(runId, startedAt, headBefore, headAfterPull, trigger,
                    $"verification failed: {string.Join(", ", verification.Failures.Select(f => f.Step))}",
                    verification.Failures, folder, preSnapshot);

                if (_options.AutoRollback)
                    await RunRollbackAsync(runId, manual: false, ct);
                return;
            }

            // PHASE 7 — resuming
            SetPhase("resuming", "restoring runner modes", runId, startedAt);
            var resumeOut = new StringBuilder();
            foreach (var (project, prev) in preModes)
            {
                if (prev == "manual") continue;
                var ok = await _backend.SetModeAsync(project, prev, ct);
                resumeOut.AppendLine($"{project} -> {prev}: {(ok ? "ok" : "FAIL")}");
            }
            folder.WriteOutput("resume-output.txt", resumeOut.ToString());

            // PHASE 8 — done
            var headAfter = _git.HeadShort();
            _store.SetHead(headAfter);
            var postModes = await _backend.ReadProjectModesAsync(ct) ?? new Dictionary<string, string>();
            var postSnapshot = await CaptureSnapshotAsync("post", runId, headAfter, postModes, ct);
            folder.WriteSnapshot(postSnapshot);
            folder.WriteSummary(BuildSummaryMarkdown(runId, trigger, startedAt, headBefore, headAfter, preSnapshot, postSnapshot, verification, null));

            FinishHistory(runId, startedAt, headBefore, headAfter, "ok", null, trigger, null, null, folder.Root);
            FinishDone(runId, startedAt, headBefore, headAfter,
                $"updated {headBefore} -> {headAfter}", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {RunId} crashed", runId);
            var headNow = _git.HeadShort();
            FinishFailed(runId, startedAt, headBefore, headNow, trigger,
                $"orchestration crashed: {ex.Message}", null, folder, preSnapshot);
        }
    }

    // ─── rollback ───────────────────────────────────────────────────────────

    private async Task RunRollbackAsync(string parentRunId, bool manual, CancellationToken ct)
    {
        var folder = new RunFolder(_options.RunsDirectory, parentRunId, _loggerFactory.CreateLogger<RunFolder>());
        var headBefore = _git.HeadShort();

        // Read the snapshot SHA from disk so manual rollback works even
        // after a process restart.
        string? targetSha = null;
        try
        {
            var preSnapPath = Path.Combine(folder.Root, "pre-snapshot.json");
            if (File.Exists(preSnapPath))
            {
                var json = await File.ReadAllTextAsync(preSnapPath, ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("head", out var h)) targetSha = h.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "rollback: failed to read pre-snapshot for {RunId}", parentRunId);
        }

        if (string.IsNullOrWhiteSpace(targetSha))
        {
            _logger.LogWarning("rollback: pre-snapshot SHA unknown, aborting");
            folder.WriteRollbackResult(new RollbackResult(parentRunId, "failed",
                headBefore, headBefore, DateTime.UtcNow, DateTime.UtcNow,
                "pre-snapshot.json missing or unparsable"));
            return;
        }

        SetPhase("rolling-back", $"resetting to {targetSha}", parentRunId, DateTime.UtcNow);
        var startedAt = DateTime.UtcNow;
        try
        {
            // Stop, reset, start.
            var (stopRc, stopOut) = await RunBashAsync(_options.StopScript, "", _options.DevspaceDir, ct);
            folder.WriteOutput("rollback-stop-output.txt", stopOut);
            // Continue regardless: a stable that's already down is not an error.

            var (rsRc, rsOut) = await RunBashAsync("-c", $"git reset --hard {targetSha}", _options.StableCheckoutDir, ct);
            folder.WriteOutput("rollback-reset-output.txt", rsOut);
            if (rsRc != 0)
            {
                folder.WriteRollbackResult(new RollbackResult(parentRunId, "failed",
                    headBefore, _git.HeadShort(), startedAt, DateTime.UtcNow,
                    $"git reset --hard rc={rsRc}"));
                return;
            }

            var headAfterReset = _git.HeadShort();
            _store.SetHead(headAfterReset);

            var (startRc, startOut) = await RunRestartAsync(ct);
            folder.WriteOutput("rollback-start-output.txt", startOut);
            var healthy = await _backend.WaitForHealthyAsync(TimeSpan.FromSeconds(_options.HealthWaitSeconds), ct);

            var status = (startRc == 0 && healthy) ? "ok" : "failed";
            var error = (startRc == 0 && healthy) ? null
                       : $"start rc={startRc} healthy={healthy}";

            folder.WriteRollbackResult(new RollbackResult(parentRunId, status,
                headBefore, headAfterReset, startedAt, DateTime.UtcNow, error));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "rollback crashed");
            folder.WriteRollbackResult(new RollbackResult(parentRunId, "failed",
                headBefore, _git.HeadShort(), startedAt, DateTime.UtcNow, ex.Message));
        }
    }

    // ─── snapshots ──────────────────────────────────────────────────────────

    private async Task<UpdateRunSnapshot> CaptureSnapshotAsync(string kind, string runId, string head, Dictionary<string, string> modes, CancellationToken ct)
    {
        var hz = await _backend.ProbeHealthzAsync(ct);
        var (runnerHttp, _) = await _backend.GetAsync("/api/runner/status", TimeSpan.FromSeconds(5), ct);
        var (jobsHttp, jobsBody) = await _backend.GetAsync("/api/jobs?limit=5", TimeSpan.FromSeconds(5), ct);
        var jobsCount = TryCountJobs(jobsBody);
        var (clientsHttp, clientsBody) = await _backend.GetAsync("/api/clients", TimeSpan.FromSeconds(5), ct);
        var clientsCount = TryCountClients(clientsBody);
        var (quotaHttp, _) = await _backend.GetAsync("/api/cli/quota", TimeSpan.FromSeconds(5), ct);

        return new UpdateRunSnapshot(
            Kind: kind,
            RunId: runId,
            CapturedAt: DateTime.UtcNow,
            Head: head,
            ProjectModes: modes,
            HealthzOk: hz.Ok,
            HealthzBody: hz.Body,
            RunnerStatusHttp: runnerHttp == 0 ? null : runnerHttp,
            JobsRecentCount: jobsCount,
            ClientsCount: clientsCount,
            CliQuotaReachable: quotaHttp == 200);
    }

    private static int? TryCountJobs(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                return doc.RootElement.GetArrayLength();
            if (doc.RootElement.TryGetProperty("jobs", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                return arr.GetArrayLength();
        }
        catch { }
        return null;
    }

    private static int? TryCountClients(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                return doc.RootElement.GetArrayLength();
            if (doc.RootElement.TryGetProperty("clients", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                return arr.GetArrayLength();
        }
        catch { }
        return null;
    }

    // ─── shell helpers ──────────────────────────────────────────────────────

    private async Task<(int Rc, string Output)> RunBashAsync(string firstArg, string secondArg, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.BashPath,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(firstArg);
        if (!string.IsNullOrEmpty(secondArg)) psi.ArgumentList.Add(secondArg);

        try
        {
            using var p = Process.Start(psi)!;
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (p.ExitCode, stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n--- stderr ---\n" + stderr));
        }
        catch (Exception ex)
        {
            return (-1, $"bash launch failed: {ex.Message}");
        }
    }

    private async Task<(int Rc, string Output, bool Ran)> MaybeRunNpmInstallAsync(string before, string after, CancellationToken ct)
    {
        // Best-effort detection: did frontend/package-lock.json change?
        var (diffRc, diffOut) = await RunBashAsync("-c",
            $"git diff --name-only {before} {after}",
            _options.StableCheckoutDir, ct);
        if (diffRc != 0) return (0, diffOut, false);
        if (!diffOut.Contains("frontend/package-lock.json", StringComparison.Ordinal))
            return (0, "package-lock.json unchanged; skipping", false);

        var (rc, output) = await RunBashAsync("-c",
            "cd frontend && npm install",
            _options.StableCheckoutDir, ct);
        return (rc, output, true);
    }

    private async Task<(int Rc, string Output)> RunRestartAsync(CancellationToken ct)
    {
        var (stopRc, stopOut) = await RunBashAsync(_options.StopScript, "", _options.DevspaceDir, ct);
        // Stop always succeeds in spirit; downstream start is what we care about.
        var (startRc, startOut) = await RunBashAsync("-c",
            $"DETACH=1 ./{_options.StartScript}",
            _options.DevspaceDir, ct);
        return (startRc, $"--- stop (rc={stopRc}) ---\n{stopOut}\n--- start (rc={startRc}) ---\n{startOut}");
    }

    // ─── store transitions ──────────────────────────────────────────────────

    private void SetPhase(string phase, string message, string? runId, DateTime? startedAt, DateTime? finishedAt = null,
        DateTime? lastSuccessAt = null, DateTime? lastRunFinishedAt = null,
        string? lastRunHeadBefore = null, string? lastRunHeadAfter = null,
        IReadOnlyList<VerificationFailure>? verificationFailures = null)
    {
        _store.SetPhase(phase, message, runId, startedAt, finishedAt, lastSuccessAt,
            lastRunFinishedAt, lastRunHeadBefore, lastRunHeadAfter, verificationFailures,
            phaseLabel: PhaseLabels.For(phase, message),
            autoRollbackEnabled: _options.AutoRollback);
    }

    private void FinishDone(string runId, DateTime startedAt, string headBefore, string headAfter, string message, IReadOnlyList<VerificationFailure>? failures)
    {
        var now = DateTime.UtcNow;
        SetPhase("done", message, runId, startedAt,
            finishedAt: now,
            lastSuccessAt: now,
            lastRunFinishedAt: now,
            lastRunHeadBefore: headBefore,
            lastRunHeadAfter: headAfter,
            verificationFailures: failures);
    }

    private void FinishFailed(string runId, DateTime startedAt, string headBefore, string headAfter, string trigger,
        string error, IReadOnlyList<VerificationFailure>? failures, RunFolder folder, UpdateRunSnapshot? preSnapshot)
    {
        var now = DateTime.UtcNow;
        FinishHistory(runId, startedAt, headBefore, headAfter, "failed", error, trigger, failures, null, folder.Root);

        // Always write a summary so the operator has a quick read.
        try
        {
            folder.WriteSummary(BuildSummaryMarkdown(runId, trigger, startedAt, headBefore, headAfter, preSnapshot, null, null, error));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "summary write failed for {RunId}", runId);
        }

        SetPhase("failed", error, runId, startedAt,
            finishedAt: now,
            lastRunFinishedAt: now,
            lastRunHeadBefore: headBefore,
            lastRunHeadAfter: headAfter,
            verificationFailures: failures);
    }

    private void FinishHistory(string runId, DateTime startedAt, string headBefore, string headAfter, string status,
        string? error, string trigger, IReadOnlyList<VerificationFailure>? failures, string? rollbackStatus, string? runFolder)
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
            Trigger: trigger,
            VerificationFailures: failures,
            RollbackStatus: rollbackStatus,
            RunFolder: runFolder);
        _store.AppendHistory(entry);
    }

    // ─── summary writer ─────────────────────────────────────────────────────

    private static string BuildSummaryMarkdown(string runId, string trigger, DateTime startedAt,
        string headBefore, string headAfter, UpdateRunSnapshot? pre, UpdateRunSnapshot? post,
        VerificationOutcome? verification, string? failureMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Update run {runId}");
        sb.AppendLine();
        sb.AppendLine($"- Trigger: `{trigger}`");
        sb.AppendLine($"- Started: {startedAt:O}");
        sb.AppendLine($"- HEAD before: `{headBefore}`");
        sb.AppendLine($"- HEAD after: `{headAfter}`");
        sb.AppendLine($"- Status: **{(failureMessage == null ? "ok" : "failed")}**");
        if (failureMessage != null) sb.AppendLine($"- Error: {failureMessage}");
        sb.AppendLine();

        if (verification != null && verification.Checks.Count > 0)
        {
            sb.AppendLine("## Verification");
            sb.AppendLine();
            sb.AppendLine("| Step | Ok | Observed | Duration |");
            sb.AppendLine("|------|----|----------|----------|");
            foreach (var c in verification.Checks)
                sb.AppendLine($"| {c.Step} | {(c.Ok ? "✓" : "✗")} | {c.Observed} | {c.DurationMs} ms |");
            sb.AppendLine();
        }

        if (pre != null)
        {
            sb.AppendLine("## Pre-snapshot");
            sb.AppendLine($"- Healthz: {(pre.HealthzOk ? "ok" : "fail")}");
            sb.AppendLine($"- Project modes: {string.Join(", ", pre.ProjectModes.Select(kv => $"{kv.Key}={kv.Value}"))}");
            sb.AppendLine();
        }
        if (post != null)
        {
            sb.AppendLine("## Post-snapshot");
            sb.AppendLine($"- Healthz: {(post.HealthzOk ? "ok" : "fail")}");
            sb.AppendLine($"- Project modes: {string.Join(", ", post.ProjectModes.Select(kv => $"{kv.Key}={kv.Value}"))}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string NewRunId() => Guid.NewGuid().ToString("N").Substring(0, 8);
}

internal static class PhaseLabels
{
    public static string? For(string phase, string? message)
    {
        return phase switch
        {
            "preparing"               => "Preparing snapshot",
            "pausing-runners"         => "Pausing runners",
            "pulling"                 => "Pulling and rebuilding",
            "building"                => "Building",
            "restarting"              => "Restarting backend",
            "verifying-after-restart" => "Verifying restart",
            "resuming"                => "Resuming runners",
            "rolling-back"            => "Rolling back",
            "done"                    => "Update verified",
            "failed"                  => "Update failed",
            "idle"                    => null,
            _                         => phase,
        };
    }
}
