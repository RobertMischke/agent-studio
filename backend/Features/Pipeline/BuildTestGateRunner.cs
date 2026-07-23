using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Git;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

public enum BuildTestGateVerdict
{
    Skipped,
    Ok,
    Warn,
    Fail,
}

public enum BuildTestGateFailureKind
{
    None,
    Code,
    Lock,
    Timeout,
    OutOfMemory,
    ProcessLaunch,
    Cancellation,
    MissingSource,
    ReviewModel,
}

public sealed record BuildTestGateRequest(
    string RepositoryPath,
    string? ExpectedSha,
    string Executor,
    bool RequireExactSubject = true)
{
    public string GateId { get; init; } = PipelineCatalogue.BuildTestGateStepId;
    public string? AttemptChainId { get; init; }
    public string? SubjectRef { get; init; }
    public string? Project { get; init; }
    public string? JobId { get; init; }
    public string Lane { get; init; } = TaskStates.AutoReview;
    public string? RequiredTestLevel { get; init; }
    public TestExecutionPolicy? TestExecution { get; init; }
    public string? JobFolderPath { get; init; }

    /// <summary>
    /// SSH host alias of the project's remote execution host. When set, the gate
    /// FIRST tries to run its verify commands remotely on that host (ssh-gate
    /// bridge, AGT-2222 tranche 2): the subject SHA is located in the host's
    /// per-project repos, materialized into a disposable worktree there, and the
    /// build-profile commands run over ssh - freeing the local machine from
    /// build/test load. Any remote infrastructure problem falls back to the
    /// local path. The bridge is superseded by claimable remote gate steps
    /// (AGT-2229) and then removed wholesale.
    /// </summary>
    public string? RemoteSshHost { get; init; }

    /// <summary>
    /// Budget for the true infrastructure operations that MUST be quick regardless
    /// of how long a verify run takes: materializing the exact-subject worktree
    /// (fetch + <c>worktree add</c>), reading HEAD, and tearing the worktree down.
    /// </summary>
    public TimeSpan InfrastructureTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Budget for WAITING in the machine-gate queue for the one running gate ahead
    /// to finish. This is deliberately separate from <see cref="InfrastructureTimeout"/>:
    /// the machine lock is held for a gate's entire build+test run (15-25 min in
    /// production), so a queued card must be willing to wait roughly one full run,
    /// not the short infra-op budget. Budgeting the queue wait against the infra SLA
    /// made every card queued behind a running gate escalate with a spurious
    /// "Timeout persisted" after 120 s (AGT-2182, 21.07.). When unset the runner
    /// derives run-timeout + infra-timeout.
    /// </summary>
    public TimeSpan? QueueWaitTimeout { get; init; }
}

