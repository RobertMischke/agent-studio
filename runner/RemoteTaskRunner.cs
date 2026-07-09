namespace AgentRunner;

/// <summary>
/// Runs exactly one task end-to-end on the remote host (RM-5 MVP). The lifecycle:
/// acquire the fenced lease, start heartbeating, prepare the git working tree,
/// spawn the agent CLI with the fetched prompt, ship its output to the server,
/// upload the results/ evidence, post an external-completion so the result
/// re-enters the local board, and always release the lease. The runner owns no
/// task state and never pushes git.
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

        using var stopRun = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var heartbeat = new LeaseHeartbeat(_client, _options, lease, _log);
        var heartbeatTask = heartbeat.RunAsync(stopRun, shutdown);

        var shipper = new LogShipper(_client, taskKey, _log);
        var shipperTask = shipper.RunAsync(TimeSpan.FromSeconds(5), stopRun.Token);

        RunOutcome outcome;
        List<string> uploadedFiles = [];
        try
        {
            outcome = await ExecuteAsync(taskKey, lease, shipper, stopRun, shutdown);
            await shipper.FlushAsync(shutdown);
            uploadedFiles = await UploadResultsAsync(taskKey, shutdown);

            if (heartbeat.LeaseLost)
            {
                _log("lease was lost mid-run; skipping completion so the takeover holder owns the outcome");
                return 3;
            }

            await CompleteAsync(taskKey, outcome, uploadedFiles, shutdown);
            _log($"task '{taskKey}' handed back to the local board: {outcome.Kind}");
            return outcome.Kind is RunOutcomeKind.Blocked or RunOutcomeKind.NeedsInput ? 1 : 0;
        }
        finally
        {
            stopRun.Cancel();
            await SafeAwait(heartbeatTask);
            await SafeAwait(shipperTask);
            await ReleaseAsync(lease, shutdown);
        }
    }

    private async Task<RunOutcome> ExecuteAsync(
        string taskKey, RunLeaseInfoDto lease, LogShipper shipper, CancellationTokenSource stopRun, CancellationToken shutdown)
    {
        var workspace = new GitWorkspace(_options, _log);
        var branch = await workspace.PrepareAsync(shutdown);
        shipper.Add("system", $"[runner] working tree ready on branch '{branch}'");

        var prompt = await _client.ReadTaskFileAsync(taskKey, "prompt.md", shutdown)
                     ?? throw new InvalidOperationException($"Task '{taskKey}' has no prompt.md to run.");

        var resultsDir = Path.Combine(_options.WorkDir, "results");
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
            return new RunOutcome(RunOutcomeKind.Blocked, $"Runner timeout after {_options.RunTimeoutSeconds}s");
        }

        var outcome = SentinelScanner.Scan(result.StdOut);
        shipper.Add("system", $"[runner] CLI exited {result.ExitCode}; outcome {outcome.Kind}");
        return outcome;
    }

    private async Task<List<string>> UploadResultsAsync(string taskKey, CancellationToken ct)
    {
        var resultsDir = Path.Combine(_options.WorkDir, "results");
        if (!Directory.Exists(resultsDir)) return [];

        var files = Directory.EnumerateFiles(resultsDir, "*", SearchOption.AllDirectories).ToList();
        if (files.Count == 0) return [];

        var uploads = new List<RunnerArtifactUpload>();
        foreach (var file in files)
        {
            var rel = "results/" + Path.GetRelativePath(resultsDir, file).Replace('\\', '/');
            var bytes = await File.ReadAllBytesAsync(file, ct);
            uploads.Add(new RunnerArtifactUpload(rel, Convert.ToBase64String(bytes)));
        }

        var resp = await _client.UploadArtifactsAsync(new ArtifactIngestRequest(taskKey, uploads), ct);
        _log($"uploaded {resp?.Uploaded ?? 0} artifact(s); commit {resp?.CommitStatus ?? "n/a"}");
        return resp?.Files ?? [];
    }

    private async Task CompleteAsync(string taskKey, RunOutcome outcome, IReadOnlyList<string> files, CancellationToken ct)
    {
        var summary = outcome.Reason is { Length: > 0 }
            ? $"{outcome.SummaryPrefix}: {outcome.Reason}"
            : $"{outcome.SummaryPrefix} on {_options.Hostname}.";
        var deliverables = files.Select(f => new ExternalDeliverable(Path: f, Note: "Uploaded by remote runner.")).ToList();

        var resp = await _client.CompleteAsync(taskKey, new ExternalCompletionRequest(
            Summary: summary,
            Deliverables: deliverables.Count > 0 ? deliverables : null,
            Source: _options.RunnerName,
            TargetState: outcome.TargetState), ct);
        _log($"external-completion recorded: state {resp?.TargetState}, evidence commit {resp?.EvidenceCommitSha ?? "n/a"}");
    }

    private async Task ReleaseAsync(RunLeaseInfoDto lease, CancellationToken ct)
    {
        try
        {
            var resp = await _client.ReleaseLeaseAsync(new RunLeaseReleaseRequest(
                lease.TaskKey, lease.LeaseId, lease.FencingToken, _options.RunnerId), ct);
            _log($"lease released: {resp.Outcome}");
        }
        catch (Exception ex)
        {
            _log($"lease release failed (server TTL will reclaim it): {ex.Message}");
        }
    }

    private static async Task SafeAwait(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { /* expected on teardown */ }
        catch { /* background loops already logged their own failures */ }
    }
}
