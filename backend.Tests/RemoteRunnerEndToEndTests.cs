extern alias Runner;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using System.Diagnostics;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

// The standalone runner's wire types, aliased so its re-declared WireModels
// (RunLeaseAcquireRequest, LogIngestRequest, ArtifactIngestRequest, ...) never
// collide with the backend's own same-named server records, which this test
// project imports globally. The whole point of the test is that these two
// independently-declared shapes are wire-compatible.
using RClient = Runner::AgentRunner.TaskServerClient;
using RAcquire = Runner::AgentRunner.RunLeaseAcquireRequest;
using RHeartbeat = Runner::AgentRunner.RunLeaseHeartbeatRequest;
using RRelease = Runner::AgentRunner.RunLeaseReleaseRequest;
using RClaim = Runner::AgentRunner.RunnerClaimRequest;
using RTelemetry = Runner::AgentRunner.HostTelemetrySample;
using RClaimStatus = Runner::AgentRunner.RunnerClaimStatus;
using RGitCapability = Runner::AgentRunner.RunnerGitCapabilityRequest;
using RLogIngest = Runner::AgentRunner.LogIngestRequest;
using RCliLine = Runner::AgentRunner.CliOutputLine;
using RArtifactIngest = Runner::AgentRunner.ArtifactIngestRequest;
using RArtifact = Runner::AgentRunner.RunnerArtifactUpload;
using RComplete = Runner::AgentRunner.ExternalCompletionRequest;
using RDeliverable = Runner::AgentRunner.ExternalDeliverable;
using RRemoteComplete = Runner::AgentRunner.RemoteRunCompletionRequest;
using ROptions = Runner::AgentRunner.RunnerOptions;
using RTaskRunner = Runner::AgentRunner.RemoteTaskRunner;
using RRemoteCompletionResponse = Runner::AgentRunner.RemoteRunCompletionResponse;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// RM-5 acceptance, proven in-process: the standalone runner's <b>real</b>
/// <see cref="RClient"/> drives one task through the whole remote lifecycle
/// against the <b>real</b> Task Server endpoints hosted by
/// <see cref="WebApplicationFactory{TEntryPoint}"/> — fenced lease → heartbeat →
/// prompt fetch → log ship → artifact upload → out-of-band completion → release.
///
/// <para>
/// This closes the gap the code review flagged: the transport hinges on the
/// RM-3/RM-4 endpoints and record shapes matching <c>runner/WireModels.cs</c>,
/// which unit tests on either side cannot prove alone. Here the runner's own
/// HTTP client + its independently-declared wire records round-trip through the
/// server's serialization and endpoint logic, and the task physically re-enters
/// the board in <c>5-human-review</c> with the runner's logs and evidence — the
/// acceptance shape ("result + logs appear in the local board"), minus only the
/// physical Hetzner host, which is a deployment detail the runbook covers.
/// </para>
///
/// <para>
/// The project is named <c>agent-runner-01</c> (the acceptance project) but has
/// no saved runner mode, so the in-process <c>TaskRunnerService</c> stays in
/// manual mode and never races the runner for the seeded card.
/// </para>
/// </summary>
// MachineBound 20.07.: E2E ueber Server-API+Prozesse, lastabhaengig
[Trait("Category", "MachineBound")]
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class RemoteRunnerEndToEndTests : IDisposable
{
    private static readonly JsonSerializerOptions ApiJson = CreateApiJson();
    private const string ProjectName = "agent-runner-01";
    private const string RunnerId = "agent-runner-e2e";
    private const string TaskKey = "AGT-RUNNER-E2E";

    private readonly string _workspace;
    private readonly string _watchPath;

    public RemoteRunnerEndToEndTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-runner-e2e-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Runner_drives_one_task_end_to_end_through_the_server_api()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote runner smoke",
            "Do the remote thing and stop.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;

        // 0a. Register the runner identity. The server's X-Client-Id boundary
        //     rejects every write from an unregistered id with 401, so the runner
        //     adopts the server-assigned id before it touches the lease.
        var clientId = await client.RegisterAsync("agent-runner-01 remote", "service", ct);
        Assert.False(string.IsNullOrWhiteSpace(clientId));
        Assert.Equal(clientId, client.ClientId);

        // 0b. Prompt fetch. The runner reads prompt.md over the API (code itself
        //     arrives via git origin, out of scope for an in-process test).
        var prompt = await client.ReadTaskFileAsync(TaskKey, "prompt.md", ct);
        Assert.Equal("Do the remote thing and stop.", prompt);

        // 1. Acquire the fenced lease → this runner may proceed, with a token.
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "claude"), ct);
        Assert.True(lease.Granted, $"lease not granted: {lease.Outcome} {lease.Message}");
        Assert.NotNull(lease.Lease);
        Assert.True(lease.Lease!.FencingToken > 0);
        var leaseId = lease.Lease.LeaseId;
        var token = lease.Lease.FencingToken;

        // 2. Heartbeat renews the lease with the fencing token.
        var renew = await client.RenewLeaseAsync(new RHeartbeat(
            TaskKey, leaseId, token, RunnerId, null,
            lease.Lease.AttemptId, lease.Lease.AuthorityEpoch, "e2e-renew-1"), ct);
        Assert.True(renew.Granted, $"renew not granted: {renew.Outcome} {renew.Message}");

        // 3. Ship CLI output — appended to the task's durable logs/cli-output.log.
        var logResp = await client.IngestLogsAsync(new RLogIngest(TaskKey,
        [
            new RCliLine(DateTime.UtcNow, "stdout", "remote runner: starting task"),
            new RCliLine(DateTime.UtcNow, "stdout", "remote runner: [[TASK_DONE]]"),
        ],
            RunnerId: lease.Lease.RunnerId,
            LeaseId: leaseId,
            FencingToken: token,
            AttemptId: lease.Lease.AttemptId,
            Fence: token,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "e2e-logs-1"), ct);
        Assert.NotNull(logResp);
        Assert.Equal(2, logResp!.Appended);

        // 4. Upload evidence — decoded under the task's results/ folder.
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("evidence bytes"));
        var artResp = await client.UploadArtifactsAsync(new RArtifactIngest(TaskKey,
        [
            new RArtifact("runner-evidence--real.txt", content),
        ],
            RunnerId: lease.Lease.RunnerId,
            LeaseId: leaseId,
            FencingToken: token,
            AttemptId: lease.Lease.AttemptId,
            Fence: token,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "e2e-artifacts-1"), ct);
        Assert.NotNull(artResp);
        Assert.Equal(1, artResp!.Uploaded);
        Assert.Contains("results/runner-evidence--real.txt", artResp.Files);

        // 5. Reconcile out-of-band: the card re-enters the local board.
        var completion = await client.CompleteAsync(TaskKey, new RComplete(
            "Ran on the remote runner; evidence uploaded.",
            [new RDeliverable(Path: "results/runner-evidence--real.txt", Note: "runner evidence")],
            Source: ProjectName), ct);
        Assert.NotNull(completion);
        Assert.Equal(TaskStates.HumanReview, completion!.TargetState);

        // 6. Release the lease so the slot is free for the next run.
        var release = await client.ReleaseLeaseAsync(new RRelease(
            TaskKey, leaseId, token, RunnerId, lease.Lease.AttemptId, lease.Lease.AuthorityEpoch, "e2e-release-1"), ct);
        Assert.Equal("Released", release.Outcome);

        // The board now shows the finished card in 5-human-review, carrying the
        // runner's shipped logs and uploaded evidence — RM-5 acceptance, proven
        // against the real API rather than asserted from the diff.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, TaskKey)));
        var moved = Path.Combine(_watchPath, TaskStates.HumanReview, TaskKey);
        Assert.True(Directory.Exists(moved), "card did not move to 5-human-review");

        var cliLog = File.ReadAllText(Path.Combine(moved, "logs", "cli-output.log"));
        Assert.Contains("remote runner: starting task", cliLog);
        Assert.Contains("[[TASK_DONE]]", cliLog);

        var evidence = File.ReadAllText(Path.Combine(moved, "results", "runner-evidence--real.txt"));
        Assert.Equal("evidence bytes", evidence);

        var status = File.ReadAllText(Path.Combine(moved, "status.md"));
        Assert.Contains(ProjectName, status);
    }

    [Fact]
    public async Task Health_probe_reports_reachable_against_the_live_server()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);

        // The connectivity preflight hits the server's open /healthz route. A live
        // server returns a null reason (reachable); the runbook's readiness check
        // (agent-runner --health-check) and the run preflight both branch on this.
        var reason = await client.ProbeHealthAsync(CancellationToken.None);

        Assert.Null(reason);
    }

    [Fact]
    public async Task Recognized_remote_task_done_uses_regular_runner_completion_not_external_completion()
    {
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        SeedTask(TaskStates.Progress, TaskKey, "Remote done", "Make a trivial change.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);

        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);
        Assert.NotNull(lease.Lease);

        await client.IngestLogsAsync(new RLogIngest(TaskKey,
        [
            new RCliLine(DateTime.UtcNow, "stdout", "Implemented and verified."),
            new RCliLine(DateTime.UtcNow, "stdout", "[[TASK_DONE]]"),
        ],
            RunnerId: lease.Lease!.RunnerId,
            LeaseId: lease.Lease.LeaseId,
            FencingToken: lease.Lease.FencingToken,
            AttemptId: lease.Lease.AttemptId,
            Fence: lease.Lease.FencingToken,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "remote-done-logs"), ct);

        var completionRequest = new RRemoteComplete(
            TaskKey,
            lease.Lease!.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: ProjectName,
            ExitCode: 0,
            ResultSha: resultSha,
            AttemptChainId: lease.Lease.LeaseId,
            Repository: "https://example.invalid/agent-studio.git",
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "remote-done-completion");

        var missingAuthority = await http.PostAsJsonAsync(
            "/api/runner/completion", completionRequest with { AttemptId = null }, ct);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, missingAuthority.StatusCode);
        var wrongLease = await http.PostAsJsonAsync(
            "/api/runner/completion", completionRequest with
            {
                LeaseId = "lease-from-another-holder",
                AttemptChainId = "lease-from-another-holder",
                IdempotencyKey = "remote-done-wrong-lease",
            }, ct);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, wrongLease.StatusCode);
        var wrongLeaseResult = await wrongLease.Content.ReadFromJsonAsync<RRemoteCompletionResponse>(ApiJson, ct);
        Assert.Equal(AttemptWriteStatus.StaleFence.ToString(), wrongLeaseResult!.FailureClassification);

        var salvageIsNotResultAuthority = await http.PostAsJsonAsync(
            "/api/runner/completion", completionRequest with
            {
                ResultSha = null,
                SalvageCommitSha = "salvage-is-evidence-only",
                IdempotencyKey = "remote-done-missing-result-sha",
            }, ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, salvageIsNotResultAuthority.StatusCode);
        var missingResult = await salvageIsNotResultAuthority.Content.ReadFromJsonAsync<RRemoteCompletionResponse>(ApiJson, ct);
        Assert.Equal(AttemptWriteStatus.Invalid.ToString(), missingResult!.FailureClassification);

        var completion = await client.CompleteRunAsync(completionRequest, ct);

        Assert.NotNull(completion);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        Assert.Equal(lease.Lease.AttemptId, completion.RunAttemptId);
        Assert.False(string.IsNullOrWhiteSpace(completion.ReviewAttemptId));
        Assert.False(string.IsNullOrWhiteSpace(completion.ReviewSubjectId));

        var projection = await http.GetFromJsonAsync<AttemptAuthorityProjection>(
            $"/api/attempts/tasks/{TaskKey}", ApiJson, ct);
        Assert.Equal(resultSha, projection!.CurrentRunAttempt!.ResultSha);
        Assert.Equal(resultSha, projection.CurrentReviewSubject!.ExpectedResultSha);

        var duplicate = await client.CompleteRunAsync(completionRequest, ct);
        Assert.Equal(completion.ReviewAttemptId, duplicate!.ReviewAttemptId);
        Assert.Equal("duplicate delivery", duplicate.Message);

        var claimReviewResponse = await http.PostAsJsonAsync(
            $"/api/attempts/reviews/{completion.ReviewAttemptId}/claim",
            new ClaimReviewAttemptRequest(completion.ReviewAttemptId!, "review-worker", "review-host", "claim-review", 60), ct);
        claimReviewResponse.EnsureSuccessStatusCode();
        var claimReview = await claimReviewResponse.Content.ReadFromJsonAsync<AttemptWriteResult>(ApiJson, ct);
        Assert.NotNull(claimReview?.ReviewAttempt);

        var reviewWrite = new AttemptWriteReference(
            completion.ReviewAttemptId!,
            claimReview!.ReviewAttempt!.LastFence,
            claimReview.ReviewAttempt.AuthorityEpoch,
            "settle-review-mismatched-sha");
        var mismatchResponse = await http.PostAsJsonAsync(
            $"/api/attempts/reviews/{completion.ReviewAttemptId}/settle",
            new SettleReviewAttemptRequest(reviewWrite, "61306343", ReviewTerminalOutcome.Pass), ct);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, mismatchResponse.StatusCode);
        var mismatch = await mismatchResponse.Content.ReadFromJsonAsync<AttemptWriteResult>(ApiJson, ct);
        Assert.Equal(AttemptWriteStatus.SubjectMismatch, mismatch!.Status);
        Assert.Equal("immutable-result-mismatch", mismatch.ReviewAttempt!.FailureClassification);
        Assert.Equal(ReviewTerminalOutcome.InfrastructureFailure, mismatch.ReviewAttempt.Outcome);

        var retryResponse = await http.PostAsJsonAsync(
            "/api/attempts/reviews",
            new CreateReviewAttemptRequest(
                TaskKey,
                mismatch.ReviewAttempt.RepositoryId,
                mismatch.ReviewAttempt.Subject.ExpectedResultSha,
                mismatch.ReviewAttempt.SourceRunAttemptId,
                mismatch.ReviewAttempt.Subject.TaskRequirementsHash,
                mismatch.ReviewAttempt.Subject.ReviewPolicyHash,
                mismatch.ReviewAttempt.Subject.EvidenceDigestInputs,
                "retry-review-same-subject",
                mismatch.ReviewAttempt.AttemptId), ct);
        retryResponse.EnsureSuccessStatusCode();
        var retry = await retryResponse.Content.ReadFromJsonAsync<AttemptWriteResult>(ApiJson, ct);
        Assert.NotEqual(mismatch.ReviewAttempt.AttemptId, retry!.ReviewAttempt!.AttemptId);
        Assert.Equal(mismatch.ReviewAttempt.Subject.SubjectId, retry.ReviewAttempt.Subject.SubjectId);
        var moved = Path.Combine(_watchPath, TaskStates.AutoReview, TaskKey);
        Assert.True(Directory.Exists(moved));

        var taskJson = File.ReadAllText(Path.Combine(moved, "task.json"));
        Assert.DoesNotContain("externalCompletion", taskJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(LifecyclePhases.PostProcessingRunning, taskJson, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(moved, "lifecycle.json")));
        var timeline = File.ReadAllText(Path.Combine(moved, "logs", "timeline.jsonl"));
        Assert.Contains("agent_run_finished", timeline);
        Assert.Contains("resultSha", timeline);
        Assert.Contains("idempotencyKey", timeline);
        Assert.Contains($"\"runId\":\"{lease.Lease.AttemptId}\"", timeline, StringComparison.Ordinal);
        Assert.Contains("\"idempotencyKey\":\"lane-completion:remote-done-completion\"", timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("external_completion", timeline);
        Assert.Equal(1, timeline.Split("agent_run_finished", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task Monolith_v1_review_plane_claims_and_accepts_fenced_grade_end_to_end()
    {
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        const string baseSha = "4136f00d4136f00d4136f00d4136f00d4136f00d";
        const string artifactDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string reviewRunnerId = "review-runner-e2e";
        const string reviewInstance = "review-host:4243";
        SeedTask(TaskStates.Progress, TaskKey, "Remote review plane", "Build and verify.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var coding = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await coding.RegisterAsync(ProjectName, "service", ct);
        var lease = await coding.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "coding-host", 4242, "codex"), ct);
        Assert.True(lease.Granted);

        var completion = await coding.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            lease.Lease!.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            ResultSha: resultSha,
            AttemptChainId: lease.Lease.LeaseId,
            Repository: "https://example.invalid/agent-studio.git",
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "v1-review-plane-completion",
            BaseSha: baseSha,
            ImmutableResultRef: "refs/heads/agent-studio/results/e2e",
            ArtifactManifestDigest: artifactDigest), ct);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);

        var compatibility = await http.PostAsJsonAsync(
            "/api/v1/protocol/compatibility",
            new Contract.ProtocolCompatibilityRequest(
                "review-runner",
                "1.0.0",
                Contract.TaskServerProtocol.Current),
            ct);
        compatibility.EnsureSuccessStatusCode();
        var compatible = await compatibility.Content.ReadFromJsonAsync<Contract.ProtocolCompatibilityResponse>(ct);
        Assert.True(compatible!.Supported);
        Assert.Contains("review-plane", compatible.Server.Capabilities!);

        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{reviewRunnerId}",
            new Contract.RegisterRunnerRequest(
                reviewRunnerId,
                "review-host",
                reviewInstance,
                "1.0.0",
                Contract.TaskServerProtocol.Current,
                [
                    Contract.ReviewCapabilities.ReviewExecutor,
                    Contract.ReviewCapabilities.GitMaterialization,
                    Contract.ReviewCapabilities.SemanticReview,
                ]),
            ct);
        registration.EnsureSuccessStatusCode();

        using var reviewClient = new RClient(
            http,
            reviewRunnerId,
            usesDurableTaskServer: true);
        var claim = await reviewClient.ClaimReviewAsync(
            new Contract.ReviewClaimRequest(reviewRunnerId, reviewInstance, 120),
            ct);
        Assert.Equal("claimed", claim.Status);
        Assert.Equal(completion.ReviewAttemptId, claim.Attempt!.AttemptId);
        Assert.True(claim.Lease!.Fence > 0);
        Assert.True(claim.Lease.AuthorityEpoch > 0);
        Assert.Equal(resultSha, claim.Subject!.ExpectedResultSha);

        var renewed = await reviewClient.RenewReviewLeaseAsync(
            claim.Attempt.AttemptId,
            new Contract.ReviewLeaseRenewRequest(
                reviewRunnerId,
                reviewInstance,
                claim.Lease.LeaseId,
                claim.Lease.Fence,
                "v1-review-renew",
                120,
                claim.Lease.AuthorityEpoch),
            ct);
        Assert.Equal(claim.Lease.AuthorityEpoch, renewed.AuthorityEpoch);

        var reportRequest = new Contract.ReviewReportRequest(
            reviewRunnerId,
            reviewInstance,
            claim.Lease.LeaseId,
            claim.Lease.Fence,
            "v1-review-grade",
            "Pass",
            null,
            "Focused .NET gate passed.",
            new Contract.ReviewWorkspaceProofDto(
                claim.Subject.RepositoryId,
                resultSha,
                resultSha,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                false,
                false,
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                claim.Lease.ResourceNamespace),
            new Contract.ReviewEnvironmentDto(
                "review-host",
                reviewRunnerId,
                reviewInstance,
                "linux",
                "x64",
                "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>()),
            [],
            [],
            [new Contract.ReviewVerdictDto("build-tests", "pass", "GatePassed", "Focused gate passed.")],
            claim.Lease.AuthorityEpoch);
        var report = await reviewClient.ReportReviewAsync(
            claim.Attempt.AttemptId,
            reportRequest,
            ct);
        Assert.Equal("Pass", report.Outcome);
        Assert.Equal(TaskStates.HumanReview, report.TaskState);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, TaskKey)));

        var duplicate = await reviewClient.ReportReviewAsync(
            claim.Attempt.AttemptId,
            reportRequest,
            ct);
        Assert.Equal(report.ReportId, duplicate.ReportId);

        var reportedAttempt = await http.GetFromJsonAsync<Contract.ReviewAttemptDto>(
            $"/api/v1/reviews/attempts/{claim.Attempt.AttemptId}",
            ct);
        Assert.Equal("Pass", reportedAttempt!.Outcome);

        var cleanup = await reviewClient.CleanupReviewAsync(
            claim.Attempt.AttemptId,
            new Contract.ReviewCleanupRequest(
                reviewRunnerId,
                reviewInstance,
                claim.Lease.LeaseId,
                claim.Lease.Fence,
                "v1-review-cleanup",
                true,
                AuthorityEpoch: claim.Lease.AuthorityEpoch),
            ct);
        Assert.Equal("cleaned", cleanup.Status);

        var handoff = await http.GetFromJsonAsync<Contract.ResultHandoffDto>(
            $"/api/v1/runs/{lease.Lease.AttemptId}/result-handoff",
            ct);
        Assert.Equal(resultSha, handoff!.Envelope.ResultSha);
        Assert.Equal(lease.Lease.AttemptId, handoff.Envelope.SourceRunAttemptId);
    }

    [Fact]
    public async Task Remote_done_without_fenced_result_sha_fails_closed_before_review()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote done", "Make a trivial change.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);

        var error = await Assert.ThrowsAsync<Runner::AgentRunner.TaskServerException>(() =>
            client.CompleteRunAsync(new RRemoteComplete(
                TaskKey,
                lease.Lease!.LeaseId,
                lease.Lease.FencingToken,
                RunnerId,
                "Done",
                Source: ProjectName,
                ExitCode: 0,
                AttemptId: lease.Lease!.AttemptId,
                AuthorityEpoch: lease.Lease.AuthorityEpoch,
                IdempotencyKey: "missing-result"), ct));

        Assert.Equal(400, error.StatusCode);
        var retainedTask = Assert.Single(Directory.GetDirectories(
            _watchPath, TaskKey, SearchOption.AllDirectories));
        Assert.NotEqual(TaskStates.AutoReview, Directory.GetParent(retainedTask)!.Name);
        Assert.False(File.Exists(ReviewSubjectStore.PathFor(retainedTask)));
    }

    [Fact]
    public async Task Failed_artifact_write_can_retry_the_same_idempotency_key()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote artifact retry", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);

        var invalid = new RArtifactIngest(TaskKey,
        [
            new RArtifact("retry.txt", "not-base64"),
        ],
            RunnerId: lease.Lease!.RunnerId,
            LeaseId: lease.Lease.LeaseId,
            FencingToken: lease.Lease.FencingToken,
            AttemptId: lease.Lease.AttemptId,
            Fence: lease.Lease.FencingToken,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "artifact-retry-1");
        var invalidResponse = await http.PostAsJsonAsync("/api/runner/artifacts", invalid, ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        var corrected = invalid with
        {
            Artifacts = [new RArtifact(
                "retry.txt", Convert.ToBase64String(Encoding.UTF8.GetBytes("durable evidence")))],
        };
        var retryResponse = await http.PostAsJsonAsync("/api/runner/artifacts", corrected, ct);
        retryResponse.EnsureSuccessStatusCode();
        var retry = await retryResponse.Content.ReadFromJsonAsync<ArtifactIngestResponse>(ApiJson, ct);

        Assert.Equal(1, retry!.Uploaded);
        var artifactPath = Assert.Single(Directory.GetFiles(
            _watchPath, "retry.txt", SearchOption.AllDirectories));
        Assert.Equal("durable evidence", File.ReadAllText(artifactPath));
    }

    [Fact]
    public async Task Log_delivery_retry_after_authority_persist_failure_does_not_append_twice()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote log crash window", "Prompt.");
        var writer = new ControllableAtomicJsonFileWriter();

        using var factory = BuildFactory(writer);
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);

        var failNextAuthorityPersist = true;
        writer.ShouldFail = (path, _) =>
        {
            if (!path.EndsWith(AttemptAuthorityService.RelativePath, StringComparison.Ordinal)
                || !failNextAuthorityPersist)
            {
                return false;
            }
            failNextAuthorityPersist = false;
            return true;
        };
        var delivery = new RLogIngest(TaskKey,
        [
            new RCliLine(DateTime.UtcNow, "stdout", "crash-window-line"),
        ],
            RunnerId: lease.Lease!.RunnerId,
            LeaseId: lease.Lease.LeaseId,
            FencingToken: lease.Lease.FencingToken,
            AttemptId: lease.Lease.AttemptId,
            Fence: lease.Lease.FencingToken,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "log-crash-window-1");

        var failed = await http.PostAsJsonAsync("/api/runner/logs", delivery, ct);
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, failed.StatusCode);

        var retry = await http.PostAsJsonAsync("/api/runner/logs", delivery, ct);
        retry.EnsureSuccessStatusCode();

        var logPath = Assert.Single(Directory.GetFiles(
            _watchPath, "cli-output.log", SearchOption.AllDirectories));
        var occurrences = File.ReadLines(logPath).Count(line => line.Contains("crash-window-line", StringComparison.Ordinal));
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task Fenced_worktree_cleanup_failure_routes_to_human_review_with_durable_gate_evidence()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote cleanup failure", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);

        const string gate = "worktree-blocked: unsecured worktree on runner-host: /runner/worktrees/AGT-100";
        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            lease.Lease!.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Blocked",
            Reason: "salvage push failed",
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "cleanup-blocked-1",
            GateItems: [gate]), ct);

        Assert.Equal(TaskStates.HumanReview, completion!.TargetState);
        var moved = Path.Combine(_watchPath, TaskStates.HumanReview, TaskKey);
        Assert.Contains(gate, File.ReadAllText(Path.Combine(moved, "orchestrator-follow-up.md")));
        var projection = await http.GetFromJsonAsync<AttemptAuthorityProjection>(
            $"/api/attempts/tasks/{TaskKey}", ApiJson, ct);
        Assert.Equal(AttemptLifecycleState.Failed, projection!.CurrentRunAttempt!.State);
        Assert.Null(projection.CurrentReviewAttempt);
    }

    [Fact]
    public async Task Runner_completion_records_divergent_salvage_refs_and_shas_as_operator_evidence()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote salvage collision", "Continue safely.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);
        Assert.NotNull(lease.Lease);

        const string canonical = "runner/agent-runner-e2e/AGT-RUNNER-E2E";
        const string canonicalSha = "09faf1b709faf1b709faf1b709faf1b709faf1b7";
        const string localSha = "b6f23a3fb6f23a3fb6f23a3fb6f23a3fb6f23a3f";
        const string recovery = canonical + "-collision-" + localSha + "-" + canonicalSha;
        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            lease.Lease.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: ProjectName,
            SalvageBranch: canonical,
            SalvageCommitSha: canonicalSha,
            ResultSha: localSha,
            AttemptChainId: lease.Lease.LeaseId,
            SalvageResolution: "divergent",
            SalvageLocalCommitSha: localSha,
            SalvageRecoveryBranch: recovery,
            SalvageRecoveryCommitSha: localSha,
            SalvageAuthoritativeBaseBranch: canonical,
            SalvageAuthoritativeBaseSha: canonicalSha,
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "salvage-collision-completion"), ct);

        Assert.NotNull(completion);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        var moved = Path.Combine(_watchPath, TaskStates.AutoReview, TaskKey);
        var timeline = File.ReadAllText(Path.Combine(moved, "logs", "timeline.jsonl"));
        Assert.Contains($"\"salvageBranch\":\"{canonical}\"", timeline);
        Assert.Contains($"\"salvageCommitSha\":\"{canonicalSha}\"", timeline);
        Assert.Contains("\"salvageResolution\":\"divergent\"", timeline);
        Assert.Contains($"\"salvageLocalCommitSha\":\"{localSha}\"", timeline);
        Assert.Contains($"\"salvageRecoveryBranch\":\"{recovery}\"", timeline);
        Assert.Contains($"\"salvageAuthoritativeBaseBranch\":\"{canonical}\"", timeline);
        Assert.Contains($"\"salvageAuthoritativeBaseSha\":\"{canonicalSha}\"", timeline);
        var deliverables = File.ReadAllText(Path.Combine(moved, "results", "deliverables.md"));
        Assert.Contains($"`{canonical}` at `{canonicalSha}`", deliverables);
        Assert.Contains($"`{recovery}` at `{localSha}`", deliverables);
        Assert.Contains($"`{canonical}` at `{canonicalSha}` was the authoritative pickup base", deliverables);
    }

    [Fact]
    public async Task Second_runner_is_refused_while_the_lease_is_held()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Contended", "Prompt.");

        using var factory = BuildFactory();
        using var httpA = factory.CreateClient();
        using var httpB = factory.CreateClient();
        using var runnerA = new RClient(httpA, "runner-a");
        using var runnerB = new RClient(httpB, "runner-b");
        var ct = CancellationToken.None;

        await runnerA.RegisterAsync("runner-a remote", "service", ct);
        await runnerB.RegisterAsync("runner-b remote", "service", ct);

        var a = await runnerA.AcquireLeaseAsync(
            new RAcquire(TaskKey, "runner-a", ProjectName, "host-a", 1, "claude"), ct);
        Assert.True(a.Granted);

        // §8.2C: the contender that loses the race is told the task is Held and
        // does not proceed — the split-brain guard the runner branches on.
        var b = await runnerB.AcquireLeaseAsync(
            new RAcquire(TaskKey, "runner-b", ProjectName, "host-b", 2, "claude"), ct);
        Assert.False(b.Granted);
        Assert.Equal("Held", b.Outcome);
    }

    [Fact]
    public async Task Daemon_claim_only_returns_server_assigned_remote_capable_project()
    {
        SeedTask(TaskStates.Ready, TaskKey, "Daemon pickup", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync(ProjectName, "service", CancellationToken.None);

        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = true });
        assignment.EnsureSuccessStatusCode();
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");

        var wrongRunner = await client.ClaimAsync(new RClaim(
            "runner-other", "runner-other", "other-host", 1, "remote-runner"), CancellationToken.None);
        Assert.Equal(RClaimStatus.Empty, wrongRunner.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));

        var request = new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner", Telemetry: new RTelemetry(
                DateTime.UtcNow, 54, 6.4, 6, 5, 34_000_000_000, 64_000_000_000,
                0, 0, 6.2, 2.1, 12, 6),
            AvailableSlots: 20,
            ActiveSlots: 0,
            IdempotencyKey: "daemon-claim-1");
        var claim = await client.ClaimAsync(request, CancellationToken.None);

        Assert.Equal(RClaimStatus.Claimed, claim.Status);
        Assert.False(string.IsNullOrWhiteSpace(claim.TaskKey));
        Assert.Equal(TaskKey, claim.JobId);
        Assert.Equal(ProjectName, claim.ProjectName);
        Assert.NotNull(claim.Lease);
        Assert.Equal("PROJ-001", claim.ProjectId);
        Assert.Equal("https://github.com/agent-orc/agent-studio.git", claim.RepositoryUrl);
        Assert.Equal("develop", claim.DefaultBranch);
        Assert.Equal(TaskKinds.Task, claim.TaskKind);
        Assert.Equal("Prompt.", await client.ReadTaskFileAsync(claim.TaskKey!, "prompt.md", CancellationToken.None));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, TaskKey)));
        var laneTimeline = File.ReadAllText(Path.Combine(
            _watchPath, TaskStates.Progress, TaskKey, "logs", "timeline.jsonl"));
        Assert.Contains($"\"runId\":\"{claim.Lease.AttemptId}\"", laneTimeline, StringComparison.Ordinal);
        Assert.Contains($"\"attemptId\":\"{claim.Lease.AttemptId}\"", laneTimeline, StringComparison.Ordinal);
        Assert.Contains($"\"fence\":\"{claim.Lease.FencingToken}\"", laneTimeline, StringComparison.Ordinal);
        Assert.Contains($"\"authorityEpoch\":\"{claim.Lease.AuthorityEpoch}\"", laneTimeline, StringComparison.Ordinal);
        Assert.Contains("\"idempotencyKey\":\"lane-claim:daemon-claim-1\"", laneTimeline, StringComparison.Ordinal);

        var replay = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.Claimed, replay.Status);
        Assert.Equal(claim.TaskKey, replay.TaskKey);
        Assert.Equal(claim.Lease.LeaseId, replay.Lease!.LeaseId);
        Assert.Equal(claim.Lease.AttemptId, replay.Lease.AttemptId);
        Assert.Equal(claim.Lease.FencingToken, replay.Lease.FencingToken);

        var telemetry = await http.GetFromJsonAsync<HostTelemetryResponse>(
            $"/api/clients/{Uri.EscapeDataString(client.ClientId)}/telemetry?window=1h");
        Assert.NotNull(telemetry);
        Assert.Single(telemetry!.Points);
        Assert.Equal(6.4, telemetry.Points[0].Load1);
        Assert.Equal(6, telemetry.Points[0].ActiveSlots);
        var clients = await http.GetFromJsonAsync<List<ClientSummary>>("/api/clients");
        var runner = Assert.Single(clients!, item => item.Id == client.ClientId);
        Assert.Equal(1, runner.RunnerActiveSlots);
        Assert.Equal(19, runner.RunnerAvailableSlots);
    }

    [Fact]
    public async Task Remote_assigned_ready_epic_completes_planning_with_children_and_no_runner_branch()
    {
        const string epicKey = "AGT-EPIC-REMOTE";
        SeedTask(TaskStates.Ready, epicKey, "Remote Epic", "Split this goal into coding cards.",
            kind: TaskKinds.Epic, cliType: "codex", model: "gpt-5.6-codex");
        var origin = await SeedOriginAsync();
        var runnerWork = Path.Combine(_workspace, "remote-runner-work");
        var cli = Path.Combine(_workspace, "fake-planner.sh");
        await File.WriteAllTextAsync(cli,
            "#!/bin/sh\nprintf '%s\\n' '```json' '{\"subTasks\":[{\"title\":\"Implement API\",\"prompt\":\"Build and test the API.\"},{\"title\":\"Add UI\",\"prompt\":\"Build and test the UI.\"}]}' '```' '[[TASK_DONE]]'\n");
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync(ProjectName, "service", CancellationToken.None);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/example/remote-epic-contract.git");

        var claim = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"), CancellationToken.None);
        Assert.Equal(RClaimStatus.Claimed, claim.Status);
        Assert.Equal(TaskKinds.Epic, claim.TaskKind);

        var options = new ROptions
        {
            ServerUrl = "http://in-process",
            RunnerId = RunnerId,
            RunnerName = ProjectName,
            Hostname = "hetzner-test",
            BackendName = "remote-runner",
            WorkDir = runnerWork,
            StateDir = Path.Combine(runnerWork, ".runner-state"),
            BaseBranch = "main",
            CliBin = cli,
            CliArgs = "",
            TtlSeconds = 120,
            HeartbeatSeconds = 30,
            RunTimeoutSeconds = 30,
            HostMaxParallelism = 1,
            PollSeconds = 1,
        };
        var taskRunner = new RTaskRunner(options, client, _ => { });
        var exit = await taskRunner.RunClaimedAsync(
            claim.TaskKey!, claim.Lease!, CancellationToken.None,
            claim.ProjectId, origin, "main", claim.TaskKind);

        Assert.Equal(0, exit);
        var epicFolder = Path.Combine(_watchPath, TaskStates.AutoReview, epicKey);
        Assert.True(Directory.Exists(epicFolder));
        var children = Directory.EnumerateFiles(_workspace, "task.json", SearchOption.AllDirectories)
            .Where(path =>
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                return json.RootElement.TryGetProperty("epicId", out var epicId)
                       && epicId.GetString() == epicKey;
            })
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .ToList();
        Assert.Equal(2, children.Count);
        foreach (var childFolder in children)
        {
            using var childJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(childFolder, "task.json")));
            Assert.Equal(epicKey, childJson.RootElement.GetProperty("epicId").GetString());
            Assert.Equal("codex", childJson.RootElement.GetProperty("cliType").GetString());
            Assert.Equal("gpt-5.6-codex", childJson.RootElement.GetProperty("model").GetString());
        }
        Assert.Equal(2, File.ReadAllLines(Path.Combine(epicFolder, ".metadata", "spawned-tasks.jsonl")).Length);
        var runnerBranch = await GitAsync(origin,
            ["show-ref", "--verify", "--quiet", $"refs/heads/runner/{RunnerId}/{epicKey}"],
            allowFailure: true);
        Assert.NotEqual(0, runnerBranch.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(runnerWork, "PROJ-001", "worktrees", epicKey)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a decomposition plan")]
    public async Task Remote_epic_empty_or_invalid_plan_returns_to_backlog(string output)
    {
        const string epicKey = "AGT-EPIC-INVALID";
        SeedTask(TaskStates.Ready, epicKey, "Invalid Epic", "Plan it.", kind: TaskKinds.Epic);
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync(ProjectName, "service", CancellationToken.None);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");
        var claim = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "host", 1, "remote-runner"), CancellationToken.None);

        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            claim.TaskKey!, claim.Lease!.LeaseId, claim.Lease.FencingToken, RunnerId,
            "Done", Source: ProjectName,
            OutputLines: string.IsNullOrEmpty(output) ? [] : [output],
            AttemptId: claim.Lease.AttemptId,
            AuthorityEpoch: claim.Lease.AuthorityEpoch,
            IdempotencyKey: $"epic-invalid:{epicKey}:{output}"), CancellationToken.None);

        Assert.Equal(TaskStates.Backlog, completion!.TargetState);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Backlog, epicKey)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_watchPath, TaskStates.AutoReview)));
    }

    [Fact]
    public async Task Remote_epic_source_mutation_invalidates_an_otherwise_valid_plan()
    {
        const string epicKey = "AGT-EPIC-MUTATED";
        SeedTask(TaskStates.Ready, epicKey, "Mutating Epic", "Plan without editing source.",
            kind: TaskKinds.Epic);
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync(ProjectName, "service", CancellationToken.None);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");
        var claim = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "host", 1, "remote-runner"), CancellationToken.None);

        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            claim.TaskKey!, claim.Lease!.LeaseId, claim.Lease.FencingToken, RunnerId,
            "Done", Source: ProjectName,
            OutputLines: ["{\"subTasks\":[{\"title\":\"Must not exist\",\"prompt\":\"No.\"}]}"],
            SourceMutated: true,
            AttemptId: claim.Lease.AttemptId,
            AuthorityEpoch: claim.Lease.AuthorityEpoch,
            IdempotencyKey: $"epic-mutated:{epicKey}"), CancellationToken.None);

        Assert.Equal(TaskStates.Backlog, completion!.TargetState);
        Assert.Contains("read-only checkout", completion.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Backlog, epicKey)));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_workspace, "task.json", SearchOption.AllDirectories),
            path => JsonDocument.Parse(File.ReadAllText(path)).RootElement
                .TryGetProperty("epicId", out var epicId) && epicId.GetString() == epicKey);
    }

    [Theory]
    [InlineData(TaskKinds.Task)]
    [InlineData(TaskKinds.Epic)]
    public async Task Remote_claim_recovers_progress_card_after_lease_is_released(string kind)
    {
        SeedTask(TaskStates.Ready, TaskKey, "Restart recovery", "Prompt.", kind: kind);
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync(ProjectName, "service", CancellationToken.None);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");
        var first = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "host", 1, "remote-runner"), CancellationToken.None);
        await client.ReleaseLeaseAsync(new RRelease(
            first.TaskKey!, first.Lease!.LeaseId, first.Lease.FencingToken, RunnerId,
            first.Lease.AttemptId, first.Lease.AuthorityEpoch,
            $"release:{first.Lease.AttemptId}"), CancellationToken.None);

        var recovered = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "host", 2, "remote-runner"), CancellationToken.None);

        Assert.Equal(RClaimStatus.Claimed, recovered.Status);
        Assert.Equal(TaskKey, recovered.JobId);
        Assert.Equal(kind, recovered.TaskKind);
        Assert.True(recovered.Lease!.FencingToken > first.Lease.FencingToken);
        var projection = await http.GetFromJsonAsync<AttemptAuthorityProjection>(
            $"/api/attempts/tasks/{first.TaskKey}", ApiJson, CancellationToken.None);
        Assert.Equal(first.Lease.AttemptId, projection!.CurrentRunAttempt!.SourceAttemptId);
    }

    [Fact]
    public async Task Daemon_claim_is_refused_until_the_runner_reports_push_ready()
    {
        SeedTask(TaskStates.Ready, TaskKey, "Push-gated pickup", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var clientId = await client.RegisterAsync(ProjectName, "service", CancellationToken.None);

        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = true });
        assignment.EnsureSuccessStatusCode();
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");

        await client.ReportGitCapabilityAsync(clientId, new RGitCapability(
            "read-only", "push-dry-run failed (128): permission denied", DateTime.UtcNow), CancellationToken.None);

        var refused = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"), CancellationToken.None);

        Assert.Equal(RClaimStatus.Empty, refused.Status);
        Assert.Contains("read-only", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));

        await client.ReportGitCapabilityAsync(clientId, new RGitCapability(
            "ready", "dry-run succeeded", DateTime.UtcNow), CancellationToken.None);

        var admitted = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"), CancellationToken.None);

        Assert.Equal(RClaimStatus.Claimed, admitted.Status);
        Assert.Equal(TaskKey, admitted.JobId);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, TaskKey)));
    }

    [Fact]
    public async Task Daemon_claim_skips_assigned_project_without_repository_url()
    {
        SeedTask(TaskStates.Ready, TaskKey, "No remote repository", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync(ProjectName, "service", CancellationToken.None);

        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = true });
        assignment.EnsureSuccessStatusCode();

        var claim = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"), CancellationToken.None);

        Assert.Equal(RClaimStatus.Empty, claim.Status);
        Assert.Null(claim.RepositoryUrl);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));
    }

    [Fact]
    public async Task Daemon_claim_skips_project_that_opts_out_of_remote_execution()
    {
        SeedTask(TaskStates.Ready, TaskKey, "Machine-bound", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync(ProjectName, "service", CancellationToken.None);

        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = false });
        assignment.EnsureSuccessStatusCode();

        var claim = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"), CancellationToken.None);

        Assert.Equal(RClaimStatus.Empty, claim.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));
    }

    private WebApplicationFactory<Program> BuildFactory(
        IAtomicJsonFileWriter? writer = null) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                        ["WatchPaths:0:RepositoryPath"] = _watchPath,
                        ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    });
                });
                if (writer is not null)
                {
                    b.ConfigureTestServices(services =>
                        services.AddSingleton<IAtomicJsonFileWriter>(writer));
                }
            });

    private static JsonSerializerOptions CreateApiJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static async Task AddRepositoryUrlAsync(HttpClient http, string repositoryUrl)
    {
        var response = await http.PostAsJsonAsync(
            "/api/projects/PROJ-001/urls",
            new { label = "repo", url = repositoryUrl });
        response.EnsureSuccessStatusCode();
    }

    private static async Task AssignRemoteAsync(HttpClient http)
    {
        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = true });
        assignment.EnsureSuccessStatusCode();
    }

    private void SeedTask(
        string state, string key, string title, string promptBody,
        string kind = TaskKinds.Task, string? cliType = null, string? model = null)
    {
        var dir = Path.Combine(_watchPath, state, key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), JsonSerializer.Serialize(new
        {
            id = key, title, state, order = 1, agent = cliType ?? "claude", kind, cliType, model,
        }));
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        File.WriteAllText(Path.Combine(dir, "status.md"), "Result: pending.");
    }

    private async Task<string> SeedOriginAsync()
    {
        var origin = Path.Combine(_workspace, "origin.git");
        var seed = Path.Combine(_workspace, "origin-seed");
        await GitAsync(_workspace, "init", "--bare", origin);
        await GitAsync(_workspace, "init", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed");
        await GitAsync(seed, "add", "--all");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "seed");
        await GitAsync(seed, "branch", "-M", "main");
        await GitAsync(seed, "remote", "add", "origin", origin);
        await GitAsync(seed, "push", "-u", "origin", "main");
        return origin;
}

    private static async Task<LocalGitResult> GitAsync(
        string cwd, params string[] args)
        => await GitAsync(cwd, args, allowFailure: false);

    private static async Task<LocalGitResult> GitAsync(
        string cwd, string[] args, bool allowFailure)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (!allowFailure && process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return new LocalGitResult(process.ExitCode, stdout, stderr);
    }
}

internal sealed record LocalGitResult(int ExitCode, string StdOut, string StdErr);
