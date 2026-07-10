extern alias Runner;

using System.Text;
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
using RClaimStatus = Runner::AgentRunner.RunnerClaimStatus;
using RLogIngest = Runner::AgentRunner.LogIngestRequest;
using RCliLine = Runner::AgentRunner.CliOutputLine;
using RArtifactIngest = Runner::AgentRunner.ArtifactIngestRequest;
using RArtifact = Runner::AgentRunner.RunnerArtifactUpload;
using RComplete = Runner::AgentRunner.ExternalCompletionRequest;
using RDeliverable = Runner::AgentRunner.ExternalDeliverable;

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

        var wrongRunner = await client.ClaimAsync(new RClaim(
            "runner-other", "runner-other", "other-host", 1, "remote-runner"), CancellationToken.None);
        Assert.Equal(RClaimStatus.Empty, wrongRunner.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));

        var claim = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"), CancellationToken.None);

        Assert.Equal(RClaimStatus.Claimed, claim.Status);
        Assert.False(string.IsNullOrWhiteSpace(claim.TaskKey));
        Assert.Equal(TaskKey, claim.JobId);
        Assert.Equal(ProjectName, claim.ProjectName);
        Assert.NotNull(claim.Lease);
        Assert.Equal("Prompt.", await client.ReadTaskFileAsync(claim.TaskKey!, "prompt.md", CancellationToken.None));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, TaskKey)));
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