public sealed record BuildTestGateProcessEvidence
{
    public string Command { get; init; } = "";
    public string FileName { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public int? ExitCode { get; init; }
    public string? TerminationSignal { get; init; }
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    public string? LaunchError { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
}

public sealed record BuildTestGateFinding(
    string Kind,
    string Scope,
    string Command,
    string Reason,
    int? ExitCode,
    string Evidence);

public sealed record BuildTestGateResult(
    BuildTestGateVerdict Verdict,
    int? ExitCode,
    long DurationMs,
    string Output,
    string Reason,
    bool RanBackendBuild,
    bool RanFrontendBuild)
{
    public string? GateRunId { get; init; }
    public DateTimeOffset? GateStartedAtUtc { get; init; }
    public DateTimeOffset? GateCompletedAtUtc { get; init; }
    public long GateQueueWaitMs { get; init; }
    public bool GateCollisionDetected { get; init; }
    public string GateId { get; init; } = PipelineCatalogue.BuildTestGateStepId;
    public string? Repository { get; init; }
    public string? ExpectedSha { get; init; }
    public string? TestedSha { get; init; }
    public string? AttemptChainId { get; init; }
    public string? Executor { get; init; }
    public string? Workspace { get; init; }
    public string? TerminationSignal { get; init; }
    public BuildTestGateFailureKind FailureKind { get; init; }
    public string? FailureFingerprint { get; init; }
    public IReadOnlyList<BuildTestGateProcessEvidence> Processes { get; init; } = [];
    public TestSelectionAudit? TestSelection { get; init; }
    public IReadOnlyList<BuildTestGateFinding> Findings { get; init; } = [];
    public bool IsInfrastructureFailure => FailureKind is not BuildTestGateFailureKind.None
        and not BuildTestGateFailureKind.Code;
}

public interface IBuildTestGateRunner
{
    Task<BuildTestGateResult> RunAsync(
        BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles,
        BuildProfile? profile,
        PostStepMode mode,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>
/// Runs deterministic verification against one exact Git subject. Real command
/// loops are serialized by one machine-wide lock without reducing coding slots.
/// The Task Server checkout only supplies Git objects and is never a command
/// workspace.
/// </summary>
public sealed class BuildTestGateRunner : IBuildTestGateRunner
{
    public const int MaxOutputLines = 300;

    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    internal static readonly string MachineGateLockPath = Path.Combine(
        Path.GetTempPath(), "agentstudio-build-test-gate.lock");
    internal static readonly string ReviewWorkspaceRoot = Path.Combine(
        Path.GetTempPath(), "agentstudio-review-gates");

    private static readonly Regex SafeSha = new(
        "^[0-9a-fA-F]{40,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VolatileHex = new(
        "\\b[0-9a-fA-F]{7,64}\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VolatileNumber = new(
        "\\b\\d+\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(
        "\\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] CodeExtensions =
    [
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets",
        ".ts", ".html", ".scss", ".css", ".json", ".mjs", ".js",
    ];

    private readonly ILogger<BuildTestGateRunner> _logger;
    private readonly ILoadThrottleGate? _loadThrottle;
    private readonly ITestSelectionAdvisor? _testSelectionAdvisor;

    public BuildTestGateRunner(
        ILogger<BuildTestGateRunner> logger,
        ILoadThrottleGate? loadThrottle = null,
        ITestSelectionAdvisor? testSelectionAdvisor = null)
    {
        _logger = logger;
        _loadThrottle = loadThrottle;
        _testSelectionAdvisor = testSelectionAdvisor;
    }

    public async Task<BuildTestGateResult> RunAsync(
        BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles,
        BuildProfile? profile,
        PostStepMode mode,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (mode == PostStepMode.Off) return Skipped("mode=off");
        var requestedLevel = TestSelectionPlanner.ResolveLevel(
            request.TestExecution, request.Lane, request.RequiredTestLevel);
        var hasContinuousBaseline = request.TestExecution?.ContinuousCommands?
            .Any(command => !string.IsNullOrWhiteSpace(command)) == true;
        if (changedFiles is { Count: > 0 }
            && !HasCodeDiff(changedFiles)
            && requestedLevel != TestExecutionLevels.Full
            && !hasContinuousBaseline)
            return Skipped("no code diff");

        var repositoryPath = Path.GetFullPath(request.RepositoryPath);
        var gateRunId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var infrastructureTimeout = request.InfrastructureTimeout > TimeSpan.Zero
            ? request.InfrastructureTimeout
            : TimeSpan.FromMinutes(2);
        var queueWaitTimeout = ResolveQueueWaitTimeout(
            request.QueueWaitTimeout, timeout, infrastructureTimeout);
        _logger.LogInformation(
            "build_test_gate_started gate_run_id={GateRunId} gate_id={GateId} started_at_utc={StartedAtUtc:o} repository={Repository} expected_sha={ExpectedSha} attempt_chain_id={AttemptChainId} executor={Executor}",
            gateRunId, request.GateId, startedAt, repositoryPath,
            request.ExpectedSha ?? "missing", request.AttemptChainId ?? "missing", request.Executor);

        MachineGateLease? machineLease = null;
        ExactWorkspaceLease? workspaceLease = null;
        BuildTestGateResult? completed = null;
        string? workspace = null;
        string? testedSha = null;
        long fallbackQueueWaitMs = 0;
        var fallbackCollision = false;
        try
        {
            // ssh-gate bridge (AGT-2222 tranche 2): try the remote host first.
            // Success or a genuine remote verdict short-circuits the local path
            // entirely - no local machine lock, no local build/test load. Only
            // remote INFRASTRUCTURE problems fall through to the local path.
            if (request.RemoteSshHost is { Length: > 0 }
                && request.RequireExactSubject
                && request.ExpectedSha is { } remoteSha
                && SafeSha.IsMatch(remoteSha))
            {
                var remotePlan = VerifyCommandPlanner.Plan(repositoryPath, profile);
                List<VerifyCommand> remoteCommands = remotePlan.IsEmpty
                    ? new()
                    : remotePlan.Commands.Where(c => ShouldRunForChange(c, changedFiles)).ToList();
                if (remoteCommands.Count > 0)
                {
                    var remote = await TryRunRemoteAsync(
                        request.RemoteSshHost, remoteSha, gateRunId, remoteCommands,
                        remotePlan.Source, mode, timeout, queueWaitTimeout,
                        infrastructureTimeout, ct).ConfigureAwait(false);
                    if (remote is not null)
                    {
                        completed = remote.Result;
                        workspace = remote.Workspace;
                        testedSha = remote.TestedSha;
                        fallbackQueueWaitMs = remote.QueueWaitMs;
                    }
                }
            }

            if (completed is null && _loadThrottle is not null)
            {
                await _loadThrottle.WaitUntilReadyAsync(
                    $"build-test-gate:{Path.GetFileName(repositoryPath)}", ct).ConfigureAwait(false);
            }

            if (completed is null)
            {
                var acquisition = await AcquireMachineGateAsync(queueWaitTimeout, ct).ConfigureAwait(false);
                fallbackQueueWaitMs = acquisition.QueueWaitMs;
                fallbackCollision = acquisition.CollisionDetected;
                if (acquisition.Lease is null)
                {
                    completed = InfrastructureFailure(
                        BuildTestGateFailureKind.Timeout, acquisition.Reason, acquisition.Reason);
                }
                else
                {
                    machineLease = acquisition.Lease;
                    _logger.LogInformation(
                        "build_test_gate_acquired gate_run_id={GateRunId} repository={Repository} collision={CollisionDetected} queue_wait_ms={QueueWaitMs}",
                        gateRunId, repositoryPath, machineLease.CollisionDetected, machineLease.QueueWaitMs);
                }
            }

            if (completed is null && request.RequireExactSubject)
            {
                var prepared = await PrepareExactWorkspaceAsync(
                    repositoryPath, request.ExpectedSha, request.SubjectRef, gateRunId,
                    infrastructureTimeout, ct).ConfigureAwait(false);
                if (prepared.Lease is null)
                {
                    completed = InfrastructureFailure(
                        prepared.FailureKind, prepared.Reason, prepared.Output);
                }
                else
                {
                    workspaceLease = prepared.Lease;
                    workspace = workspaceLease.Path;
                    testedSha = workspaceLease.TestedSha;
                }
            }
            else if (completed is null && !Directory.Exists(repositoryPath))
            {
                completed = InfrastructureFailure(
                    BuildTestGateFailureKind.MissingSource,
                    $"repository not found: {repositoryPath}", string.Empty);
            }
            else if (completed is null)
            {
                workspace = repositoryPath;
                testedSha = await ReadHeadShaAsync(repositoryPath, infrastructureTimeout, ct).ConfigureAwait(false);
            }

            if (completed is null)
            {
                var plan = VerifyCommandPlanner.Plan(workspace!, profile);
                if (plan.IsEmpty)
                {
                    _logger.LogInformation(
                        "BuildTestGateRunner: no verify commands derivable for {Repo}; gate runs without a build check",
                        workspace);
                    completed = Skipped("no verify commands derivable");
                }
                else
                {
                    var staged = TestSelectionPlanner.Plan(
                        workspace!, plan, changedFiles, request.TestExecution,
                        request.Lane, request.RequiredTestLevel);
                    if (_testSelectionAdvisor is not null
                        && staged.Audit.Level == TestExecutionLevels.WorkPackage
                        && staged.Audit.Candidates.Count > 0)
                    {
                        var advice = await _testSelectionAdvisor.AdviseAsync(
                            staged.Audit, request.TestExecution, workspace!,
                            request.Project, request.JobId, request.JobFolderPath, ct).ConfigureAwait(false);
                        if (advice is not null)
                        {
                            staged = TestSelectionPlanner.Plan(
                                workspace!, plan, changedFiles, request.TestExecution,
                                request.Lane, request.RequiredTestLevel, advice);
                        }
                    }
                    var commands = staged.Commands.Where(c => ShouldRunForChange(c, changedFiles)).ToList();
                    completed = commands.Count == 0
                        ? Skipped($"no verify commands apply to the changed files ({plan.Source}); level={staged.Audit.Level}")
                            with { TestSelection = staged.Audit }
                        : await RunCommandsAsync(workspace!, commands, plan.Source, mode, timeout, ct)
                            .ConfigureAwait(false);
                    var completedAudit = CompleteAudit(staged.Audit, commands, completed.Processes);
                    completed = completed with
                    {
                        TestSelection = completedAudit,
                        Reason = CoverageReason(completed.Reason, completedAudit),
                    };
                }
            }

            if (workspaceLease is not null)
            {
                var cleanupError = await workspaceLease.RemoveAsync(
                    infrastructureTimeout, CancellationToken.None).ConfigureAwait(false);
                workspaceLease = null;
                if (cleanupError is not null)
                {
                    completed = InfrastructureFailure(
                        cleanupError.FailureKind,
                        "exact review workspace cleanup failed after bounded retries",
                        cleanupError.Evidence) with { Processes = completed.Processes };
                }
            }

            completed = completed with
            {
                GateRunId = gateRunId,
                GateId = request.GateId,
                GateStartedAtUtc = startedAt,
                GateCompletedAtUtc = DateTimeOffset.UtcNow,
                GateQueueWaitMs = machineLease?.QueueWaitMs ?? fallbackQueueWaitMs,
                GateCollisionDetected = machineLease?.CollisionDetected ?? fallbackCollision,
                Repository = repositoryPath,
                ExpectedSha = request.ExpectedSha,
                TestedSha = testedSha,
                AttemptChainId = request.AttemptChainId,
                Executor = request.Executor,
                Workspace = workspace,
            };
            return completed;
        }
        finally
        {
            if (workspaceLease is not null)
                await workspaceLease.RemoveBestEffortAsync(infrastructureTimeout).ConfigureAwait(false);
            var completedAt = completed?.GateCompletedAtUtc ?? DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "build_test_gate_completed gate_run_id={GateRunId} gate_id={GateId} completed_at_utc={CompletedAtUtc:o} repository={Repository} expected_sha={ExpectedSha} tested_sha={TestedSha} attempt_chain_id={AttemptChainId} executor={Executor} workspace={Workspace} verdict={Verdict} exit={ExitCode} signal={Signal} failure_kind={FailureKind} failure_fingerprint={FailureFingerprint} collision={CollisionDetected} queue_wait_ms={QueueWaitMs}",
                gateRunId, request.GateId, completedAt, repositoryPath,
                request.ExpectedSha ?? "missing", completed?.TestedSha ?? testedSha ?? "missing",
                request.AttemptChainId ?? "missing", request.Executor,
                completed?.Workspace ?? workspace ?? "missing", completed?.Verdict.ToString() ?? "interrupted",
                completed?.ExitCode?.ToString() ?? "n/a", completed?.TerminationSignal ?? "n/a",
                completed?.FailureKind.ToString() ?? BuildTestGateFailureKind.Cancellation.ToString(),
                completed?.FailureFingerprint ?? "none",
                machineLease?.CollisionDetected ?? false, machineLease?.QueueWaitMs ?? 0);
            machineLease?.Dispose();
        }
    }

    // ================= ssh-gate bridge (AGT-2222 tranche 2) =================
    // Deliberately self-contained so the cleanup card can remove the whole
    // region plus the RemoteSshHost request field once claimable remote gate
    // steps (AGT-2229) supersede it. Concurrency: the remote host runs up to
    // RemoteGateSlots verify runs in parallel - independent of the local
    // machine lock, which remote gates never touch.

    private const int RemoteGateSlots = 4;
    private static readonly SemaphoreSlim RemoteGate = new(RemoteGateSlots, RemoteGateSlots);

    private sealed record RemoteGateOutcome(
        BuildTestGateResult Result, string Workspace, string TestedSha, long QueueWaitMs);

    private async Task<RemoteGateOutcome?> TryRunRemoteAsync(
        string sshHost,
        string sha,
        string gateRunId,
        IReadOnlyList<VerifyCommand> commands,
        string planSource,
        PostStepMode mode,
        TimeSpan timeout,
        TimeSpan queueWaitTimeout,
        TimeSpan infrastructureTimeout,
        CancellationToken ct)
    {
        var queueWait = Stopwatch.StartNew();
        if (!await RemoteGate.WaitAsync(queueWaitTimeout, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "remote_gate_queue_timeout gate_run_id={GateRunId} host={Host}; falling back to the local gate",
                gateRunId, sshHost);
            return null;
        }
        queueWait.Stop();

        var worktree = $"$HOME/gate-work/{gateRunId}";
        string? repo = null;
        try
        {
            // 1. Locate a host repo that already knows the subject SHA (second
            //    pass fetches, in case the branch was pushed after the host's
            //    last fetch). No hit -> local fallback.
            var discover = await RunSshCaptureAsync(sshHost,
                "for d in $HOME/runner-work/*/repo; do if git -C \"$d\" cat-file -e " + sha + "^{commit} 2>/dev/null; then echo \"FOUND:$d\"; exit 0; fi; done; " +
                "for d in $HOME/runner-work/*/repo; do git -C \"$d\" fetch -q origin >/dev/null 2>&1; if git -C \"$d\" cat-file -e " + sha + "^{commit} 2>/dev/null; then echo \"FOUND:$d\"; exit 0; fi; done; echo NONE",
                infrastructureTimeout, ct).ConfigureAwait(false);
            var found = discover.Stdout.Split('\n', StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.StartsWith("FOUND:", StringComparison.Ordinal));
            if (discover.ExitCode != 0 || found is null)
            {
                _logger.LogInformation(
                    "remote_gate_subject_not_on_host gate_run_id={GateRunId} host={Host} sha={Sha} exit={Exit}; falling back to the local gate",
                    gateRunId, sshHost, sha, discover.ExitCode);
                return null;
            }
            repo = found["FOUND:".Length..];

            // 2. Disposable worktree at the exact SHA; verify HEAD before testing.
            var prep = await RunSshCaptureAsync(sshHost,
                $"git -C \"{repo}\" worktree add --detach --force \"{worktree}\" {sha} >/dev/null 2>&1 && git -C \"{worktree}\" rev-parse HEAD",
                infrastructureTimeout, ct).ConfigureAwait(false);
            var testedSha = prep.Stdout.Trim().Split('\n').LastOrDefault()?.Trim();
            if (prep.ExitCode != 0 || !string.Equals(testedSha, sha, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "remote_gate_worktree_failed gate_run_id={GateRunId} host={Host} exit={Exit} head={Head}; falling back to the local gate",
                    gateRunId, sshHost, prep.ExitCode, testedSha ?? "n/a");
                return null;
            }

            _logger.LogInformation(
                "remote_gate_started gate_run_id={GateRunId} host={Host} repo={Repo} sha={Sha} queue_wait_ms={QueueWaitMs}",
                gateRunId, sshHost, repo, sha, queueWait.ElapsedMilliseconds);

            // 3. Verify commands over ssh - same loop shape, verdicts and
            //    fingerprints as the local RunCommandsAsync.
            var sw = Stopwatch.StartNew();
            var output = new RingOutput(MaxOutputLines);
            var evidence = new List<BuildTestGateProcessEvidence>();
            output.AppendLine($"# verify plan: {planSource} ({commands.Count} command(s)) [remote: {sshHost}]");
            var ranBackend = false;
            var ranFrontend = false;
            BuildTestGateResult result;

            foreach (var command in commands)
            {
                if (command.Ecosystem == VerifyEcosystem.Node) ranFrontend = true;
                else ranBackend = true;
                var remoteDir = string.IsNullOrWhiteSpace(command.WorkingSubdir) || command.WorkingSubdir == "."
                    ? worktree
                    : $"{worktree}/{command.WorkingSubdir.Replace('\\', '/')}";
                output.AppendLine($"# working directory: {sshHost}:{remoteDir}");

                var remaining = Remaining(timeout, sw.Elapsed);
                var script = $"cd \"{remoteDir}\" && {command.Command}";
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
                // Remote-side `timeout` mirrors the local budget so an ssh kill
                // never leaves an orphaned build burning the host.
                var sshCommand = $"echo {b64} | base64 -d | timeout {(int)Math.Max(30, remaining.TotalSeconds)} bash -l";
                var process = await RunProcessAsync(
                    Path.GetTempPath(), command.Command, "ssh",
                    ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", sshHost, sshCommand],
                    remaining, output, ct).ConfigureAwait(false);
                evidence.Add(process);
                if (process.ExitCode != 0 || process.TimedOut || process.Cancelled || process.LaunchError is not null)
                {
                    sw.Stop();
                    var kind = ClassifyFailure(process);
                    var verdict = kind == BuildTestGateFailureKind.Code && mode != PostStepMode.Fail
                        ? BuildTestGateVerdict.Warn
                        : BuildTestGateVerdict.Fail;
                    var reason = $"{Describe(command)} exit {process.ExitCode?.ToString() ?? "n/a"} [remote:{sshHost}]";
                    result = WithFailure(new BuildTestGateResult(
                        verdict, process.ExitCode, sw.ElapsedMilliseconds, output.Text,
                        reason, ranBackend, ranFrontend)
                    {
                        Processes = evidence,
                        TerminationSignal = process.TerminationSignal,
                    }, kind);
                    return new RemoteGateOutcome(result, $"{sshHost}:{worktree}", sha, queueWait.ElapsedMilliseconds);
                }
            }

            sw.Stop();
            result = new BuildTestGateResult(
                BuildTestGateVerdict.Ok, 0, sw.ElapsedMilliseconds, output.Text,
                $"verify gate passed ({planSource}) [remote:{sshHost}]", ranBackend, ranFrontend)
            {
                Processes = evidence,
            };
            return new RemoteGateOutcome(result, $"{sshHost}:{worktree}", sha, queueWait.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "remote_gate_failed gate_run_id={GateRunId} host={Host}; falling back to the local gate",
                gateRunId, sshHost);
            return null;
        }
        finally
        {
            if (repo is not null)
            {
                try
                {
                    await RunSshCaptureAsync(sshHost,
                        $"git -C \"{repo}\" worktree remove --force \"{worktree}\" >/dev/null 2>&1; git -C \"{repo}\" worktree prune >/dev/null 2>&1; rm -rf \"{worktree}\"",
                        infrastructureTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SilentCatch.Note(ex, "BuildTestGateRunner: remote gate worktree cleanup");
                }
            }
            RemoteGate.Release();
        }
    }

    private sealed record SshCaptureResult(int? ExitCode, string Stdout);

    /// <summary>Small capture runner for the bridge's infra scripts (discovery,
    /// worktree, cleanup); verify commands go through <see cref="RunProcessAsync"/>
    /// instead so their output lands in the gate evidence.</summary>
    private static async Task<SshCaptureResult> RunSshCaptureAsync(
        string sshHost, string script, TimeSpan timeout, CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", sshHost,
            $"echo {b64} | base64 -d | timeout {(int)Math.Max(15, timeout.TotalSeconds)} bash -l",
        }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("ssh process failed to start");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout + TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { SilentCatch.Note(ex, "BuildTestGateRunner: ssh capture kill"); }
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);
        return new SshCaptureResult(process.HasExited ? process.ExitCode : null, stdout);
    }

    // =============== end ssh-gate bridge (AGT-2222 tranche 2) ===============

    private async Task<BuildTestGateResult> RunCommandsAsync(
        string repositoryPath,
        IReadOnlyList<VerifyCommand> commands,
        string planSource,
        PostStepMode mode,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var output = new RingOutput(MaxOutputLines);
        var evidence = new List<BuildTestGateProcessEvidence>();
        var findings = new List<BuildTestGateFinding>();
        output.AppendLine($"# verify plan: {planSource} ({commands.Count} command(s))");
        var ranBackend = false;
        var ranFrontend = false;

        foreach (var command in commands)
        {
            var workingDirectory = ResolveWorkingDirectory(repositoryPath, command);
            if (!Directory.Exists(workingDirectory))
            {
                return WithFailure(new BuildTestGateResult(
                    BuildTestGateVerdict.Fail, null, sw.ElapsedMilliseconds, output.Text,
                    $"verify command directory is missing: {workingDirectory}", ranBackend, ranFrontend)
                {
                    Processes = evidence,
                }, BuildTestGateFailureKind.MissingSource);
            }

            if (command.Ecosystem == VerifyEcosystem.Node) ranFrontend = true;
            else ranBackend = true;
            output.AppendLine($"# working directory: {workingDirectory}");

            var process = await RunShellAsync(
                workingDirectory, command.Command, Remaining(timeout, sw.Elapsed), output, ct)
                .ConfigureAwait(false);
            evidence.Add(process);
            if (process.ExitCode != 0 || process.TimedOut || process.Cancelled || process.LaunchError is not null)
            {
                var kind = ClassifyFailure(process);
                if (kind == BuildTestGateFailureKind.Code && !command.BlocksWorkPackage)
                {
                    findings.Add(new BuildTestGateFinding(
                        "out-of-work-package-test-failure",
                        command.TestScope,
                        command.Command,
                        $"{Describe(command)} failed outside the selected work package",
                        process.ExitCode,
                        LastEvidence(process)));
                    output.AppendLine("# non-blocking finding: continuous test failure recorded separately");
                    continue;
                }
                sw.Stop();
                var verdict = kind == BuildTestGateFailureKind.Code && mode != PostStepMode.Fail
                    ? BuildTestGateVerdict.Warn
                    : BuildTestGateVerdict.Fail;
                var reason = $"{Describe(command)} exit {process.ExitCode?.ToString() ?? "n/a"}";
                return WithFailure(new BuildTestGateResult(
                    verdict, process.ExitCode, sw.ElapsedMilliseconds, output.Text,
                    reason, ranBackend, ranFrontend)
                {
                    Processes = evidence,
                    Findings = findings,
                    TerminationSignal = process.TerminationSignal,
                }, kind);
            }
        }

        sw.Stop();
        return new BuildTestGateResult(
            findings.Count == 0 ? BuildTestGateVerdict.Ok : BuildTestGateVerdict.Warn,
            0, sw.ElapsedMilliseconds, output.Text,
            findings.Count == 0
                ? $"verify gate passed ({planSource})"
                : $"work-package gate passed with {findings.Count} separate non-blocking finding(s)",
            ranBackend, ranFrontend)
        {
            Processes = evidence,
            Findings = findings,
        };
    }

    private static string CoverageReason(string reason, TestSelectionAudit audit)
    {
        var omitted = audit.OmittedTestCommands.Count;
        return $"{reason}; test-level={audit.Level}; selected={audit.SelectedCommands.Count}; " +
               (audit.FullSuiteRan
                   ? audit.FullSuiteRequired ? "full-suite=required-and-run" : "full-suite=run-conservatively"
                   : $"full-suite=not-run; omitted={omitted}");
    }

    private static TestSelectionAudit CompleteAudit(
        TestSelectionAudit audit,
        IReadOnlyList<VerifyCommand> commands,
        IReadOnlyList<BuildTestGateProcessEvidence> processes)
    {
        if (audit.Level != TestExecutionLevels.Full) return audit;

        // Evidence is appended once per attempted command and commands execute
        // sequentially. A failure can stop the loop, so only the matching prefix
        // is known to have run. An empty declared test inventory is complete
        // after the remaining verify commands finish successfully; the verdict
        // still guards that case at the pre-main boundary.
        var attemptedCount = Math.Min(commands.Count, processes.Count);
        var allTestsAttempted = commands
            .Select((command, index) => (command, index))
            .Where(item => item.command.Kind == VerifyCommandKind.Test)
            .All(item => item.index < attemptedCount
                && processes[item.index].LaunchError is null);
        var notRun = commands
            .Select((command, index) => (command, index))
            .Where(item => item.command.Kind == VerifyCommandKind.Test
                && (item.index >= attemptedCount || processes[item.index].LaunchError is not null))
            .Select(item => TestSelectionPlanner.Describe(item.command));
        return audit with
        {
            FullSuiteRan = allTestsAttempted,
            OmittedTestCommands = audit.OmittedTestCommands
                .Concat(notRun)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static string LastEvidence(BuildTestGateProcessEvidence process)
    {
        var text = string.Join('\n', process.StandardOutput, process.StandardError);
        var lines = text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(40));
    }

    /// <summary>
    /// The budget for WAITING in the machine-gate queue for the one running gate
    /// ahead to finish. The machine lock is held for a gate's entire build+test run
    /// (15-25 min in production), so a queued card must be willing to wait roughly
    /// one full run - NOT the short infra-op SLA. Budgeting the queue wait against
    /// the infra SLA made every card queued behind a running gate escalate as
    /// "Timeout persisted" after 120 s (AGT-2182, 21.07.). An explicit
    /// <paramref name="configured"/> value wins; otherwise derive run-timeout plus
    /// infra-timeout so one full run ahead is tolerated.
    /// </summary>
    internal static TimeSpan ResolveQueueWaitTimeout(
        TimeSpan? configured,
        TimeSpan runTimeout,
        TimeSpan infrastructureTimeout)
    {
        if (configured is { } value && value > TimeSpan.Zero)
            return value;
        var run = runTimeout > TimeSpan.Zero ? runTimeout : TimeSpan.Zero;
        var infra = infrastructureTimeout > TimeSpan.Zero ? infrastructureTimeout : TimeSpan.Zero;
        var derived = run + infra;
        return derived > TimeSpan.Zero ? derived : TimeSpan.FromMinutes(2);
    }

    private static async Task<MachineGateAcquisition> AcquireMachineGateAsync(
        TimeSpan queueWaitTimeout,
        CancellationToken ct)
    {
        var wait = Stopwatch.StartNew();
        var collision = !await ProcessGate.WaitAsync(0, ct).ConfigureAwait(false);
        var ownsProcessGate = !collision;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(queueWaitTimeout);
        try
        {
            if (!ownsProcessGate)
            {
                await ProcessGate.WaitAsync(bounded.Token).ConfigureAwait(false);
                ownsProcessGate = true;
            }

            while (true)
            {
                bounded.Token.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        MachineGateLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        OperatingSystem.IsWindows() ? FileShare.None : FileShare.ReadWrite,
                        bufferSize: 1, FileOptions.None);
                    if (!OperatingSystem.IsWindows() && !NativeFileLock.TryAcquireExclusive(stream))
                    {
                        stream.Dispose();
                        collision = true;
                        await Task.Delay(TimeSpan.FromMilliseconds(100), bounded.Token).ConfigureAwait(false);
                        continue;
                    }

                    wait.Stop();
                    return MachineGateAcquisition.Acquired(
                        new MachineGateLease(stream, wait.ElapsedMilliseconds, collision));
                }
                catch (IOException)
                {
                    collision = true;
                    await Task.Delay(TimeSpan.FromMilliseconds(100), bounded.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (ownsProcessGate) ProcessGate.Release();
            wait.Stop();
            return MachineGateAcquisition.TimedOut(wait.ElapsedMilliseconds, collision,
                $"machine build/test gate queue wait exceeded SLA of {queueWaitTimeout.TotalSeconds:F0}s");
        }
        catch
        {
            if (ownsProcessGate) ProcessGate.Release();
            throw;
        }
    }

    private static class NativeFileLock
    {
        private const int LockExclusive = 2;
        private const int LockNonBlocking = 4;

        [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        private static extern int Flock(int fileDescriptor, int operation);

        public static bool TryAcquireExclusive(FileStream stream)
        {
            if (Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(),
                    LockExclusive | LockNonBlocking) == 0)
                return true;
            var error = Marshal.GetLastPInvokeError();
            if (error is 4 or 11 or 35) return false;
            throw new InvalidOperationException(
                $"Could not acquire the build/test machine lock (flock errno {error}).");
        }
    }

    private sealed record MachineGateAcquisition(
        MachineGateLease? Lease,
        long QueueWaitMs,
        bool CollisionDetected,
        string Reason)
    {
        public static MachineGateAcquisition Acquired(MachineGateLease lease)
            => new(lease, lease.QueueWaitMs, lease.CollisionDetected, string.Empty);

        public static MachineGateAcquisition TimedOut(long waitMs, bool collision, string reason)
            => new(null, waitMs, collision, reason);
    }

    private sealed class MachineGateLease : IDisposable
    {
        private readonly FileStream _stream;
        private bool _disposed;

        public MachineGateLease(FileStream stream, long queueWaitMs, bool collisionDetected)
        {
            _stream = stream;
            QueueWaitMs = queueWaitMs;
            CollisionDetected = collisionDetected;
        }

        public long QueueWaitMs { get; }
        public bool CollisionDetected { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _stream.Dispose(); }
            finally { ProcessGate.Release(); }
        }
    }

    private async Task<WorkspacePreparation> PrepareExactWorkspaceAsync(
        string repositoryPath,
        string? expectedSha,
        string? subjectRef,
        string gateRunId,
        TimeSpan infrastructureTimeout,
        CancellationToken ct)
    {
        if (!Directory.Exists(repositoryPath))
            return WorkspacePreparation.Failed(BuildTestGateFailureKind.MissingSource,
                $"repository not found: {repositoryPath}");
        if (string.IsNullOrWhiteSpace(expectedSha) || !SafeSha.IsMatch(expectedSha))
            return WorkspacePreparation.Failed(BuildTestGateFailureKind.MissingSource,
                "exact review subject SHA is missing or invalid");

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(infrastructureTimeout);
        try
        {
            Directory.CreateDirectory(ReviewWorkspaceRoot);
            var available = await RunGitAsync(
                repositoryPath, ["cat-file", "-e", expectedSha + "^{commit}"], bounded.Token)
                .ConfigureAwait(false);
            if (available.ExitCode != 0)
            {
                var fetchTarget = string.IsNullOrWhiteSpace(subjectRef) ? expectedSha : subjectRef;
                var fetch = await RunGitAsync(
                    repositoryPath, ["fetch", "--no-tags", "origin", fetchTarget!], bounded.Token)
                    .ConfigureAwait(false);
                if (fetch.ExitCode != 0)
                {
                    var fetchEvidence = fetch.StandardOutput + "\n" + fetch.StandardError;
                    return WorkspacePreparation.Failed(
                        ClassifyInfrastructureOrMissing(fetchEvidence),
                        "exact review subject could not be fetched", fetchEvidence);
                }
            }

            var workspace = Path.Combine(ReviewWorkspaceRoot, gateRunId);
            var add = await RunGitAsync(
                repositoryPath, ["worktree", "add", "--detach", workspace, expectedSha], bounded.Token)
                .ConfigureAwait(false);
            if (add.ExitCode != 0)
            {
                var addEvidence = add.StandardOutput + "\n" + add.StandardError;
                return WorkspacePreparation.Failed(
                    ClassifyInfrastructureOrMissing(addEvidence),
                    "exact review workspace could not be created", addEvidence);
            }

            var lease = new ExactWorkspaceLease(repositoryPath, workspace, "missing", _logger);
            string? testedSha;
            try
            {
                testedSha = await ReadHeadShaAsync(workspace, infrastructureTimeout, bounded.Token)
                    .ConfigureAwait(false);
                lease.SetTestedSha(testedSha ?? "missing");
            }
            catch
            {
                await lease.RemoveBestEffortAsync(infrastructureTimeout).ConfigureAwait(false);
                throw;
            }
            if (!string.Equals(expectedSha, testedSha, StringComparison.OrdinalIgnoreCase))
            {
                await lease.RemoveBestEffortAsync(infrastructureTimeout).ConfigureAwait(false);
                return WorkspacePreparation.Failed(BuildTestGateFailureKind.MissingSource,
                    $"exact review subject mismatch: expected {expectedSha}, tested {testedSha ?? "missing"}");
            }

            _logger.LogInformation(
                "build_test_gate_workspace_ready repository={Repository} expected_sha={ExpectedSha} tested_sha={TestedSha} workspace={Workspace}",
                repositoryPath, expectedSha, testedSha, workspace);
            return WorkspacePreparation.Ready(lease);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return WorkspacePreparation.Failed(BuildTestGateFailureKind.Timeout,
                $"exact review subject materialization exceeded infrastructure SLA of {infrastructureTimeout.TotalSeconds:F0}s");
        }
    }

    private static BuildTestGateFailureKind ClassifyInfrastructureOrMissing(string evidence)
    {
        var classified = ClassifyFailure(evidence);
        return classified == BuildTestGateFailureKind.None
            ? BuildTestGateFailureKind.MissingSource
            : classified;
    }

    private static async Task<string?> ReadHeadShaAsync(
        string path,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        var result = await RunGitAsync(path, ["rev-parse", "HEAD"], bounded.Token).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            process = Process.Start(psi);
            if (process is null)
                return new GitCommandResult(null, string.Empty, "Process.Start returned null");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new GitCommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            if (process is not null)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "BuildTestGateRunner: bounded Git process kill"); }
            }
            throw;
        }
        catch (Exception ex)
        {
            return new GitCommandResult(null, string.Empty, $"Process.Start failed: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private sealed record GitCommandResult(int? ExitCode, string StandardOutput, string StandardError);

    private sealed record WorkspacePreparation(
        ExactWorkspaceLease? Lease,
        BuildTestGateFailureKind FailureKind,
        string Reason,
        string Output)
    {
        public static WorkspacePreparation Ready(ExactWorkspaceLease lease)
            => new(lease, BuildTestGateFailureKind.None, string.Empty, string.Empty);

        public static WorkspacePreparation Failed(
            BuildTestGateFailureKind kind, string reason, string output = "")
            => new(null, kind, reason, output);
    }

    private sealed record WorkspaceCleanupError(
        BuildTestGateFailureKind FailureKind,
        string Evidence);

    private sealed class ExactWorkspaceLease
    {
        private readonly string _repositoryPath;
        private readonly ILogger _logger;
        private bool _removed;

        public ExactWorkspaceLease(string repositoryPath, string path, string testedSha, ILogger logger)
        {
            _repositoryPath = repositoryPath;
            Path = path;
            TestedSha = testedSha;
            _logger = logger;
        }

        public string Path { get; }
        public string TestedSha { get; private set; }

        public void SetTestedSha(string testedSha)
            => TestedSha = testedSha;

        public async Task<WorkspaceCleanupError?> RemoveAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (_removed) return null;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(timeout);
            var evidence = new StringBuilder();
            try
            {
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    var remove = await RunGitAsync(
                        _repositoryPath, ["worktree", "remove", "--force", Path], bounded.Token)
                        .ConfigureAwait(false);
                    if (remove.ExitCode == 0)
                    {
                        _removed = true;
                        return null;
                    }
                    evidence.AppendLine($"cleanup attempt {attempt}: {remove.StandardOutput} {remove.StandardError}".Trim());

                    if (attempt < 3)
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), bounded.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new WorkspaceCleanupError(BuildTestGateFailureKind.Timeout,
                    evidence.Append("cleanup infrastructure SLA expired").ToString());
            }
            return new WorkspaceCleanupError(
                ClassifyInfrastructureOrMissing(evidence.ToString()), evidence.ToString().Trim());
        }

        public async Task RemoveBestEffortAsync(TimeSpan timeout)
        {
            try
            {
                var error = await RemoveAsync(timeout, CancellationToken.None).ConfigureAwait(false);
                if (error is not null)
                {
                    _logger.LogWarning(
                        "BuildTestGateRunner: exact workspace cleanup failed for {Workspace}: {Error}",
                        Path, error.Evidence);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "BuildTestGateRunner: exact workspace cleanup threw for {Workspace}", Path);
            }
        }
    }

    private Task<BuildTestGateProcessEvidence> RunShellAsync(
        string workingDirectory,
        string command,
        TimeSpan timeout,
        RingOutput output,
        CancellationToken ct)
    {
        var (fileName, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>)["/c", command])
            : ("/bin/sh", (IReadOnlyList<string>)["-c", command]);
        return RunProcessAsync(workingDirectory, command, fileName, args, timeout, output, ct);
    }

