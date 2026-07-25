using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Executes one separately fenced ReviewAttempt. The Task Server supplies only
/// immutable subject facts and a review plan; this service owns every checkout,
/// build, test, provider CLI, semantic, vision, and artifact verification
/// process, then returns one fenced report.
/// </summary>
public sealed class RemoteReviewExecutor
{
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;

    public RemoteReviewExecutor(RunnerOptions options, TaskServerClient client, Action<string> log)
    {
        _options = options;
        _client = client;
        _log = log;
    }

    public async Task<int> RunClaimedAsync(ReviewClaimResponse claim, CancellationToken shutdown)
    {
        if (claim.Attempt is null || claim.Subject is null || claim.Lease is null)
            throw new ArgumentException("Claim must contain an attempt, immutable subject, and review lease.", nameof(claim));
        var attempt = claim.Attempt;
        var subject = claim.Subject;
        var lease = claim.Lease;
        var workspace = new RemoteReviewWorkspace(_options, subject, lease, _log);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var heartbeat = RenewLoopAsync(attempt.AttemptId, lease, heartbeatStop.Token);
        ReviewReportDto? report = null;
        try
        {
            ReviewExecutionEvidence evidence;
            string? failureClassification = null;
            string? summary = null;
            try
            {
                _log($"materializing review attempt={attempt.AttemptId} subject={subject.SubjectId} expected={subject.ExpectedResultSha}");
                await workspace.PrepareAsync(_client, shutdown);
                evidence = await workspace.ExecutePlanAsync(shutdown);
                summary = evidence.Outcome == "Pass"
                    ? "All applicable remote review aspects passed."
                    : "At least one remote review aspect found a product concern.";
            }
            catch (ReviewInfrastructureException exception)
            {
                failureClassification = exception.Classification;
                summary = exception.Message;
                evidence = InfrastructureEvidence(workspace, subject, lease, exception.Classification);
                _log($"review infrastructure outcome attempt={attempt.AttemptId} classification={exception.Classification}: {exception.Message}");
                var failedCapability = CapabilityFor(exception.Classification);
                if (failedCapability is not null)
                {
                    await _client.ReportCapabilityFailureAsync(
                        failedCapability,
                        exception.Classification,
                        exception.Message.Length <= 500 ? exception.Message : exception.Message[..500],
                        $"review-capability:{attempt.AttemptId}:{lease.Fence}:{failedCapability}",
                        "review",
                        attempt.AttemptId,
                        lease.Fence,
                        CancellationToken.None);
                }
            }

            var request = new ReviewReportRequest(
                lease.ExecutorId,
                lease.InstanceId,
                lease.LeaseId,
                lease.Fence,
                $"review-report:{attempt.AttemptId}:{lease.Fence}",
                failureClassification is null ? evidence.Outcome : "ReviewInfra",
                failureClassification,
                summary,
                evidence.Workspace,
                workspace.EnvironmentEvidence(),
                evidence.Commands,
                evidence.Artifacts,
                evidence.Verdicts);
            report = await _client.ReportReviewAsync(attempt.AttemptId, request, CancellationToken.None);
            _log($"review report accepted attempt={attempt.AttemptId} outcome={report.Outcome} classification={report.FailureClassification ?? "none"} taskState={report.TaskState}");
            return report.Outcome == "Pass" ? 0 : report.Outcome == "ProductFailure" ? 2 : 3;
        }
        finally
        {
            heartbeatStop.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) { }
            catch (Exception exception) { _log($"review heartbeat stopped with error: {exception.Message}"); }

            var removed = false;
            try
            {
                removed = await workspace.CleanupAsync();
            }
            catch (Exception exception)
            {
                _log($"review workspace cleanup failed attempt={attempt.AttemptId} path={workspace.AttemptRoot}: {exception.Message}");
            }

            try
            {
                var cleanup = await _client.CleanupReviewAsync(
                    attempt.AttemptId,
                    new ReviewCleanupRequest(
                        lease.ExecutorId,
                        lease.InstanceId,
                        lease.LeaseId,
                        lease.Fence,
                        $"review-cleanup:{attempt.AttemptId}:{lease.Fence}",
                        removed,
                        removed ? null : report is null
                            ? "ExecutorStoppedBeforeReport"
                            : "WorkspaceCleanupFailed"),
                    CancellationToken.None);
                _log($"review cleanup recorded attempt={attempt.AttemptId} status={cleanup.Status} retry={cleanup.RetryScheduled}");
            }
            catch (Exception exception)
            {
                _log($"review cleanup report rejected attempt={attempt.AttemptId}: {exception.Message}");
            }
        }
    }

    private async Task RenewLoopAsync(
        string attemptId,
        ReviewLeaseDto lease,
        CancellationToken stop)
    {
        var sequence = 0L;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Max(1, Math.Min(_options.HeartbeatSeconds, Math.Max(1, _options.TtlSeconds / 3)))));
        while (await timer.WaitForNextTickAsync(stop))
        {
            await _client.RenewReviewLeaseAsync(
                attemptId,
                new ReviewLeaseRenewRequest(
                    lease.ExecutorId,
                    lease.InstanceId,
                    lease.LeaseId,
                    lease.Fence,
                    $"review-renew:{attemptId}:{lease.Fence}:{++sequence}",
                    _options.TtlSeconds),
                stop);
        }
    }

    private static ReviewExecutionEvidence InfrastructureEvidence(
        RemoteReviewWorkspace workspace,
        ReviewSubjectDto subject,
        ReviewLeaseDto lease,
        string classification)
    {
        var repositoryId = classification == "RepositoryMismatch" ? "unknown" : subject.RepositoryId;
        var actualHead = classification == "ShaMismatch" ? "unknown" : subject.ExpectedResultSha;
        var dirtyBefore = classification == "DirtyBefore";
        var proof = new ReviewWorkspaceProofDto(
            repositoryId,
            subject.ExpectedResultSha,
            actualHead,
            "unknown",
            dirtyBefore,
            false,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(workspace.AttemptRoot))).ToLowerInvariant(),
            lease.ResourceNamespace);
        return new ReviewExecutionEvidence(
            "ReviewInfra",
            proof,
            [],
            [],
            []);
    }

    private static string? CapabilityFor(string classification)
        => classification switch
        {
            "SnapshotUnavailable" or "RepositoryMismatch" or "ShaMismatch"
                => CapabilityProtocol.RepositoryAccess,
            "ToolUnavailable" => ReviewCapabilities.SemanticReview,
            "VisionUnavailable" => CapabilityProtocol.Vision,
            "DiskFull" => CapabilityProtocol.Disk,
            "LeaseAuthorityInvalid" => CapabilityProtocol.LeaseAuthority,
            _ => null,
        };
}
