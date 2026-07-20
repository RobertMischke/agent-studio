extern alias Runner;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
using RRemoteCompletionResponse = Runner::AgentRunner.RemoteRunCompletionResponse;

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
        ], lease.Lease.AttemptId, token, lease.Lease.AuthorityEpoch, "e2e-logs-1"), ct);
        Assert.NotNull(logResp);
        Assert.Equal(2, logResp!.Appended);

        // 4. Upload evidence — decoded under the task's results/ folder.
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("evidence bytes"));
        var artResp = await client.UploadArtifactsAsync(new RArtifactIngest(TaskKey,
        [
            new RArtifact("runner-evidence--real.txt", content),
        ], lease.Lease.AttemptId, token, lease.Lease.AuthorityEpoch, "e2e-artifacts-1"), ct);
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
        ], lease.Lease!.AttemptId, lease.Lease.FencingToken, lease.Lease.AuthorityEpoch, "remote-done-logs"), ct);

        var completionRequest = new RRemoteComplete(
            TaskKey,
            lease.Lease!.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: ProjectName,
            ExitCode: 0,
            ResultSha: "589c462f",
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
                IdempotencyKey = "remote-done-wrong-lease",
            }, ct);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, wrongLease.StatusCode);
        var wrongLeaseResult = await wrongLease.Content.ReadFromJsonAsync<RRemoteCompletionResponse>(ApiJson, ct);
        Assert.Equal(AttemptWriteStatus.StaleFence.ToString(), wrongLeaseResult!.FailureClassification);

        var completion = await client.CompleteRunAsync(completionRequest, ct);

        Assert.NotNull(completion);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        Assert.Equal(lease.Lease.AttemptId, completion.RunAttemptId);
        Assert.False(string.IsNullOrWhiteSpace(completion.ReviewAttemptId));
        Assert.False(string.IsNullOrWhiteSpace(completion.ReviewSubjectId));

        var projection = await http.GetFromJsonAsync<AttemptAuthorityProjection>(
            $"/api/attempts/tasks/{TaskKey}", ApiJson, ct);
        Assert.Equal("589c462f", projection!.CurrentRunAttempt!.ResultSha);
        Assert.Equal("589c462f", projection.CurrentReviewSubject!.ExpectedResultSha);

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
        var timeline = File.ReadAllText(Path.Combine(moved, "logs", "timeline.jsonl"));
        Assert.Contains("agent_run_finished", timeline);
        Assert.Contains("idempotencyKey", timeline);
        Assert.DoesNotContain("external_completion", timeline);
        Assert.Equal(1, timeline.Split("agent_run_finished", StringSplitOptions.None).Length - 1);
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

        var claim = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner", Telemetry: new RTelemetry(
                DateTime.UtcNow, 54, 6.4, 6, 5, 34_000_000_000, 64_000_000_000,
                0, 0, 6.2, 2.1, 12, 6)), CancellationToken.None);

        Assert.Equal(RClaimStatus.Claimed, claim.Status);
        Assert.False(string.IsNullOrWhiteSpace(claim.TaskKey));
        Assert.Equal(TaskKey, claim.JobId);
        Assert.Equal(ProjectName, claim.ProjectName);
        Assert.NotNull(claim.Lease);
        Assert.Equal("PROJ-001", claim.ProjectId);
        Assert.Equal("https://github.com/agent-orc/agent-studio.git", claim.RepositoryUrl);
        Assert.Equal("develop", claim.DefaultBranch);
        Assert.Equal("Prompt.", await client.ReadTaskFileAsync(claim.TaskKey!, "prompt.md", CancellationToken.None));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, TaskKey)));
        var telemetry = await http.GetFromJsonAsync<HostTelemetryResponse>(
            $"/api/clients/{Uri.EscapeDataString(client.ClientId)}/telemetry?window=1h");
        Assert.NotNull(telemetry);
        Assert.Single(telemetry!.Points);
        Assert.Equal(6.4, telemetry.Points[0].Load1);
        Assert.Equal(6, telemetry.Points[0].ActiveSlots);
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

    private WebApplicationFactory<Program> BuildFactory() =>
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
                    });
                });
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

    private void SeedTask(string state, string key, string title, string promptBody)
    {
        var dir = Path.Combine(_watchPath, state, key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{key}\",\"title\":\"{title}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        File.WriteAllText(Path.Combine(dir, "status.md"), "Result: pending.");
    }
}
