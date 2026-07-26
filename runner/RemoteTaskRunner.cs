using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

/// <summary>
/// Runs exactly one task end-to-end on the remote host (RM-5 MVP). The lifecycle:
/// acquire the fenced lease, start heartbeating, prepare the git working tree,
/// spawn the agent CLI with the fetched prompt, ship its output to the server,
/// upload the results/ evidence, post a fenced runner completion so the result
/// enters the normal review pipeline, and always release the lease. Before removing
/// a worktree it salvages changes and pushes the runner branch to origin.
/// </summary>
public sealed class RemoteTaskRunner
{
    internal const int MaxEnvironmentPreparationAttempts = 3;

    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;
    private readonly RunnerStateStore _state;
    private readonly RunnerProcessInventoryTracker _inventory;

    public RemoteTaskRunner(
        RunnerOptions options,
        TaskServerClient client,
        Action<string> log,
        RunnerStateStore? state = null,
        RunnerProcessInventoryTracker? inventory = null)
    {
        _options = options;
        _client = client;
        _log = log;
        _state = state ?? new RunnerStateStore(options.StateDir);
        _inventory = inventory ?? new RunnerProcessInventoryTracker();
    }

    /// <returns>Process exit code: 0 on a clean handoff, non-zero when the run could not complete.</returns>
    public async Task<int> RunAsync(string taskKey, CancellationToken shutdown)
    {
        // Connectivity preflight: over a reverse tunnel the Task Server is only
        // reachable while the tunnel is up. Probe /healthz first so a dropped
        // connection is reported once, cleanly, as a connection-lost diagnostic
        // that names the tunnel - instead of surfacing as a raw transport error
        // buried in register/lease and reading like a task launch failure.
        var health = await _client.ProbeHealthAsync(shutdown);
        if (health is not null)
        {
            _log($"connection lost: cannot reach the task server at {_options.ServerUrl} ({health}). " +
                 "Verify the reverse tunnel / autossh service is up (agent-host --health-check) before assigning tasks.");
            return 4;
        }
        _log("preflight ok: task server reachable");

        // Register the runner identity first: the server's X-Client-Id boundary
        // rejects every write (lease, logs, artifacts, completion) from an
        // unregistered id with 401, so this must precede the lease acquire.
        var clientId = await _client.RegisterAsync(_options.RunnerName, "service", shutdown);
        _log($"registered runner identity '{_options.RunnerName}' as client '{clientId}'");
        await new DurableHandoffRecovery(_options, _client, _log).RecoverAllAsync(shutdown);

        _log($"acquiring lease for task '{taskKey}' as runner '{_options.RunnerId}' ({_options.RunnerName})");
        var acquire = await _client.AcquireLeaseAsync(new RunLeaseAcquireRequest(
            taskKey, _options.RunnerId, _options.RunnerName, _options.Hostname,
            Environment.ProcessId, _options.BackendName, _options.TtlSeconds), shutdown);

        if (!acquire.Granted || acquire.Lease is null)
        {
            _log($"lease not granted: {acquire.Outcome} - {acquire.Message}");
            return 2;
        }

        var lease = acquire.Lease;
        _log($"lease {lease.LeaseId} granted, fencing token {lease.FencingToken}, expires {lease.ExpiresAt:o}");

        return await RunClaimedAsync(taskKey, lease, shutdown);
    }

