using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Replays host-local result transfer state before the daemon claims new work.
/// It never starts an agent CLI. Transfer, acknowledgement, cleanup, and
/// completion resume from the journaled facts of the original RunAttempt.
/// </summary>
public sealed class DurableHandoffRecovery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;

    public DurableHandoffRecovery(
        RunnerOptions options,
        TaskServerClient client,
        Action<string> log)
    {
        _options = options;
        _client = client;
        _log = log;
    }

    public async Task RecoverAllAsync(CancellationToken ct)
    {
        if (!_client.UsesDurableTaskServer) return;
        var root = Path.Combine(_options.WorkDir, "outbox");
        foreach (var outbox in DurableRunOutbox.OpenAll(root))
        {
            if (!string.Equals(
                    outbox.Authority.RunnerId,
                    _options.RunnerId,
                    StringComparison.Ordinal))
                continue;
            if (DurableRunOutbox.IsActive(outbox.Authority.RunId))
                continue;
            if ((string.Equals(
                     outbox.Snapshot.FinalHandoffState,
                     "completed",
                     StringComparison.Ordinal)
                 || string.Equals(
                     outbox.Snapshot.FinalHandoffState,
                     "delivery-verification-failed",
                     StringComparison.Ordinal))
                && outbox.Pending.Count == 0)
                continue;

            try
            {
                await RecoverAsync(outbox, ct);
            }
            catch (WorktreeSalvageException ex)
                when (RemoteTaskRunner.IsRegisteredRepositoryVerificationFailure(ex))
            {
                await RemoteTaskRunner.ReportUnsecuredDurableWorktreeAsync(
                    _options,
                    _client,
                    outbox,
                    _log,
                    ex,
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outbox.RecordHandoffState("transfer-recovery");
                await ReportSafeAsync(outbox, CancellationToken.None);
                _log($"outbox recovery deferred run={outbox.Authority.RunId} task={outbox.Authority.TaskKey} error={ex.Message}");
            }
        }
    }

    private async Task RecoverAsync(DurableRunOutbox outbox, CancellationToken ct)
    {
        var context = Latest<DurableRunContextPayload>(outbox, "run-context")
                      ?? throw new InvalidDataException(
                          $"Run '{outbox.Authority.RunId}' has no durable repository context.");
        var terminal = Latest<DurableTerminalPayload>(outbox, "terminal")
                       ?? throw new InvalidDataException(
                           $"Run '{outbox.Authority.RunId}' has no durable terminal fact.");
        var workspace = new GitWorkspace(
            _options,
            outbox.Authority.TaskKey,
            _log,
            context.RepositoryId,
            context.RepositoryUrl,
            context.DefaultBranch);

        var finalItem = outbox.Items.LastOrDefault(item => item.Kind == "final-result");
        ImmutableResultEnvelope envelope;
        WorktreeTeardownResult secured;
        if (finalItem is null)
        {
            var manifest = LatestManifest(outbox)
                           ?? await JournalArtifactsAsync(outbox, ct);
            outbox.RecordHandoffState("transferring");
            await ReportSafeAsync(outbox, ct);
            secured = await workspace.SecureForHandoffAsync(
                terminal.Outcome,
                outbox.Authority.RunId,
                ct);
            var dependencies = await workspace.ReadDependencyIdentitiesAsync(ct);
            envelope = new ImmutableResultEnvelope(
                context.RepositoryId,
                outbox.Authority.RunId,
                context.BaseSha,
                secured.ResultSha
                ?? throw new InvalidOperationException("Recovered transfer has no result SHA."),
                secured.ImmutableResultRef,
                null,
                manifest.Digest,
                dependencies.Submodules,
                dependencies.LfsObjects);
            outbox.Enqueue(
                "git-facts",
                JsonSerializer.Serialize(
                    new DurableGitFactsPayload(
                        context.RepositoryId,
                        context.BaseSha,
                        secured.ResultSha
                        ?? throw new InvalidOperationException(
                            "Recovered transfer has no result SHA."),
                        secured.ImmutableResultRef,
                        secured.Reconciliation,
                        secured.Reconciliation?.Kind == "divergent"
                            ? "inspect-preserved-divergent-tips"
                            : "retry-transfer-without-coding"),
                    Json));
            finalItem = outbox.Enqueue(
                "final-result",
                JsonSerializer.Serialize(envelope, Json));
        }
        else
        {
            envelope = JsonSerializer.Deserialize<ImmutableResultEnvelope>(
                           finalItem.PayloadJson,
                           Json)
                       ?? throw new InvalidDataException("Durable result envelope is empty.");
            secured = new WorktreeTeardownResult(
                true,
                ResultSha: envelope.ResultSha,
                Branch: null,
                CommitSha: null,
                BranchUrl: null,
                ImmutableResultRef: envelope.ImmutableRemoteRef);
        }

        foreach (var item in outbox.Pending.Where(item => item.Sequence < finalItem.Sequence))
        {
            await _client.SendOutboxItemAsync(outbox.Authority, item, ct);
            outbox.Acknowledge(item.Sequence);
        }

        ResultHandoffAck acknowledgement;
        if (outbox.HandoffAcknowledgement is null)
        {
            acknowledgement = await _client.AcknowledgeResultHandoffAsync(
                outbox.Authority,
                finalItem,
                envelope,
                ct);
            outbox.RecordHandoffAcknowledgement(acknowledgement);
        }
        else
        {
            acknowledgement = outbox.HandoffAcknowledgement;
        }
        var envelopeDigest = acknowledgement.EnvelopeDigest;
        await ReportSafeAsync(outbox, ct);

        if (Directory.Exists(workspace.RepoPath))
        {
            await workspace.TeardownAfterHandoffAsync(
                secured,
                acknowledgement,
                outbox.Authority.RunId,
                envelopeDigest,
                ct);
        }

        var completion = outbox.Items.LastOrDefault(item => item.Kind == "completion")
                         ?? outbox.Enqueue(
                             "completion",
                             JsonSerializer.Serialize(
                                 new DurableCompletionPayload(
                                     terminal.Outcome,
                                     terminal.Reason,
                                     envelopeDigest),
                                 Json));
        if (completion.Sequence > outbox.LastAcknowledgedSequence)
        {
            await _client.SendOutboxItemAsync(outbox.Authority, completion, ct);
            outbox.Acknowledge(completion.Sequence);
        }
        outbox.RecordHandoffState("completed", envelopeDigest);
        await ReportSafeAsync(outbox, ct);
        _log($"outbox recovery completed run={outbox.Authority.RunId} task={outbox.Authority.TaskKey} resultSha={envelope.ResultSha}");
    }

    private async Task<DurableArtifactManifest> JournalArtifactsAsync(
        DurableRunOutbox outbox,
        CancellationToken ct)
    {
        var results = Path.Combine(
            _options.WorkDir,
            "tasks",
            GitWorkspace.SafeSegment(outbox.Authority.TaskKey),
            "results");
        var entries = new List<ArtifactManifestEntry>();
        if (Directory.Exists(results))
        {
            foreach (var path in Directory.EnumerateFiles(
                         results,
                         "*",
                         SearchOption.AllDirectories))
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                var relative = "results/" + Path.GetRelativePath(results, path).Replace('\\', '/');
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                entries.Add(new ArtifactManifestEntry(relative, sha, bytes.LongLength));
                outbox.Enqueue(
                    "artifact",
                    JsonSerializer.Serialize(
                        new DurableArtifactPayload(
                            relative,
                            TaskServerClient.MediaTypeForPath(relative),
                            Convert.ToBase64String(bytes),
                            sha),
                        Json));
            }
        }
        var manifest = RemoteTaskRunner.BuildArtifactManifest(entries);
        outbox.Enqueue("artifact-manifest", manifest.Json);
        return manifest;
    }

    private static T? Latest<T>(DurableRunOutbox outbox, string kind)
        where T : class
    {
        var item = outbox.Items.LastOrDefault(item => item.Kind == kind);
        return item is null ? null : JsonSerializer.Deserialize<T>(item.PayloadJson, Json);
    }

    private static DurableArtifactManifest? LatestManifest(DurableRunOutbox outbox)
    {
        var item = outbox.Items.LastOrDefault(item => item.Kind == "artifact-manifest");
        if (item is null) return null;
        var digest = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.PayloadJson)))
            .ToLowerInvariant();
        return new DurableArtifactManifest(digest, item.PayloadJson);
    }

    private async Task ReportSafeAsync(DurableRunOutbox outbox, CancellationToken ct)
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
            _log($"outbox recovery observability deferred run={outbox.Authority.RunId} error={ex.Message}");
        }
    }
}