    private async Task<BuildTestGateProcessEvidence> RunProcessAsync(
        string workingDirectory,
        string command,
        string fileName,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        RingOutput output,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        output.AppendLine($"> {fileName} {string.Join(' ', args)}");

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BuildTestGateRunner: Process.Start failed for {FileName}", fileName);
            output.AppendLine(ex.Message);
            return NewProcessEvidence(startedAt, command, fileName, args, workingDirectory) with
            {
                CompletedAtUtc = DateTimeOffset.UtcNow,
                LaunchError = ex.Message,
            };
        }
        if (process is null)
        {
            const string error = "Process.Start returned null";
            output.AppendLine(error);
            return NewProcessEvidence(startedAt, command, fileName, args, workingDirectory) with
            {
                CompletedAtUtc = DateTimeOffset.UtcNow,
                LaunchError = error,
            };
        }

        using (process)
        using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            bounded.CancelAfter(timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var timedOut = false;
            var cancelled = false;
            try
            {
                await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = ct.IsCancellationRequested;
                timedOut = !cancelled;
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "BuildTestGateRunner: process tree kill"); }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { SilentCatch.Note(ex, "BuildTestGateRunner: process exit after kill"); }
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            output.AppendBlock("stdout", stdout);
            output.AppendBlock("stderr", stderr);
            if (timedOut) output.AppendLine($"{fileName} timed out after {timeout.TotalSeconds:F0}s");
            if (cancelled) output.AppendLine($"{fileName} was cancelled");
            int? exitCode = process.HasExited ? process.ExitCode : null;
            var signal = ResolveTerminationSignal(exitCode, timedOut, cancelled);
            return NewProcessEvidence(startedAt, command, fileName, args, workingDirectory) with
            {
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ExitCode = exitCode,
                TerminationSignal = signal,
                TimedOut = timedOut,
                Cancelled = cancelled,
                StandardOutput = stdout,
                StandardError = stderr,
            };
        }
    }

    private static BuildTestGateProcessEvidence NewProcessEvidence(
        DateTimeOffset startedAt,
        string command,
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory)
        => new()
        {
            Command = command,
            FileName = fileName,
            Arguments = args.ToArray(),
            WorkingDirectory = workingDirectory,
            StartedAtUtc = startedAt,
            CompletedAtUtc = startedAt,
        };

    private static string? ResolveTerminationSignal(int? exitCode, bool timedOut, bool cancelled)
    {
        if (timedOut) return "timeout";
        if (cancelled) return "cancellation";
        if (OperatingSystem.IsWindows() || exitCode is null || exitCode < 128) return null;
        return exitCode switch
        {
            137 => "SIGKILL",
            143 => "SIGTERM",
            _ => "signal-" + (exitCode - 128),
        };
    }

    internal static BuildTestGateFailureKind ClassifyFailure(BuildTestGateProcessEvidence process)
    {
        if (process.LaunchError is not null) return BuildTestGateFailureKind.ProcessLaunch;
        if (process.Cancelled) return BuildTestGateFailureKind.Cancellation;
        if (process.TimedOut) return BuildTestGateFailureKind.Timeout;
        if (process.ExitCode == 137 || string.Equals(process.TerminationSignal, "SIGKILL", StringComparison.Ordinal))
            return BuildTestGateFailureKind.OutOfMemory;
        var evidence = process.StandardError + "\n" + process.StandardOutput;
        var classified = ClassifyFailure(evidence);
        if (classified == BuildTestGateFailureKind.None)
            return BuildTestGateFailureKind.Code;
        // A verify command that ran to completion and returned an exit code was NOT
        // prevented from running by the host: whatever lock / OOM / timeout string it
        // printed is its own reported result - e.g. a test that logs an
        // IOException "... because it is being used by another process" on its temp
        // DB files (AGT-2110, 21.07.). Treating such a DETERMINISTIC test failure as
        // review infrastructure poisoned the environmental-retry budget: the same
        // 15-25 min build+test was re-run twice more, each time holding the machine
        // gate and starving every queued card, before escalating "Lock persisted".
        // Only a genuine MSBuild build-output lock (MSB3026/MSB3027) is a real,
        // retryable host fault; every other string from a completed process is a
        // code/test defect that must flow through the normal reissue path instead.
        if (CompletedNormally(process) && !IsGenuineBuildOutputLock(evidence))
            return BuildTestGateFailureKind.Code;
        return classified;
    }

    private static bool CompletedNormally(BuildTestGateProcessEvidence process)
        => process.LaunchError is null
           && !process.TimedOut
           && !process.Cancelled
           && process.ExitCode is not null
           && process.ExitCode != 137;

    private static bool IsGenuineBuildOutputLock(string evidence)
        => evidence.Contains("MSB3026", StringComparison.OrdinalIgnoreCase)
           || evidence.Contains("MSB3027", StringComparison.OrdinalIgnoreCase);

    internal static BuildTestGateFailureKind ClassifyFailure(string? text)
    {
        var value = text ?? string.Empty;
        if (ContainsAny(value,
                "being used by another process", "file is locked", "cannot access the file",
                "resource temporarily unavailable", "sharing violation", "MSB3026", "MSB3027"))
            return BuildTestGateFailureKind.Lock;
        if (ContainsAny(value,
                "out of memory", "outofmemoryexception", "cannot allocate memory", "heap limit"))
            return BuildTestGateFailureKind.OutOfMemory;
        if (ContainsAny(value,
                "timed out after", "deadline exceeded", "operation exceeded its time limit"))
            return BuildTestGateFailureKind.Timeout;
        if (ContainsAny(value,
                "process.start failed", "process.start returned null", "failed to start process",
                "executable file not found"))
            return BuildTestGateFailureKind.ProcessLaunch;
        if (ContainsAny(value, "operation was cancelled", "operation was canceled", "operationcanceledexception"))
            return BuildTestGateFailureKind.Cancellation;
        if (ContainsAny(value,
                "repository not found", "missing source", "bad object", "not a git repository",
                "unknown revision", "not a valid object name", "couldn't find remote ref"))
            return BuildTestGateFailureKind.MissingSource;
        if (ContainsAny(value, "review model", "model not found", "invalid model", "no parseable verdict"))
            return BuildTestGateFailureKind.ReviewModel;
        return BuildTestGateFailureKind.None;
    }

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    internal static string Fingerprint(BuildTestGateFailureKind kind, string evidence)
    {
        var normalized = VolatileNumber.Replace(
            VolatileHex.Replace(evidence.ToLowerInvariant(), "<sha>"), "<n>");
        normalized = Whitespace.Replace(normalized, " ").Trim();
        if (normalized.Length > 8_192) normalized = normalized[^8_192..];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..16];
        return $"{kind.ToString().ToLowerInvariant()}:{hash}";
    }

    private static BuildTestGateResult Skipped(string reason)
        => new(BuildTestGateVerdict.Skipped, null, 0, string.Empty, reason, false, false);

    private static BuildTestGateResult InfrastructureFailure(
        BuildTestGateFailureKind kind,
        string reason,
        string output)
        => WithFailure(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, null, 0, output, reason, false, false), kind);

    private static BuildTestGateResult WithFailure(
        BuildTestGateResult result,
        BuildTestGateFailureKind kind)
    {
        var processEvidence = string.Join("\n", result.Processes.Select(p =>
            $"{p.Command}\nexit={p.ExitCode}\nsignal={p.TerminationSignal}\nlaunch={p.LaunchError}\n{p.StandardOutput}\n{p.StandardError}"));
        return result with
        {
            FailureKind = kind,
            FailureFingerprint = Fingerprint(kind, result.Reason + "\n" + result.Output + "\n" + processEvidence),
            TerminationSignal = result.TerminationSignal
                ?? (result.ExitCode is null ? kind.ToString().ToLowerInvariant() : null),
        };
    }

    internal static bool ShouldRunForChange(VerifyCommand command, IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null) return true;
        // Staged test selection has already applied diff, ownership, Test Hub,
        // and optional model evidence. Re-applying the legacy package-prefix
        // filter here would silently discard cross-package tests selected from
        // history or by the adviser. It would also make an explicit full run
        // smaller than the declared suite.
        if (command.Kind == VerifyCommandKind.Test) return true;
        if (command.Ecosystem != VerifyEcosystem.Node || string.IsNullOrEmpty(command.WorkingSubdir))
            return true;
        var prefix = command.WorkingSubdir.Replace('\\', '/').TrimEnd('/') + "/";
        return changedFiles.Any(file =>
            file.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(VerifyCommand command)
    {
        var location = string.IsNullOrEmpty(command.WorkingSubdir) ? string.Empty : $" ({command.WorkingSubdir})";
        return $"`{command.Command}`{location}";
    }

    internal static string ResolveWorkingDirectory(string repositoryPath, VerifyCommand command)
    {
        var repositoryRoot = Path.GetFullPath(repositoryPath);
        return string.IsNullOrEmpty(command.WorkingSubdir)
            ? repositoryRoot
            : Path.GetFullPath(Path.Combine(repositoryRoot, command.WorkingSubdir));
    }

    // Retained as a diagnostic compatibility helper. Admission itself is now
    // machine-wide, but callers can still compare linked checkout identities.
    internal static string ResolveAdmissionKey(string repositoryPath)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var commonGitDirectory = ReadOnlyGitRefFingerprint.ResolveCommonDirectory(canonical);
        return commonGitDirectory is null
            ? canonical
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(commonGitDirectory));
    }

    internal static bool HasCodeDiff(IReadOnlyList<string> changedFiles)
        => changedFiles.Any(IsCodePath);

    private static bool IsCodePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith(".orchestrator/", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return false;
        var extension = Path.GetExtension(normalized);
        return CodeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static TimeSpan Remaining(TimeSpan timeout, TimeSpan elapsed)
    {
        var remaining = timeout - elapsed;
        return remaining > TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10);
    }

    private sealed class RingOutput
    {
        private readonly int _capacity;
        private readonly Queue<string> _lines = new();
        private readonly object _lock = new();

        public RingOutput(int capacity) => _capacity = capacity;

        public string Text
        {
            get
            {
                lock (_lock) return string.Join(Environment.NewLine, _lines);
            }
        }

        public void AppendBlock(string stream, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line) AppendLine($"[{stream}] {line}");
        }

        public void AppendLine(string? line)
        {
            if (line is null) return;
            lock (_lock)
            {
                _lines.Enqueue(line);
                while (_lines.Count > _capacity) _lines.Dequeue();
            }
        }
    }
}