    /// <summary>Runs a daemon-claimed task using the lease minted by the atomic claim endpoint.</summary>
    public async Task<int> RunClaimedAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        CancellationToken shutdown,
        string? projectId = null,
        string? repositoryUrl = null,
        string? defaultBranch = null,
        string? taskKind = null,
        string? runId = null,
        string? leaseInstanceId = null)
    {
        var isProjectClone = !string.IsNullOrWhiteSpace(projectId);
        if (isProjectClone && string.IsNullOrWhiteSpace(repositoryUrl))
        {
            _log(
                $"remote-runner-project-not-remote-capable projectId={projectId ?? "unknown"} " +
                $"task={taskKey} reason=repository-url-not-configured");
            await ReleaseAsync(lease, CancellationToken.None);
            return 2;
        }

        _log($"running claimed task '{taskKey}' with lease {lease.LeaseId}, fencing token {lease.FencingToken}");

        var workspace = new GitWorkspace(
            _options, taskKey, _log, projectId, repositoryUrl, defaultBranch, isProjectClone);
        var slot = _state.Create(
            taskKey, lease, workspace.RepoPath, runId, leaseInstanceId,
            projectId, repositoryUrl, defaultBranch, taskKind);
        return await RunPersistedAsync(slot, workspace, shutdown, reattach: false);
    }

    /// <summary>Continue a positively verified detached process from durable host state.</summary>
    public async Task<int> ReattachAsync(PersistedRunnerSlot slot, CancellationToken stopRun)
    {
        _log($"reattaching task '{slot.TaskKey}' attempt {slot.AttemptId} pid={slot.ProcessId} worktree={slot.WorktreePath}");
        var workspace = new GitWorkspace(
            _options, slot.TaskKey, _log, slot.ProjectId, slot.RepositoryUrl, slot.DefaultBranch);
        return await RunPersistedAsync(slot, workspace, stopRun, reattach: true);
    }

    public async Task<bool> ReleaseDeadAsync(PersistedRunnerSlot slot, string reason)
    {
        _log($"releasing dead persisted attempt task={slot.TaskKey} attempt={slot.AttemptId}: {reason}");
        if (await ReleaseAsync(slot.Lease, CancellationToken.None))
        {
            _state.Delete(slot);
            return true;
        }

        _log($"dead attempt state retained for release retry: {slot.TaskKey}");
        return false;
    }

    private async Task<int> RunPersistedAsync(
        PersistedRunnerSlot slot,
        GitWorkspace workspace,
        CancellationToken shutdown,
        bool reattach)
    {
        var taskKey = slot.TaskKey;
        var lease = slot.Lease;
        var inventoryRunId = slot.RunId ?? slot.AttemptId;
        using var inventoryRegistration = _inventory.Track(
            inventoryRunId,
            taskKey,
            workspace.RepoPath);
        if (slot.ProcessId is > 0)
            _inventory.AttachProcess(inventoryRunId, slot.ProcessId.Value);

        var outbox = _client.UsesDurableTaskServer
            ? DurableRunOutbox.Open(
                Path.Combine(_options.WorkDir, "outbox"),
                _client.OutboxAuthority(taskKey))
            : null;
        using var activeOutbox = outbox?.MarkActive();
        using var stopRun = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var heartbeat = new LeaseHeartbeat(
            _client,
            _options,
            lease,
            _log,
            inventory: _inventory);
        var heartbeatTask = heartbeat.RunAsync(stopRun, shutdown);

        outbox?.Enqueue("status", JsonSerializer.Serialize(
            new { phase = "claimed", taskKey },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var shipper = new LogShipper(_client, taskKey, lease, _log, outbox);
        var shipperTask = shipper.RunAsync(TimeSpan.FromSeconds(5), stopRun.Token);

        var outcome = new RunOutcome(RunOutcomeKind.Unknown, "Runner ended before a terminal outcome was recorded.");
        var outcomeDecision = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            lease.AttemptId ?? lease.LeaseId,
            ExecutionAttemptKind.Coding,
            DurableOutputState: DurableOutputState.Missing));
        var epicPlanning = string.Equals(slot.TaskKind, "epic", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string> outputLines = [];
        var sourceMutated = false;
        var handedBack = false;
        var teardownAttempted = false;
        var releaseOnly = false;
        try
        {
            var execution = reattach
                ? await AwaitDetachedAsync(slot, workspace, shipper, outbox, stopRun.Token)
                : await ExecuteAsync(slot, workspace, shipper, outbox, stopRun, shutdown, epicPlanning);
            outcome = execution.Outcome;
            outcomeDecision = execution.Decision;
            outputLines = execution.OutputLines;
            await shipper.FlushAsync(shutdown);
            var artifactManifest = await UploadResultsAsync(taskKey, lease, outbox, shutdown);

            if (heartbeat.LeaseLost)
            {
                _log("lease was lost mid-run; skipping completion so the takeover holder owns the outcome");
                return 3;
            }

            teardownAttempted = true;
            WorktreeTeardownResult teardown;
            ResultHandoffAck? handoffAcknowledgement = null;
            string? envelopeDigest = null;
            if (epicPlanning)
            {
                // Epic planning is source-read-only: verify no mutation and
                // discard the detached checkout without salvage or a coding
                // branch. The mutation verdict rides the additive completion
                // fields; develop's salvage protocol stays untouched.
                sourceMutated = await workspace.TeardownReadOnlyAsync(CancellationToken.None);
                teardown = WorktreeTeardownResult.NoWork;
            }
            else if (outbox is not null)
            {
                teardown = await SecureForHandoffWithRetryAsync(
                    taskKey,
                    workspace,
                    outcome,
                    outbox,
                    shutdown);
                var dependencyIdentities = await workspace.ReadDependencyIdentitiesAsync(shutdown);
                var repositoryId = !string.IsNullOrWhiteSpace(slot.ProjectId)
                    ? slot.ProjectId
                    : throw new InvalidOperationException(
                        "Durable result handoff requires the Task Server repository identity.");
                var envelope = new ImmutableResultEnvelope(
                    repositoryId,
                    outbox.Authority.RunId,
                    workspace.BaseSha
                    ?? throw new InvalidOperationException("Durable result handoff has no recorded base SHA."),
                    teardown.ResultSha
                    ?? throw new InvalidOperationException("Durable result handoff has no result SHA."),
                    teardown.ImmutableResultRef,
                    null,
                    artifactManifest.Digest,
                    dependencyIdentities.Submodules,
                    dependencyIdentities.LfsObjects,
                    workspace.RepositoryUrl);
                envelopeDigest = ResultEnvelopeDigest.Compute(envelope);
                outbox.Enqueue("git-facts", JsonSerializer.Serialize(
                    new DurableGitFactsPayload(
                        repositoryId,
                        envelope.BaseSha,
                        envelope.ResultSha,
                        teardown.ImmutableResultRef,
                        teardown.Reconciliation,
                        teardown.Reconciliation?.Kind == "divergent"
                            ? "inspect-preserved-divergent-tips"
                            : null),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                var finalItem = outbox.Enqueue(
                    "final-result",
                    JsonSerializer.Serialize(
                        envelope,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                await ReplayBeforeAsync(outbox, finalItem.Sequence, shutdown);
                handoffAcknowledgement = await _client.AcknowledgeResultHandoffAsync(
                    outbox.Authority,
                    finalItem,
                    envelope,
                    shutdown);
                outbox.RecordHandoffAcknowledgement(handoffAcknowledgement);
                await ReportOutboxSafeAsync(outbox, shutdown);
                await workspace.TeardownAfterHandoffAsync(
                    teardown,
                    outbox.HandoffAcknowledgement
                    ?? throw new InvalidDataException(
                        "Durable handoff acknowledgement was not persisted."),
                    outbox.Authority.RunId,
                    envelopeDigest,
                    CancellationToken.None);
            }
            else
            {
                teardown = await workspace.TeardownAsync(outcome.Kind.ToString(), CancellationToken.None);
            }
            outcomeDecision = WithDurableOutput(outcomeDecision, teardown);
            if (outbox is not null && !epicPlanning)
            {
                var completion = outbox.Enqueue(
                    "completion",
                    JsonSerializer.Serialize(
                        new DurableCompletionPayload(
                            outcomeDecision.Outcome.ToString(),
                            outcome.Reason,
                            envelopeDigest,
                            outcomeDecision),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                await _client.SendOutboxItemAsync(outbox.Authority, completion, shutdown);
                outbox.Acknowledge(completion.Sequence);
                outbox.RecordHandoffState("completed", envelopeDigest);
                await ReportOutboxSafeAsync(outbox, shutdown);
            }
            else
            {
                await CompleteAsync(
                    taskKey,
                    lease,
                    outcome,
                    outcomeDecision,
                    teardown,
                    workspace.RepositoryUrl,
                    outputLines,
                    sourceMutated,
                    shutdown);
            }
            handedBack = true;
            _log($"task '{taskKey}' handed back to the local board: {outcome.Kind}");
            return outcome.Kind is RunOutcomeKind.Done or RunOutcomeKind.NoOp ? 0 : 1;
        }
        catch (DetachedWorkerLostException ex)
        {
            releaseOnly = true;
            _log($"detached worker lost; attempt will be released to Ready: {ex.Message}");
            return 3;
        }
        catch (RemoteClaimPreparationException ex)
        {
            outcome = new RunOutcome(RunOutcomeKind.EnvironmentFailure, ex.Message);
            shipper.Add("system", $"[runner] remote-claim-environment-failed: {ex.Message}");
            await shipper.FlushAsync(CancellationToken.None);
            if (!heartbeat.LeaseLost)
            {
                await CompleteAsync(
                    taskKey,
                    lease,
                    outcome,
                    outcomeDecision,
                    WorktreeTeardownResult.NoWork,
                    workspace.RepositoryUrl,
                    outputLines,
                    sourceMutated: false,
                    CancellationToken.None);
                handedBack = true;
            }
            return 1;
        }
        catch (WorktreeSalvageException ex)
        {
            if (outbox is not null)
            {
                outbox.RecordHandoffState("transfer-recovery");
                await ReportOutboxSafeAsync(outbox, CancellationToken.None);
                _log($"result transfer remains recoverable without a new coding attempt: {ex.Message}");
                return 4;
            }
            await ReportUnsecuredWorktreeAsync(taskKey, lease, ex);
            handedBack = true;
            return 1;
        }
        finally
        {
            stopRun.Cancel();
            await SafeAwait(heartbeatTask);
            // This path covers shutdown, cancellation, quota death, and any
            // exception before the normal completion handoff. Salvage uses an
            // independent token because SIGINT has already cancelled the run.
            if (outbox is null && !teardownAttempted && Directory.Exists(workspace.RepoPath))
            {
                try
                {
                    teardownAttempted = true;
                    var teardown = epicPlanning
                        ? WorktreeTeardownResult.NoWork
                        : await workspace.TeardownAsync(outcome.Kind.ToString(), CancellationToken.None);
                    if (epicPlanning)
                        sourceMutated = await workspace.TeardownReadOnlyAsync(CancellationToken.None);
                    if (!handedBack && !heartbeat.LeaseLost && !releaseOnly)
                    {
                        outcomeDecision = WithDurableOutput(outcomeDecision, teardown);
                        await CompleteAsync(
                            taskKey,
                            lease,
                            outcome,
                            outcomeDecision,
                            teardown,
                            workspace.RepositoryUrl,
                            outputLines,
                            sourceMutated,
                            CancellationToken.None);
                        handedBack = true;
                    }
                }
                catch (WorktreeSalvageException ex)
                {
                    // Even a lost lease cannot hide an unsecured host-local
                    // checkout. The gate is safety evidence, not an ownership
                    // claim over the run's successful outcome.
                    if (!handedBack)
                    {
                        await ReportUnsecuredWorktreeAsync(taskKey, lease, ex);
                        handedBack = true;
                    }
                }
                catch (Exception ex)
                {
                    _log($"task worktree teardown failed; worktree retained at {workspace.RepoPath}: {ex.Message}");
                }
            }

            // Completion is fenced by the live lease, so release only after the
            // normal or fail-closed handoff has finished.
            if ((outbox is null || handedBack)
                && await ReleaseAsync(lease, CancellationToken.None))
                _state.Delete(slot);
        }
    }

    private async Task<RemoteExecutionResult> ExecuteAsync(
        PersistedRunnerSlot slot, GitWorkspace workspace, LogShipper shipper,
        DurableRunOutbox? outbox, CancellationTokenSource stopRun,
        CancellationToken shutdown, bool epicPlanning)
    {
        var taskKey = slot.TaskKey;
        var lease = slot.Lease;
        Func<CancellationToken, Task<string>> prepare = epicPlanning
            ? workspace.PrepareReadOnlyAsync
            : workspace.PrepareAsync;
        string branch;
        try
        {
            branch = await RetryEnvironmentPreparationAsync(
                prepare,
                _log,
                shutdown);
        }
        catch (WorktreeSalvageException)
        {
            throw;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (RemoteEnvironmentPreparationException ex)
        {
            throw new RemoteClaimPreparationException(
                DescribePreparationFailure(ex.InnerException ?? ex),
                ex);
        }
        catch (Exception ex)
        {
            throw new RemoteClaimPreparationException(DescribePreparationFailure(ex), ex);
        }
        shipper.Add("system", $"[runner] working tree ready on branch '{branch}'");
        if (outbox is not null && !epicPlanning)
        {
            outbox.Enqueue(
                "run-context",
                JsonSerializer.Serialize(
                    new DurableRunContextPayload(
                        slot.ProjectId
                        ?? throw new InvalidOperationException(
                            "Durable coding execution requires a repository identity."),
                        workspace.RepositoryUrl,
                        slot.DefaultBranch,
                        workspace.BaseSha
                        ?? throw new InvalidOperationException(
                            "Durable coding execution has no base SHA.")),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        if (workspace.PickupReconciliation is { } recovery)
        {
            shipper.Add("system",
                $"[runner] salvage-reconciliation kind={recovery.Kind} " +
                $"canonicalRef=refs/heads/{recovery.CanonicalBranch} canonicalSha={recovery.CanonicalCommitSha} " +
                $"localSha={recovery.LocalCommitSha} " +
                $"recoveryRef={(recovery.RecoveryBranch is null ? "none" : $"refs/heads/{recovery.RecoveryBranch}")} " +
                $"recoverySha={recovery.RecoveryCommitSha ?? "none"} " +
                $"authoritativeBaseRef=refs/heads/{recovery.AuthoritativeBaseBranch} " +
                $"authoritativeBaseSha={recovery.AuthoritativeBaseSha}");
        }

        string prompt;
        if (epicPlanning)
        {
            var planning = await _client.GetEpicPlanningPromptAsync(new RemoteEpicPlanningPromptRequest(
                taskKey, lease.LeaseId, lease.FencingToken, _options.RunnerId, workspace.RepoPath), shutdown)
                ?? throw new InvalidOperationException("Server returned no Epic planning prompt.");
            prompt = planning.Prompt;
            shipper.Add("system", $"[runner] server-rendered Epic decomposition prompt; cli={planning.CliType ?? "default"} model={planning.Model ?? "default"} thinking={planning.ThinkingLevel ?? "default"}");
        }
        else
        {
        var taskPrompt = await _client.ReadTaskFileAsync(taskKey, "prompt.md", shutdown)
                         ?? throw new InvalidOperationException($"Task '{taskKey}' has no prompt.md to run.");
            prompt = RemoteRunPrompt.Build(taskPrompt);
        shipper.Add("system", "[runner] remote-completion-protocol appended to task prompt");
        }

        var resultsDir = ResultsDir(taskKey);
        if (Directory.Exists(resultsDir)) Directory.Delete(resultsDir, recursive: true);
        Directory.CreateDirectory(resultsDir);

        shipper.Add("system", $"[runner] spawning {_options.CliBin} {_options.CliArgs}");
        slot = _state.Save(slot with
        {
            WorktreePath = workspace.RepoPath,
            Phase = "launching",
        });
        DurableAgentProcess process;
        try
        {
            process = DurableAgentProcess.Start(
                _options, slot.WorkerDirectory, workspace.RepoPath, prompt, resultsDir);
        }
        catch (Exception ex)
        {
            throw new RemoteClaimPreparationException(DescribePreparationFailure(ex), ex);
        }
        slot = _state.Save(slot with
        {
            ProcessId = process.ProcessId,
            ProcessStartedAtUtc = process.ProcessStartedAtUtc,
            WorktreePath = workspace.RepoPath,
            Phase = "running",
        });
        _inventory.AttachProcess(slot.RunId ?? slot.AttemptId, process.ProcessId);
        _log($"detached worker started task={taskKey} pid={process.ProcessId} attempt={slot.AttemptId}");
        return await AwaitDetachedAsync(slot, workspace, shipper, outbox, stopRun.Token);
    }

    private async Task<RemoteExecutionResult> AwaitDetachedAsync(
        PersistedRunnerSlot slot,
        GitWorkspace workspace,
        LogShipper shipper,
        DurableRunOutbox? outbox,
        CancellationToken stopRun,
        int sameSessionResumeAttempts = 0)
    {
        var process = DurableAgentProcess.Attach(slot);
        var sequence = slot.LastOutputSequence;
        try
        {
            while (true)
            {
                var lines = process.ReadAfter(sequence);
                foreach (var line in lines)
                {
                    shipper.Add(line.Stream, line.Text);
                    sequence = Math.Max(sequence, line.Sequence);
                }
                if (lines.Count > 0 || shipper.PendingCount > 0)
                {
                    if (await shipper.FlushAsync(stopRun))
                        slot = _state.Save(slot with { LastOutputSequence = sequence });
                }

                var result = process.ReadResult();
                if (result is not null)
                {
                    _state.Save(slot with { Phase = "finalizing", LastOutputSequence = sequence });
                    var processResult = new ProcessResult(result.ExitCode, result.StdOut, result.StdErr);
                    if (RunnerCapabilityProbe.IsProviderAuthenticationFailure(processResult))
                    {
                        var provider = RunnerCapabilityProbe.Provider(_options.CliBin);
                        var claimId = outbox?.Authority.RunId;
                        var diagnostic = result.StdErr
                            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                            .LastOrDefault()
                            ?? $"{provider} exited {result.ExitCode} with an authentication failure";
                        await _client.ReportCapabilityFailureAsync(
                            CapabilityProtocol.ProviderAuthentication(provider),
                            "ProviderUnauthorized",
                            diagnostic.Length <= 500 ? diagnostic : diagnostic[..500],
                            $"provider-auth:{claimId ?? slot.Lease.LeaseId}:{slot.Lease.FencingToken}",
                            "run",
                            claimId,
                            slot.Lease.FencingToken,
                            stopRun);
                        shipper.Add(
                            "system",
                            $"[runner] capability-failure capability={CapabilityProtocol.ProviderAuthentication(provider)} classification=ProviderUnauthorized");
                    }
                    var classified = result.TimedOut
                        ? ClassifyTimedOutResult(slot.Lease, workspace, result, sameSessionResumeAttempts)
                        : ClassifyProcessResult(
                            slot.Lease,
                            workspace,
                            processResult,
                            sameSessionResumeAttempts);
                    if (classified.Decision.RecoveryAction == ExecutionRecoveryAction.ResumeSameSession
                        && sameSessionResumeAttempts < ExecutionOutcomeAdapter.MaxSameSessionResumeAttempts)
                    {
                        var sessionId = classified.Decision.RawFacts.SessionId!;
                        var resumeArgs = _options.CliResumeArgs!
                            .Replace("{sessionId}", sessionId, StringComparison.Ordinal);
                        shipper.Add(
                            "system",
                            $"[runner] bounded same-session resume 1/{ExecutionOutcomeAdapter.MaxSameSessionResumeAttempts}; session={sessionId}");
                        var resumeSlot = _state.Save(slot with
                        {
                            WorkerDirectory = Path.Combine(slot.WorkerDirectory, "resume-1"),
                            ProcessId = null,
                            ProcessStartedAtUtc = null,
                            LastOutputSequence = 0,
                            Phase = "launching",
                        });
                        var resumed = DurableAgentProcess.Start(
                            _options,
                            resumeSlot.WorkerDirectory,
                            workspace.RepoPath,
                            "Continue the interrupted attempt from the durable workspace state. Complete the requested work, verify it, and end with exactly one required [[TASK_*]] terminal sentinel.",
                            ResultsDir(slot.TaskKey),
                            AgentCliProcess.SplitArgs(resumeArgs));
                        resumeSlot = _state.Save(resumeSlot with
                        {
                            ProcessId = resumed.ProcessId,
                            ProcessStartedAtUtc = resumed.ProcessStartedAtUtc,
                            Phase = "running",
                        });
                        _inventory.AttachProcess(
                            resumeSlot.RunId ?? resumeSlot.AttemptId,
                            resumed.ProcessId);
                        return await AwaitDetachedAsync(
                            resumeSlot,
                            workspace,
                            shipper,
                            outbox,
                            stopRun,
                            sameSessionResumeAttempts + 1);
                    }

                    shipper.Add(
                        "system",
                        $"[runner] CLI exited {classified.Decision.RawFacts.ExitCode?.ToString() ?? "without an exit code"}; typedOutcome={classified.Decision.Outcome} recovery={classified.Decision.RecoveryAction} classifier={classified.Decision.ClassifierVersion} legacyOutcome={classified.Outcome.Kind}");
                    outbox?.Enqueue(
                        "terminal",
                        JsonSerializer.Serialize(
                            new DurableTerminalPayload(
                                classified.Decision.Outcome.ToString(),
                                classified.Outcome.Reason),
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    return classified;
                }

                if (!DurableAgentProcess.VerifyLive(slot, out var reason))
                    throw new DetachedWorkerLostException($"Detached worker disappeared before recording a result: {reason}");
                await Task.Delay(TimeSpan.FromMilliseconds(250), stopRun);
            }
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }
    }

    private RemoteExecutionResult ClassifyTimedOutResult(
        RunLeaseInfoDto lease,
        GitWorkspace workspace,
        DetachedJobResult result,
        int sameSessionResumeAttempts)
    {
        var decision = ExecutionOutcomeAdapter.Classify(Facts(
            lease,
            workspace,
            StdOut: result.StdOut,
            StdErr: result.StdErr,
            ExitCode: result.ExitCode,
            TimedOut: true,
            SameSessionResumeAttempts: sameSessionResumeAttempts));
        return new RemoteExecutionResult(
            new RunOutcome(RunOutcomeKind.Unknown, decision.Outcome.ToString()),
            result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            decision);
    }

    private RemoteExecutionResult ClassifyProcessResult(
        RunLeaseInfoDto lease,
        GitWorkspace workspace,
        ProcessResult result,
        int sameSessionResumeAttempts)
    {
        var provider = ProviderOutputEvidenceExtractor.Extract(result.StdOut);
        var sessionState = !string.IsNullOrWhiteSpace(provider.SessionId)
                           && !string.IsNullOrWhiteSpace(_options.CliResumeArgs)
            ? ExecutionSessionState.Resumable
            : string.IsNullOrWhiteSpace(provider.SessionId)
                ? ExecutionSessionState.Unsupported
                : ExecutionSessionState.Active;
        var factsAfterExit = Facts(
            lease,
            workspace,
            ProviderTerminalEvent: provider.TerminalEvent,
            FinalAssistantOutput: provider.FinalAssistantOutput,
            StdOut: result.StdOut,
            StdErr: result.StdErr,
            ExitCode: result.ExitCode,
            Signal: SignalFromExitCode(result.ExitCode),
            SessionState: sessionState,
            SessionId: provider.SessionId,
            SameSessionResumeAttempts: sameSessionResumeAttempts);
        var typed = ExecutionOutcomeAdapter.Classify(factsAfterExit);
        var sentinelOutcome = SentinelScanner.Scan(result.StdOut);
        var outcome = typed.Outcome switch
        {
            ExecutionOutcomeKind.SuccessfulCompletion when sentinelOutcome.Kind == RunOutcomeKind.NoOp
                => sentinelOutcome,
            ExecutionOutcomeKind.SuccessfulCompletion
                => new RunOutcome(RunOutcomeKind.Done, sentinelOutcome.Reason),
            ExecutionOutcomeKind.ExplicitAgentBlocker when sentinelOutcome.Kind is RunOutcomeKind.Blocked or RunOutcomeKind.NeedsInput
                => sentinelOutcome,
            _ => new RunOutcome(RunOutcomeKind.Unknown, typed.Outcome.ToString()),
        };
        return new RemoteExecutionResult(
            outcome,
            result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            typed);
    }

    private async Task<DurableArtifactManifest> UploadResultsAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        DurableRunOutbox? outbox,
        CancellationToken ct)
    {
        var resultsDir = ResultsDir(taskKey);
        var manifest = new List<ArtifactManifestEntry>();
        var files = Directory.Exists(resultsDir)
            ? Directory.EnumerateFiles(
                resultsDir,
                "*",
                SearchOption.AllDirectories).ToList()
            : [];

        var uploads = new List<RunnerArtifactUpload>();
        foreach (var file in files)
        {
            var rel = "results/" + Path.GetRelativePath(resultsDir, file).Replace('\\', '/');
            var bytes = await File.ReadAllBytesAsync(file, ct);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            manifest.Add(new ArtifactManifestEntry(rel, sha, bytes.LongLength));
            var content = Convert.ToBase64String(bytes);
            if (outbox is not null)
            {
                outbox.Enqueue(
                    "artifact",
                    JsonSerializer.Serialize(
                        new DurableArtifactPayload(
                            rel,
                            TaskServerClient.MediaTypeForPath(rel),
                            content,
                            sha),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            else
            {
                uploads.Add(new RunnerArtifactUpload(rel, content));
            }
        }

        var artifactManifest = BuildArtifactManifest(manifest);
        if (outbox is not null)
        {
            outbox.Enqueue("artifact-manifest", artifactManifest.Json);
            await outbox.ReplayAsync(
                (item, token) => _client.SendOutboxItemAsync(outbox.Authority, item, token),
                ct);
            _log($"durably uploaded {manifest.Count} artifact(s) from outbox");
        }
        else
        {
            var digestInput = string.Join("\n", uploads.OrderBy(x => x.Path, StringComparer.Ordinal)
                .Select(x => $"{x.Path}:{WireDigest.Hash(x.ContentBase64)}"));
            var resp = await _client.UploadArtifactsAsync(new ArtifactIngestRequest(
                taskKey,
                uploads,
                RunnerId: lease.RunnerId,
                LeaseId: lease.LeaseId,
                FencingToken: lease.FencingToken,
                AttemptId: lease.AttemptId,
                Fence: lease.FencingToken,
                AuthorityEpoch: lease.AuthorityEpoch,
                IdempotencyKey: $"artifacts:{lease.AttemptId}:{WireDigest.Hash(digestInput)}"), ct);
            _log($"uploaded {resp?.Uploaded ?? 0} artifact(s); commit {resp?.CommitStatus ?? "n/a"}");
        }
        return artifactManifest;
    }

    private async Task<WorktreeTeardownResult> SecureForHandoffWithRetryAsync(
        string taskKey,
        GitWorkspace workspace,
        RunOutcome outcome,
        DurableRunOutbox outbox,
        CancellationToken shutdown)
    {
        for (var attempt = 1; ; attempt++)
        {
            shutdown.ThrowIfCancellationRequested();
            try
            {
                outbox.RecordHandoffState("transferring");
                await ReportOutboxSafeAsync(outbox, shutdown);
                return await workspace.SecureForHandoffAsync(
                    outcome.Kind.ToString(),
                    outbox.Authority.RunId,
                    shutdown);
            }
            catch (WorktreeSalvageException ex) when (!shutdown.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Max(2, attempt * 5)));
                outbox.Enqueue(
                    "transfer-recovery",
                    JsonSerializer.Serialize(
                        new
                        {
                            taskKey,
                            attempt,
                            delaySeconds = delay.TotalSeconds,
                            worktree = ex.WorktreePath,
                            ex.Branch,
                            ex.LocalCommitSha,
                            ex.RemoteCommitSha,
                            recoveryAction = "retry-transfer-without-coding",
                            error = ex.InnerException?.Message ?? ex.Message,
                        },
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                outbox.RecordHandoffState("transfer-recovery");
                await ReportOutboxSafeAsync(outbox, CancellationToken.None);
                _log($"result transfer failed; coding result retained, transfer-only retry {attempt} in {delay.TotalSeconds:0}s: {ex.InnerException?.Message ?? ex.Message}");
                await Task.Delay(delay, shutdown);
            }
        }
    }

    private async Task ReplayBeforeAsync(
        DurableRunOutbox outbox,
        long exclusiveSequence,
        CancellationToken ct)
    {
        foreach (var item in outbox.Pending.Where(item => item.Sequence < exclusiveSequence))
        {
            await _client.SendOutboxItemAsync(outbox.Authority, item, ct);
            outbox.Acknowledge(item.Sequence);
        }
    }

    private async Task ReportOutboxSafeAsync(
        DurableRunOutbox outbox,
        CancellationToken ct)
    {
        try
        {
            await _client.ReportOutboxAsync(
                _options.RunnerId,
                _client.RunnerInstanceId,
                outbox,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"outbox observability report deferred: {ex.Message}");
        }
    }

    internal static DurableArtifactManifest BuildArtifactManifest(
        IReadOnlyList<ArtifactManifestEntry> entries)
    {
        var ordered = entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        var json = JsonSerializer.Serialize(
            ordered,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
        return new DurableArtifactManifest(digest, json);
    }

    private async Task CompleteAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        RunOutcome outcome,
        ExecutionOutcomeDecision outcomeDecision,
        WorktreeTeardownResult teardown,
        string? repository,
        IReadOnlyList<string> outputLines,
        bool sourceMutated,
        CancellationToken ct)
    {
        var resp = await _client.CompleteRunAsync(new RemoteRunCompletionRequest(
            taskKey, lease.LeaseId, lease.FencingToken, _options.RunnerId,
            outcome.Kind.ToString(), outcome.Reason, _options.RunnerName,
            SalvageBranch: teardown.Branch,
            SalvageCommitSha: teardown.CommitSha,
            SalvageBranchUrl: teardown.BranchUrl,
            ResultSha: teardown.ResultSha,
            AttemptChainId: lease.LeaseId,
            Repository: repository,
            SalvageResolution: teardown.Reconciliation?.Kind,
            SalvageLocalCommitSha: teardown.Reconciliation?.LocalCommitSha,
            SalvageRecoveryBranch: teardown.Reconciliation?.RecoveryBranch,
            SalvageRecoveryCommitSha: teardown.Reconciliation?.RecoveryCommitSha,
            SalvageRecoveryBranchUrl: null,
            SalvageAuthoritativeBaseBranch: teardown.Reconciliation?.AuthoritativeBaseBranch,
            SalvageAuthoritativeBaseSha: teardown.Reconciliation?.AuthoritativeBaseSha,
            OutputLines: outputLines,
            SourceMutated: sourceMutated,
            AttemptId: lease.AttemptId,
            AuthorityEpoch: lease.AuthorityEpoch,
            IdempotencyKey: $"completion:{lease.AttemptId}:{outcome.Kind}:{teardown.ResultSha ?? "none"}",
            OutcomeDecision: outcomeDecision), ct);
        _log($"remote-runner-completion recorded: outcome {resp?.Outcome}, state {resp?.TargetState}");
    }

    private async Task ReportUnsecuredWorktreeAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        WorktreeSalvageException ex)
    {
        var failure = ex.InnerException?.Message.Replace('\r', ' ').Replace('\n', ' ').Trim()
                      ?? ex.Message;
        var workflowScopeMissing = GitPushProbe.IsWorkflowScopeFailure(failure);
        var gate = BuildUnsecuredWorktreeGate(_options.Hostname, ex);
        _log($"worktree-salvage-escalated task={taskKey} host={_options.Hostname} path={ex.WorktreePath} branch={ex.Branch} localSha={ex.LocalCommitSha ?? "unknown"} remoteSha={ex.RemoteCommitSha ?? "unknown"}");
        if (workflowScopeMissing)
        {
            try
            {
                await _client.ReportGitCapabilityAsync(
                    _client.ClientId,
                    new RunnerGitCapabilityRequest(
                        GitPushProbe.ReadyNoWorkflowScope,
                        GitPushProbe.WorkflowScopeFix(failure),
                        DateTime.UtcNow),
                    CancellationToken.None);
            }
            catch (Exception capabilityEx)
            {
                _log(
                    $"runner-git-workflow-capability-report-failed task={taskKey} " +
                    $"error={capabilityEx.Message}");
            }
        }
        try
        {
            await _client.CompleteRunAsync(new RemoteRunCompletionRequest(
                taskKey,
                lease.LeaseId,
                lease.FencingToken,
                _options.RunnerId,
                RunOutcomeKind.Blocked.ToString(),
                $"Remote runner retained unsecured worktree at {ex.WorktreePath}; intended branch {ex.Branch}.",
                _options.RunnerName,
                AttemptId: lease.AttemptId,
                AuthorityEpoch: lease.AuthorityEpoch,
                IdempotencyKey: $"completion:{lease.AttemptId}:worktree-blocked",
                GateItems: [gate]), CancellationToken.None);
        }
        catch (Exception reportEx)
        {
            _log($"worktree-salvage-escalation-failed task={taskKey} path={ex.WorktreePath} error={reportEx.Message}");
        }
    }

    internal static string BuildUnsecuredWorktreeGate(
        string hostname,
        WorktreeSalvageException ex)
    {
        var refs = $"canonical refs/heads/{ex.Branch} at {ex.RemoteCommitSha ?? "unknown"}; " +
                   $"retained local HEAD {ex.LocalCommitSha ?? "unknown"}";
        var failure = ex.InnerException?.Message.Replace('\r', ' ').Replace('\n', ' ').Trim()
                      ?? ex.Message;
        var remediation = GitPushProbe.IsWorkflowScopeFailure(failure)
            ? GitPushProbe.WorkflowScopeFix()
            : "Restore origin push access, publish the retained HEAD to a new ref, then requeue.";
        return $"worktree-blocked: unsecured worktree on {hostname}: {ex.WorktreePath} " +
               $"({refs}; failure: {failure}). No ref was overwritten. {remediation}";
    }

    private async Task<bool> ReleaseAsync(RunLeaseInfoDto lease, CancellationToken ct)
    {
        try
        {
            var resp = await _client.ReleaseLeaseAsync(new RunLeaseReleaseRequest(
                lease.TaskKey, lease.LeaseId, lease.FencingToken, _options.RunnerId,
                lease.AttemptId, lease.AuthorityEpoch,
                $"release:{lease.AttemptId}:{lease.LeaseId}"), ct);
            _log($"lease released: {resp.Outcome}");
            return string.Equals(resp.Outcome, "Released", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(resp.Outcome, "NotHeld", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(resp.Outcome, "Expired", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(resp.Outcome, "NotFound", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(resp.Outcome, "AlreadyReleased", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log($"lease release failed (server TTL will reclaim it): {ex.Message}");
            return false;
        }
    }

    private string ResultsDir(string taskKey)
        => Path.Combine(_options.WorkDir, "tasks", GitWorkspace.SafeSegment(taskKey), "results");

    private static readonly Regex CredentialedHttpUrl = new(
        @"(?<scheme>https?://)[^/@\s]+(?::[^/@\s]+)?@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string DescribePreparationFailure(Exception exception)
    {
        var message = CredentialedHttpUrl
            .Replace(exception.Message, "${scheme}***@")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (message.StartsWith("git clone ", StringComparison.OrdinalIgnoreCase))
            return $"clone failed: {message}";
        if (message.StartsWith("git fetch ", StringComparison.OrdinalIgnoreCase))
            return $"fetch failed: {message}";
        return $"environment preparation failed: {message}";
    }

    private sealed class RemoteClaimPreparationException : Exception
    {
        public RemoteClaimPreparationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal static ExecutionOutcomeDecision WithDurableOutput(
        ExecutionOutcomeDecision decision,
        WorktreeTeardownResult teardown)
    {
        // ResultSha is the exact local result preserved by salvage. In a
        // divergence, CommitSha can name the canonical remote branch tip while
        // ResultSha names the separately published recovery result.
        var reference = teardown.ResultSha ?? teardown.CommitSha ?? teardown.Branch;
        var state = string.IsNullOrWhiteSpace(reference)
            ? decision.RawFacts.DurableOutputState
            : DurableOutputState.Acknowledged;
        return ExecutionOutcomeAdapter.Classify(decision.RawFacts with
        {
            DurableOutputState = state,
            DurableOutputReference = reference ?? decision.RawFacts.DurableOutputReference,
        });
    }

    private static int? SignalFromExitCode(int exitCode)
        => !OperatingSystem.IsWindows() && exitCode is >= 129 and <= 255
            ? exitCode - 128
            : null;

    private static ExecutionRawFacts Facts(
        RunLeaseInfoDto lease,
        GitWorkspace workspace,
        string? ProviderTerminalEvent = null,
        string? FinalAssistantOutput = null,
        string? StdOut = null,
        string? StdErr = null,
        int? ExitCode = null,
        int? Signal = null,
        bool LaunchFailed = false,
        bool TimedOut = false,
        bool OomKilled = false,
        bool OperatorCancelled = false,
        bool HostShutdown = false,
        bool LeaseLost = false,
        ExecutionTransportState TransportState = ExecutionTransportState.Connected,
        ExecutionSessionState SessionState = ExecutionSessionState.Unsupported,
        string? SessionId = null,
        int SameSessionResumeAttempts = 0,
        int FreshSalvageAttempts = 0)
        => new(
            lease.AttemptId ?? lease.LeaseId,
            ExecutionAttemptKind.Coding,
            ProviderTerminalEvent,
            FinalAssistantOutput,
            StdOut,
            StdErr,
            ExitCode,
            Signal,
            LaunchFailed,
            TimedOut,
            OomKilled,
            OperatorCancelled,
            HostShutdown,
            LeaseLost,
            TransportState,
            SessionState,
            SessionId,
            DurableOutputState.LocalOnly,
            workspace.RepoPath,
            SameSessionResumeAttempts,
            FreshSalvageAttempts);

    internal static async Task<T> RetryEnvironmentPreparationAsync<T>(
        Func<CancellationToken, Task<T>> prepare,
        Action<string> log,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(log);
        delay ??= Task.Delay;

        Exception? last = null;
        for (var attempt = 1; attempt <= MaxEnvironmentPreparationAttempts; attempt++)
        {
            try
            {
                return await prepare(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not WorktreeSalvageException)
            {
                last = ex;
                log(
                    $"remote-environment-preparation-failed attempt={attempt}/{MaxEnvironmentPreparationAttempts} " +
                    $"error={OneLine(ex.Message)}");
                if (attempt < MaxEnvironmentPreparationAttempts)
                    await delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }

        throw new RemoteEnvironmentPreparationException(
            MaxEnvironmentPreparationAttempts,
            last ?? new InvalidOperationException("Environment preparation failed without an exception."));
    }

    private static string OneLine(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static async Task SafeAwait(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { /* expected on teardown */ }
        catch { /* background loops already logged their own failures */ }
    }
}

internal sealed class RemoteEnvironmentPreparationException : Exception
{
    public RemoteEnvironmentPreparationException(int attempts, Exception innerException)
        : base($"Remote environment preparation failed after {attempts} attempts.", innerException)
    {
        Attempts = attempts;
    }

    public int Attempts { get; }
}

internal sealed record RemoteExecutionResult(
    RunOutcome Outcome,
    IReadOnlyList<string> OutputLines,
    ExecutionOutcomeDecision Decision);
