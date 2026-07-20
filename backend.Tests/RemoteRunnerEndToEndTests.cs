extern alias Runner;

using System.Text;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
        var renew = await client.RenewLeaseAsync(new RHeartbeat(TaskKey, leaseId, token, RunnerId), ct);
        Assert.True(renew.Granted, $"renew not granted: {renew.Outcome} {renew.Message}");

        // 3. Ship CLI output — appended to the task's durable logs/cli-output.log.
        var logResp = await client.IngestLogsAsync(new RLogIngest(TaskKey,
        [
            new RCliLine(DateTime.UtcNow, "stdout", "remote runner: starting task"),
            new RCliLine(DateTime.UtcNow, "stdout", "remote runner: [[TASK_DONE]]"),
        ]), ct);
        Assert.NotNull(logResp);
        Assert.Equal(2, logResp!.Appended);

        // 4. Upload evidence — decoded under the task's results/ folder.
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("evidence bytes"));
        var artResp = await client.UploadArtifactsAsync(new RArtifactIngest(TaskKey,
        [
            new RArtifact("runner-evidence--real.txt", content),
        ]), ct);
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
        var release = await client.ReleaseLeaseAsync(new RRelease(TaskKey, leaseId, token, RunnerId), ct);
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
        ]), ct);

        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            lease.Lease!.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: ProjectName,
            ExitCode: 0,
            ResultSha: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            AttemptChainId: lease.Lease.LeaseId,
            Repository: "https://example.invalid/agent-studio.git"), ct);

        Assert.NotNull(completion);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        var moved = Path.Combine(_watchPath, TaskStates.AutoReview, TaskKey);
        Assert.True(Directory.Exists(moved));

        var taskJson = File.ReadAllText(Path.Combine(moved, "task.json"));
        Assert.DoesNotContain("externalCompletion", taskJson, StringComparison.OrdinalIgnoreCase);
        var timeline = File.ReadAllText(Path.Combine(moved, "logs", "timeline.jsonl"));
        Assert.Contains("agent_run_finished", timeline);
        Assert.Contains("resultSha", timeline);
        Assert.DoesNotContain("external_completion", timeline);
        var subject = ReviewSubjectStore.Read(moved);
        Assert.NotNull(subject);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", subject!.ResultSha);
        Assert.Equal(lease.Lease.LeaseId, subject.AttemptChainId);
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
                ExitCode: 0), ct));

        Assert.Equal(400, error.StatusCode);
        var retainedTask = Assert.Single(Directory.GetDirectories(
            _watchPath, TaskKey, SearchOption.AllDirectories));
        Assert.NotEqual(TaskStates.AutoReview, Directory.GetParent(retainedTask)!.Name);
        Assert.False(File.Exists(ReviewSubjectStore.PathFor(retainedTask)));
    }

    [Fact]
    public async Task July_gate_storm_replay_uses_three_real_remote_subjects_without_false_reissues()
    {
        var taskKeys = new[] { "AGT-STORM-A", "AGT-STORM-B", "AGT-STORM-C" };
        foreach (var taskKey in taskKeys)
        {
            SeedTask(TaskStates.Progress, taskKey, taskKey, "Verify exact remote subject.");
            File.WriteAllText(
                Path.Combine(_watchPath, TaskStates.Progress, taskKey, "status.md"),
                "## Summary\nRemote work completed.\n\nResult: Success\n\n## Open Items\nNone\n");
        }
        InitializeGitRepository();
        var resultSha = RunGit("rev-parse", "HEAD").Trim();

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync(ProjectName, "service", CancellationToken.None);
        var attemptChains = new Dictionary<string, string>();
        foreach (var taskKey in taskKeys)
        {
            var lease = await client.AcquireLeaseAsync(new RAcquire(
                taskKey, RunnerId, ProjectName, "storm-host", 4242, "codex"), CancellationToken.None);
            Assert.True(lease.Granted);
            await client.IngestLogsAsync(new RLogIngest(taskKey,
            [
                new RCliLine(DateTime.UtcNow, "stdout", "Remote implementation verified."),
                new RCliLine(DateTime.UtcNow, "stdout", "[[TASK_DONE]]"),
            ]), CancellationToken.None);
            var completion = await client.CompleteRunAsync(new RRemoteComplete(
                taskKey,
                lease.Lease!.LeaseId,
                lease.Lease.FencingToken,
                RunnerId,
                "Done",
                Source: "storm-remote",
                ExitCode: 0,
                ResultSha: resultSha,
                AttemptChainId: lease.Lease.LeaseId,
                Repository: _watchPath), CancellationToken.None);
            Assert.NotNull(completion);
            Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
            attemptChains[taskKey] = lease.Lease.LeaseId;
        }

        var sharedMarker = Path.Combine(_workspace, "gate-storm-active.tmp");
        var marker = sharedMarker.Replace("'", "'\\''", StringComparison.Ordinal);
        var command = OperatingSystem.IsWindows()
            ? $"if exist \"{sharedMarker}\" exit /b 91 & type nul > \"{sharedMarker}\" & ping 127.0.0.1 -n 2 > nul & del \"{sharedMarker}\""
            : $"test ! -e '{marker}'; touch '{marker}'; sleep 1; rm '{marker}'";
        var orchestrator = BuildReviewOrchestrator(
            new BuildProfile { BuildCmds = [command] });

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var workspaces = new HashSet<string>(StringComparer.Ordinal);
        var collisions = 0;
        foreach (var taskKey in taskKeys)
        {
            var folder = Path.Combine(_watchPath, TaskStates.HumanReview, taskKey);
            Assert.True(Directory.Exists(folder), $"{taskKey} did not reach human review");
            var subject = ReviewSubjectStore.Read(folder);
            Assert.NotNull(subject);
            Assert.Equal(resultSha, subject!.ResultSha);
            Assert.Equal(attemptChains[taskKey], subject.AttemptChainId);
            var gateLog = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(folder, "post-steps"), "build-test-gate-*.log"));
            var text = File.ReadAllText(gateLog);
            Assert.Contains($"expectedSha={resultSha} testedSha={resultSha}", text);
            Assert.Contains($"attemptChainId={attemptChains[taskKey]}", text);
            var workspaceLine = text.Split('\n').Single(line => line.StartsWith("attemptChainId=", StringComparison.Ordinal));
            workspaces.Add(workspaceLine[(workspaceLine.IndexOf(" workspace=", StringComparison.Ordinal) + 11)..].Trim());
            if (text.Contains("collision=True", StringComparison.Ordinal)) collisions++;
        }

        Assert.Equal(3, workspaces.Count);
        Assert.True(collisions >= 2, $"expected at least two queued gates, observed {collisions}");
        Assert.False(File.Exists(sharedMarker));
        Assert.DoesNotContain(ReviewDecisionLog.ReadAll(_workspace, ProjectName),
            decision => decision.Kind == ReviewDecisionKind.Reissue
                        || decision.Kind == ReviewDecisionKind.Escalate);
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
            lease.Lease!.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: ProjectName,
            SalvageBranch: canonical,
            SalvageCommitSha: canonicalSha,
            SalvageResolution: "divergent",
            SalvageLocalCommitSha: localSha,
            SalvageRecoveryBranch: recovery,
            SalvageRecoveryCommitSha: localSha,
            SalvageAuthoritativeBaseBranch: canonical,
            SalvageAuthoritativeBaseSha: canonicalSha), ct);

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
                        ["WatchPaths:0:RepositoryPath"] = _watchPath,
                        ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    });
                });
            });

    private ReviewDecisionOrchestrator BuildReviewOrchestrator(BuildProfile profile)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _watchPath,
            ["WatchPaths:0:RepositoryPath"] = _watchPath,
            ["ReviewDecisionOrchestrator:Enabled"] = "true",
            ["ReviewDecisionOrchestrator:CallsPerHour"] = "100",
            ["ReviewDecisionOrchestrator:AspectsEnabled"] = "true",
            ["ReviewDecisionOrchestrator:MaxParallelReviews"] = "4",
            ["ReviewDecisionOrchestrator:MaxAutoReissueAttempts"] = "3",
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance)
        {
            CliRunner = (_, _, _, _, _, _) =>
                Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"),
        };
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetBuildProfile(ProjectName, profile);
        var transitions = new TaskTransitionService(
            scanner, stateMachine, mutations, git, settings,
            NullLogger<TaskTransitionService>.Instance);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, stateMachine, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        return new ReviewDecisionOrchestrator(
            scanner, stateMachine, taskAccess, chatLog, prompts, aspects,
            new AutoReviewStatusSnapshot(), config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            git: git,
            pipelineLog: new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            buildTestGateRunner: new BuildTestGateRunner(NullLogger<BuildTestGateRunner>.Instance),
            projectSettings: settings);
    }

    private void InitializeGitRepository()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "test@example.invalid");
        RunGit("config", "user.name", "Remote Replay");
        File.WriteAllText(Path.Combine(_watchPath, "remote-subject.txt"), "remote exact subject");
        RunGit("add", ".");
        RunGit("commit", "-q", "-m", "remote exact subject");
    }

    private string RunGit(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = _watchPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("git did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
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
