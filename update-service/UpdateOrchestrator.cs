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
    private readonly IGitProbe _git;
    private readonly IBackendProbe _backend;
    private readonly UpdateVerifier _verifier;
    private readonly ReleasePreflightService _releasePreflight;
    private readonly UpdateServiceOptions _options;
    private readonly ILogger<UpdateOrchestrator> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateOrchestrator(
        UpdateStatusStore store,
        IGitProbe git,
        IBackendProbe backend,
        UpdateVerifier verifier,
        ReleasePreflightService releasePreflight,
        UpdateServiceOptions options,
        ILogger<UpdateOrchestrator> logger,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _git = git;
        _backend = backend;
        _verifier = verifier;
        _releasePreflight = releasePreflight;
        _options = options;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public (string RunId, string Phase, string Message) StartTrigger(string trigger, bool force, CancellationToken ct)
    {
        var (_, behindBy) = RefreshGitStatus();
        if (behindBy <= 0)
        {
            _logger.LogInformation("Update trigger ignored because stable is not behind origin (behindBy={BehindBy})", behindBy);
            return ("(none)", _store.Get().Phase, "already up to date");
        }

        if (!CanApplyForTrigger(trigger, force))
        {
            _logger.LogInformation(
                "Update trigger ignored because apply mode is manual (trigger={Trigger}, force={Force}, behindBy={BehindBy})",
                trigger, force, behindBy);
            return ("(none)", _store.Get().Phase, "manual apply mode");
        }

        if (!_gate.Wait(0, CancellationToken.None))
        {
            var current = _store.Get();
            if (!force) return (current.CurrentRunId ?? "(unknown)", current.Phase, "already running");
            var queuedRunId = NewRunId();
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync(ct);
                try { await RunOrchestrationAsync(queuedRunId, trigger, force, ct); }
                finally { _gate.Release(); }
            }, ct);
            return (queuedRunId, "preparing", "queued behind in-flight run");
        }

        var runId = NewRunId();
        var startedAt = DateTime.UtcNow;
        SetPhase("preparing", $"run {runId}: queued", runId, startedAt);

        _ = Task.Run(async () =>
        {
            try { await RunOrchestrationAsync(runId, trigger, force, ct); }
            finally { _gate.Release(); }
        }, ct);

        return (runId, "preparing", "accepted");
    }

    private bool CanApplyForTrigger(string trigger, bool force)
    {
        if (force) return true;
        if (!string.Equals(_options.Mode, "manual", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(trigger, "manual", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, "manual-rollback", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldQuiesceRunners()
        => !string.Equals(_options.Mode, "manual", StringComparison.OrdinalIgnoreCase);

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

    private async Task RunOrchestrationAsync(string runId, string trigger, bool force, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var folder = new RunFolder(_options.RunsDirectory, runId, _loggerFactory.CreateLogger<RunFolder>());

        var headBefore = _git.HeadShort();
        UpdateRunSnapshot? preSnapshot = null;
        Dictionary<string, string>? restoreModes = null;

        try
        {
            var (_, behindBy) = RefreshGitStatus();
            if (behindBy <= 0 && !force)
            {
                _logger.LogInformation(
                    "Update run {RunId} ignored at execution time because stable is not behind origin (behindBy={BehindBy})",
                    runId, behindBy);
                SetPhase("idle", "already up to date", null, null);
                return;
            }

            if (!CanApplyForTrigger(trigger, force))
            {
                _logger.LogInformation(
                    "Update run {RunId} ignored at execution time because apply mode is manual (trigger={Trigger}, force={Force}, behindBy={BehindBy})",
                    runId, trigger, force, behindBy);
                SetPhase("idle", "manual apply mode", null, null);
                return;
            }

            // PHASE 1 — preparing
            SetPhase("preparing", "snapshotting pre-state", runId, startedAt);
            ReleaseManifest? intendedRelease = null;
            ReleaseManifest? observedRelease = null;
            ReleaseComparison? releaseComparison = null;
            if (_options.RequireReleaseManifest)
            {
                var release = await _releasePreflight.EvaluateAsync(allowDowngrade: false, ct);
                releaseComparison = release;
                folder.WriteOutput("release-preflight.json", System.Text.Json.JsonSerializer.Serialize(release,
                    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true }));
                if (!release.Allowed)
                {
                    FinishFailed(runId, startedAt, headBefore, headBefore, trigger,
                        $"release preflight refused: {string.Join("; ", release.Errors)}", null, folder, preSnapshot);
                    return;
                }
                intendedRelease = release.Candidate;
                if (release.Installed is not null)
                    folder.WriteOutput("rollback-build-manifest.json", System.Text.Json.JsonSerializer.Serialize(release.Installed,
                        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true }));
                if (intendedRelease is not null)
                    folder.WriteOutput("intended-build-manifest.json", System.Text.Json.JsonSerializer.Serialize(intendedRelease,
                        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true }));
            }
            var preModes = await _backend.ReadProjectModesAsync(ct) ?? new Dictionary<string, string>();
            var shouldQuiesceRunners = ShouldQuiesceRunners();
            restoreModes = shouldQuiesceRunners ? new Dictionary<string, string>(preModes) : null;
            preSnapshot = await CaptureSnapshotAsync("pre", runId, headBefore, preModes, ct);
            folder.WriteSnapshot(preSnapshot);

            // PHASE 2 — pausing-runners
            if (shouldQuiesceRunners)
            {
                SetPhase("pausing-runners", $"pausing {preModes.Count} project runner(s)", runId, startedAt);
                foreach (var kv in preModes)
                    if (kv.Value != "manual")
                        await _backend.SetModeAsync(kv.Key, "manual", "update-quiesce", ct);
            }
            else
            {
                _logger.LogInformation(
                    "Update run {RunId} is applying without runner quiesce because update apply mode is manual",
                    runId);
            }

            // PHASE 3 — pulling
            SetPhase("pulling", "git fetch + merge --ff-only", runId, startedAt);
            var (pullRc, pullOut) = await RunBashAsync(
                "-c",
                $"git fetch origin main && git merge --ff-only FETCH_HEAD",
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
            SetPhase("building", "npm install (frontend deps)", runId, startedAt);
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

            if (intendedRelease is not null)
            {
                observedRelease = ReleasePreflightService.ToManifest(await _backend.ReadRuntimeVersionAsync(ct));
                if (observedRelease is null || !StableReleaseContract.IdentityEquals(observedRelease, intendedRelease))
                {
                    var failure = new VerificationFailure("runtime-identity", observedRelease?.Tag ?? "missing", intendedRelease.Tag);
                    FinishFailed(runId, startedAt, headBefore, headAfterPull, trigger,
                        "runtime identity does not equal intended build manifest", new[] { failure }, folder, preSnapshot);
                    if (_options.AutoRollback) await RunRollbackAsync(runId, manual: false, ct);
                    return;
                }
            }

            // PHASE 6 — verifying-after-restart
            SetPhase("verifying-after-restart", "running 6-check matrix", runId, startedAt);
            var verification = await _verifier.RunAsync(runId, preModes, folder.AppendVerification, ct);
            if (!verification.AllPassed)
            {
                var failedSteps = string.Join(", ", verification.Failures.Select(f => f.Step));
                var hint = verification.Failures.Any(f => f.Observed != null && f.Observed.Contains("no response"))
                    ? ". The backend may not have finished starting after the update"
                    : "";
                FinishFailed(runId, startedAt, headBefore, headAfterPull, trigger,
                    $"verification failed ({failedSteps}){hint}",
                    verification.Failures, folder, preSnapshot);

                if (_options.AutoRollback)
                    await RunRollbackAsync(runId, manual: false, ct);
                return;
            }

            // PHASE 7 — resuming
            if (restoreModes is not null)
            {
                SetPhase("resuming", "restoring runner modes", runId, startedAt);
                await RestoreRunnerModesAsync(restoreModes, folder, "resume-output.txt", ct);
                restoreModes = null;
            }

            // PHASE 8 — done
            var headAfter = _git.HeadShort();
            _store.SetHead(headAfter);
            var postModes = await _backend.ReadProjectModesAsync(ct) ?? new Dictionary<string, string>();
            var postSnapshot = await CaptureSnapshotAsync("post", runId, headAfter, postModes, ct);
            folder.WriteSnapshot(postSnapshot);
            folder.WriteSummary(BuildSummaryMarkdown(runId, trigger, startedAt, headBefore, headAfter, preSnapshot, postSnapshot, verification, null));

            FinishHistory(runId, startedAt, headBefore, headAfter, "ok", null, trigger, null, null, folder.Root,
                intendedRelease?.Tag, observedRelease?.Tag, releaseComparison?.Direction.ToString(), intendedRelease?.Integrity);
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
        finally
        {
            if (restoreModes is not null)
                await RestoreRunnerModesAsync(restoreModes, folder, "resume-output.txt", CancellationToken.None);
        }
    }

    // ─── rollback ───────────────────────────────────────────────────────────

    private async Task RunRollbackAsync(string parentRunId, bool manual, CancellationToken ct)
    {
        var folder = new RunFolder(_options.RunsDirectory, parentRunId, _loggerFactory.CreateLogger<RunFolder>());
        var headBefore = _git.HeadShort();
        var rollbackStartedAt = DateTime.UtcNow;
        var rollbackTrigger = manual ? "manual-rollback" : "auto-rollback";

        // Read the snapshot SHA + project modes from disk so manual rollback
        // works even after a process restart. Modes feed phase-6 (strict
        // matrix needs the pre-snapshot project list) and phase-7 (resume
        // restores each project's pre-update mode).
        var (targetSha, preModes) = await ReadPreSnapshotAsync(folder.Root, ct);

        if (string.IsNullOrWhiteSpace(targetSha))
        {
            _logger.LogWarning("rollback: pre-snapshot SHA unknown, aborting");
            var missing = new RollbackResult(parentRunId, "failed",
                headBefore, headBefore, rollbackStartedAt, DateTime.UtcNow,
                "pre-snapshot.json missing or unparsable", VerificationFailures: null);
            folder.WriteRollbackResult(missing);
            AppendRollbackHistory(parentRunId, rollbackTrigger, rollbackStartedAt, headBefore, headBefore, missing, folder.Root);
            return;
        }

        SetPhase("rolling-back", $"resetting to {targetSha}", parentRunId, DateTime.UtcNow);
        var startedAt = rollbackStartedAt;
        try
        {
            // PHASE 5' — stop + git reset + start.
            var (stopRc, stopOut) = await RunBashAsync(_options.StopScript, "", _options.DevspaceDir, ct);
            folder.WriteOutput("rollback-stop-output.txt", stopOut);
            // Continue regardless: a stable that's already down is not an error.

            var (rsRc, rsOut) = await RunBashAsync("-c", $"git reset --hard {targetSha}", _options.StableCheckoutDir, ct);
            folder.WriteOutput("rollback-reset-output.txt", rsOut);
            if (rsRc != 0)
            {
                var failedReset = new RollbackResult(parentRunId, "failed",
                    headBefore, _git.HeadShort(), startedAt, DateTime.UtcNow,
                    $"git reset --hard rc={rsRc}", VerificationFailures: null);
                folder.WriteRollbackResult(failedReset);
                AppendRollbackHistory(parentRunId, rollbackTrigger, startedAt, headBefore, _git.HeadShort(), failedReset, folder.Root);
                return;
            }

            var headAfterReset = _git.HeadShort();
            _store.SetHead(headAfterReset);

            var (startRc, startOut) = await RunRestartAsync(ct);
            folder.WriteOutput("rollback-start-output.txt", startOut);
            var healthy = await _backend.WaitForHealthyAsync(TimeSpan.FromSeconds(_options.HealthWaitSeconds), ct);

            if (startRc != 0 || !healthy)
            {
                var failedStart = new RollbackResult(parentRunId, "failed",
                    headBefore, headAfterReset, startedAt, DateTime.UtcNow,
                    $"start rc={startRc} healthy={healthy}",
                    VerificationFailures: new[]
                    {
                        new VerificationFailure(
                            "healthz-stable",
                            $"start rc={startRc} healthy={healthy} after {_options.HealthWaitSeconds}s wait",
                            "/healthz=200 after rollback restart")
                    });
                folder.WriteRollbackResult(failedStart);
                AppendRollbackHistory(parentRunId, rollbackTrigger, startedAt, headBefore, headAfterReset, failedStart, folder.Root);
                return;
            }

            // PHASE 6' — re-run the strict 6-check matrix against the reverted
            // backend. ADR-0031 reissue-2026-05-11: rollback must clear the
            // same bar as a forward run; healthz alone is not enough.
            SetPhase("rolling-back", "verifying after rollback", parentRunId, startedAt);
            var verification = await _verifier.RunAsync(parentRunId, preModes, folder.AppendRollbackVerification, ct);

            if (!verification.AllPassed)
            {
                _logger.LogWarning("rollback verification failed: {Steps}",
                    string.Join(",", verification.Failures.Select(f => f.Step)));
                var failedMatrix = new RollbackResult(parentRunId, "failed",
                    headBefore, headAfterReset, startedAt, DateTime.UtcNow,
                    "verification after rollback failed",
                    VerificationFailures: verification.Failures);
                folder.WriteRollbackResult(failedMatrix);
                AppendRollbackHistory(parentRunId, rollbackTrigger, startedAt, headBefore, headAfterReset, failedMatrix, folder.Root);
                return;
            }

            // PHASE 7' — resume runners using the pre-snapshot modes.
            SetPhase("rolling-back", "restoring runner modes", parentRunId, startedAt);
            await RestoreRunnerModesAsync(preModes, folder, "rollback-resume-output.txt", ct);

            var okResult = new RollbackResult(parentRunId, "ok",
                headBefore, headAfterReset, startedAt, DateTime.UtcNow,
                null, VerificationFailures: null);
            folder.WriteRollbackResult(okResult);
            AppendRollbackHistory(parentRunId, rollbackTrigger, startedAt, headBefore, headAfterReset, okResult, folder.Root);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "rollback crashed");
            var crashed = new RollbackResult(parentRunId, "failed",
                headBefore, _git.HeadShort(), startedAt, DateTime.UtcNow,
                ex.Message, VerificationFailures: null);
            folder.WriteRollbackResult(crashed);
            AppendRollbackHistory(parentRunId, rollbackTrigger, startedAt, headBefore, _git.HeadShort(), crashed, folder.Root);
        }
    }

    private void AppendRollbackHistory(string parentRunId, string trigger, DateTime startedAt,
        string headBefore, string headAfter, RollbackResult result, string runFolderRoot)
    {
        // ADR-0031 reissue-2026-05-11: emit a dedicated history row for the
        // rollback so dashboards + the integration suite can observe the
        // rollback outcome without parsing rollback-result.json. RunId
        // matches the parent so the rollback row stays linked to its
        // forward run; trigger distinguishes auto vs. manual.
        var entry = new UpdateHistoryEntry(
            RunId: parentRunId,
            StartedAt: startedAt,
            FinishedAt: result.FinishedAt,
            Status: result.Status,
            HeadBefore: headBefore,
            HeadAfter: headAfter,
            DurationSeconds: (int)(result.FinishedAt - startedAt).TotalSeconds,
            Error: result.Error,
            Trigger: trigger,
            VerificationFailures: result.VerificationFailures,
            RollbackStatus: result.Status,
            RunFolder: runFolderRoot);
        _store.AppendHistory(entry);
    }

    private async Task<(string? Sha, Dictionary<string, string> Modes)> ReadPreSnapshotAsync(string runFolderRoot, CancellationToken ct)
    {
        var preSnapPath = Path.Combine(runFolderRoot, "pre-snapshot.json");
        if (!File.Exists(preSnapPath)) return (null, new Dictionary<string, string>());
        try
        {
            var json = await File.ReadAllTextAsync(preSnapPath, ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? sha = root.TryGetProperty("head", out var h) ? h.GetString() : null;
            var modes = new Dictionary<string, string>();
            if (root.TryGetProperty("projectModes", out var pm) && pm.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in pm.EnumerateObject())
                {
                    var v = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String ? prop.Value.GetString() : null;
                    if (!string.IsNullOrEmpty(v)) modes[prop.Name] = v;
                }
            }
            return (sha, modes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "rollback: failed to read pre-snapshot at {Path}", preSnapPath);
            return (null, new Dictionary<string, string>());
        }
    }

    private (string Origin, int BehindBy) RefreshGitStatus()
    {
        var head = _git.HeadShort();
        if (!string.IsNullOrEmpty(head)) _store.SetHead(head);

        var (origin, behindBy) = _git.FetchAndCompare();
        if (!string.IsNullOrEmpty(origin))
        {
            var pending = behindBy > 0 ? _git.PendingCommits(50) : Array.Empty<CommitInfo>();
            _store.SetFetchResult(origin, behindBy, pending);
        }

        return (origin, behindBy);
    }

    private async Task RestoreRunnerModesAsync(
        IReadOnlyDictionary<string, string> modes,
        RunFolder folder,
        string outputName,
        CancellationToken ct)
    {
        var resumeOut = new StringBuilder();
        foreach (var (project, prev) in modes)
        {
            var ok = await _backend.SetModeAsync(project, prev, "update-resume", ct);
            resumeOut.AppendLine($"{project} -> {prev}: {(ok ? "ok" : "FAIL")}");
        }
        folder.WriteOutput(outputName, resumeOut.ToString());
    }

    // ─── snapshots ──────────────────────────────────────────────────────────

    private async Task<UpdateRunSnapshot> CaptureSnapshotAsync(string kind, string runId, string head, Dictionary<string, string> modes, CancellationToken ct)
    {
        var hz = await _backend.ProbeHealthzAsync(ct);
        var (runnerHttp, _) = await _backend.GetAsync("/api/runner/status", TimeSpan.FromSeconds(5), ct);
        var (jobsHttp, jobsBody) = await _backend.GetAsync("/api/tasks?limit=5", TimeSpan.FromSeconds(5), ct);
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
        // Always run npm install (idempotent: fast when the dep tree already
        // matches the lock, installs anything missing otherwise). The previous
        // "only if frontend/package-lock.json changed in this pull" optimisation
        // skipped installs when node_modules had drifted from the lock (a dep
        // added in a commit window a prior update skipped), leaving the frontend
        // build failing on a missing module after a successful-looking update
        // (the 2026-06-02 @microsoft/signalr incident). npm install IS the cheap
        // check-and-fix; a non-zero rc aborts the update before the restart.
        _ = before;
        _ = after;
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
        string? error, string trigger, IReadOnlyList<VerificationFailure>? failures, string? rollbackStatus, string? runFolder,
        string? intendedTag = null, string? observedTag = null, string? releaseDirection = null, string? manifestIntegrity = null)
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
            RunFolder: runFolder,
            IntendedTag: intendedTag,
            ObservedTag: observedTag,
            ReleaseDirection: releaseDirection,
            ManifestIntegrity: manifestIntegrity);
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
