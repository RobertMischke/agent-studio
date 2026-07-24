namespace AgentRunner;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;

    public RemoteTaskRunner(RunnerOptions options, TaskServerClient client, Action<string> log)
    {
        _options = options;
        _client = client;
        _log = log;
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
                 "Verify the reverse tunnel / autossh service is up (agent-runner --health-check) before assigning tasks.");
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
        string? taskKind = null)
    {
        _log($"running claimed task '{taskKey}' with lease {lease.LeaseId}, fencing token {lease.FencingToken}");

        using var stopRun = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var heartbeat = new LeaseHeartbeat(_client, _options, lease, _log);
        var heartbeatTask = heartbeat.RunAsync(stopRun, shutdown);

        var outbox = _client.UsesDurableTaskServer
            ? DurableRunOutbox.Open(
                Path.Combine(_options.WorkDir, "outbox"),
                _client.OutboxAuthority(taskKey))
            : null;
        outbox?.Enqueue("status", JsonSerializer.Serialize(
            new { phase = "claimed", taskKey },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var shipper = new LogShipper(_client, taskKey, lease, _log, outbox);
        var shipperTask = shipper.RunAsync(TimeSpan.FromSeconds(5), stopRun.Token);

        var outcome = new RunOutcome(RunOutcomeKind.Unknown, "Runner ended before a terminal outcome was recorded.");
        var workspace = new GitWorkspace(
            _options, taskKey, _log, projectId, repositoryUrl, defaultBranch);
        var epicPlanning = string.Equals(taskKind, "epic", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string> outputLines = [];
        var sourceMutated = false;
        var handedBack = false;
        var teardownAttempted = false;
        try
        {
            var execution = await ExecuteAsync(
                taskKey,
                lease,
                workspace,
                shipper,
                outbox,
                stopRun,
                shutdown,
                epicPlanning,
                projectId,
                defaultBranch);
            outcome = execution.Outcome;
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
                var repositoryId = !string.IsNullOrWhiteSpace(projectId)
                    ? projectId
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
                    dependencyIdentities.LfsObjects);
                envelopeDigest = ResultEnvelopeDigest.Compute(envelope);
                outbox.Enqueue("git-facts", JsonSerializer.Serialize(
                    new
                    {
                        repositoryId,
                        baseSha = workspace.BaseSha,
                        resultSha = teardown.ResultSha,
                        immutableRef = teardown.ImmutableResultRef,
                        salvageResolution = teardown.Reconciliation?.Kind,
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                outbox.Enqueue("artifact-manifest", artifactManifest.Json);
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
                outbox.Acknowledge(finalItem.Sequence);
                outbox.RecordHandoffAcknowledgement(handoffAcknowledgement);
                await ReportOutboxSafeAsync(outbox, shutdown);
                await workspace.TeardownAfterHandoffAsync(
                    teardown,
                    handoffAcknowledgement,
                    envelopeDigest,
                    CancellationToken.None);
            }
            else
            {
                teardown = await workspace.TeardownAsync(outcome.Kind.ToString(), CancellationToken.None);
            }
            if (outbox is not null && !epicPlanning)
            {
                var completion = outbox.Enqueue(
                    "completion",
                    JsonSerializer.Serialize(
                        new DurableCompletionPayload(
                            outcome.Kind.ToString(),
                            outcome.Reason,
                            envelopeDigest),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                await _client.SendOutboxItemAsync(outbox.Authority, completion, shutdown);
                outbox.Acknowledge(completion.Sequence);
                outbox.RecordHandoffState("completed", envelopeDigest);
                await ReportOutboxSafeAsync(outbox, shutdown);
            }
            else
            {
                await CompleteAsync(taskKey, lease, outcome, teardown, workspace.RepositoryUrl, outputLines, sourceMutated, shutdown);
            }
            handedBack = true;
            _log($"task '{taskKey}' handed back to the local board: {outcome.Kind}");
            return outcome.Kind is RunOutcomeKind.Done or RunOutcomeKind.NoOp ? 0 : 1;
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
            await SafeAwait(shipperTask);
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
                    if (!handedBack && !heartbeat.LeaseLost)
                    {
                        await CompleteAsync(taskKey, lease, outcome, teardown, workspace.RepositoryUrl, outputLines, sourceMutated, CancellationToken.None);
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
            if (outbox is null || handedBack)
                await ReleaseAsync(lease, CancellationToken.None);
        }
    }

    private async Task<RemoteExecutionResult> ExecuteAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        GitWorkspace workspace,
        LogShipper shipper,
        DurableRunOutbox? outbox,
        CancellationTokenSource stopRun,
        CancellationToken shutdown,
        bool epicPlanning,
        string? projectId,
        string? defaultBranch)
    {
        var branch = epicPlanning
            ? await workspace.PrepareReadOnlyAsync(shutdown)
            : await workspace.PrepareAsync(shutdown);
        shipper.Add("system", $"[runner] working tree ready on branch '{branch}'");
        if (outbox is not null && !epicPlanning)
        {
            outbox.Enqueue(
                "run-context",
                JsonSerializer.Serialize(
                    new DurableRunContextPayload(
                        projectId
                        ?? throw new InvalidOperationException(
                            "Durable coding execution requires a repository identity."),
                        workspace.RepositoryUrl,
                        defaultBranch,
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

        var cli = new AgentCliProcess(_options, resultsDir, _log);
        shipper.Add("system", $"[runner] spawning {_options.CliBin} {_options.CliArgs}");

        using var runTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.RunTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopRun.Token, runTimeout.Token);

        ProcessResult result;
        try
        {
            result = await cli.RunAsync(
                workspace.RepoPath, prompt,
                onStdOut: line => shipper.Add("stdout", line),
                onStdErr: line => shipper.Add("stderr", line),
                ct: linked.Token);
        }
        catch (OperationCanceledException) when (runTimeout.IsCancellationRequested)
        {
            shipper.Add("system", $"[runner] run exceeded {_options.RunTimeoutSeconds}s timeout");
            outbox?.Enqueue(
                "terminal",
                JsonSerializer.Serialize(
                    new DurableTerminalPayload(
                        RunOutcomeKind.Blocked.ToString(),
                        $"Runner timeout after {_options.RunTimeoutSeconds}s"),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return new RemoteExecutionResult(
                new RunOutcome(RunOutcomeKind.Blocked, $"Runner timeout after {_options.RunTimeoutSeconds}s"), []);
        }

        var outcome = SentinelScanner.Scan(result.StdOut);
        outbox?.Enqueue(
            "terminal",
            JsonSerializer.Serialize(
                new DurableTerminalPayload(outcome.Kind.ToString(), outcome.Reason),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        shipper.Add("system", $"[runner] CLI exited {result.ExitCode}; outcome {outcome.Kind}");
        return new RemoteExecutionResult(
            outcome,
            result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<DurableArtifactManifest> UploadResultsAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        DurableRunOutbox? outbox,
        CancellationToken ct)
    {
        var resultsDir = ResultsDir(taskKey);
        var manifest = new List<ArtifactManifestEntry>();
        if (!Directory.Exists(resultsDir))
            return BuildArtifactManifest(manifest);

        var files = Directory.EnumerateFiles(resultsDir, "*", SearchOption.AllDirectories).ToList();
        if (files.Count == 0)
            return BuildArtifactManifest(manifest);

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

        if (outbox is not null)
        {
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
        return BuildArtifactManifest(manifest);
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
            IdempotencyKey: $"completion:{lease.AttemptId}:{outcome.Kind}:{teardown.ResultSha ?? "none"}"), ct);
        _log($"remote-runner-completion recorded: outcome {resp?.Outcome}, state {resp?.TargetState}");
    }

    private async Task ReportUnsecuredWorktreeAsync(
        string taskKey,
        RunLeaseInfoDto lease,
        WorktreeSalvageException ex)
    {
        var refs = $"canonical refs/heads/{ex.Branch} at {ex.RemoteCommitSha ?? "unknown"}; " +
                   $"retained local HEAD {ex.LocalCommitSha ?? "unknown"}";
        var failure = ex.InnerException?.Message.Replace('\r', ' ').Replace('\n', ' ').Trim()
                      ?? ex.Message;
        var gate = $"worktree-blocked: unsecured worktree on {_options.Hostname}: {ex.WorktreePath} " +
                   $"({refs}; failure: {failure}). No ref was overwritten. Restore origin push access, publish the retained HEAD to a new ref, then requeue.";
        _log($"worktree-salvage-escalated task={taskKey} host={_options.Hostname} path={ex.WorktreePath} branch={ex.Branch} localSha={ex.LocalCommitSha ?? "unknown"} remoteSha={ex.RemoteCommitSha ?? "unknown"}");
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

    private async Task ReleaseAsync(RunLeaseInfoDto lease, CancellationToken ct)
    {
        try
        {
            var resp = await _client.ReleaseLeaseAsync(new RunLeaseReleaseRequest(
                lease.TaskKey, lease.LeaseId, lease.FencingToken, _options.RunnerId,
                lease.AttemptId, lease.AuthorityEpoch,
                $"release:{lease.AttemptId}:{lease.LeaseId}"), ct);
            _log($"lease released: {resp.Outcome}");
        }
        catch (Exception ex)
        {
            _log($"lease release failed (server TTL will reclaim it): {ex.Message}");
        }
    }

    private string ResultsDir(string taskKey)
        => Path.Combine(_options.WorkDir, "tasks", GitWorkspace.SafeSegment(taskKey), "results");

    private static async Task SafeAwait(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { /* expected on teardown */ }
        catch { /* background loops already logged their own failures */ }
    }
}

internal sealed record RemoteExecutionResult(RunOutcome Outcome, IReadOnlyList<string> OutputLines);
