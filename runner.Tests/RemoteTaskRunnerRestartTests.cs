using System.Net;
using System.Text;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteTaskRunnerRestartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-runner-reattach", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Restarted_runner_follows_fake_job_and_delivers_completion_without_a_zombie_lease()
    {
        var origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        await CreateOriginAsync(origin, seed);
        var work = Path.Combine(_root, "runner-work");
        var stateRoot = Path.Combine(_root, "state");
        var options = Options(work, stateRoot, origin);
        var lease = Lease();
        var workspace = new GitWorkspace(options, lease.TaskKey, _ => { });
        await workspace.PrepareAsync(CancellationToken.None);
        var results = Path.Combine(work, "tasks", GitWorkspace.SafeSegment(lease.TaskKey), "results");
        Directory.CreateDirectory(results);

        var originalStore = new RunnerStateStore(stateRoot);
        var slot = originalStore.Create(lease.TaskKey, lease, workspace.RepoPath);
        var process = DurableAgentProcess.Start(
            options, slot.WorkerDirectory, workspace.RepoPath, "", results);
        originalStore.Save(slot with
        {
            ProcessId = process.ProcessId,
            ProcessStartedAtUtc = process.ProcessStartedAtUtc,
            Phase = "running",
        });

        var replacementStore = new RunnerStateStore(stateRoot);
        var recovered = Assert.Single(replacementStore.LoadAll());
        Assert.True(DurableAgentProcess.VerifyLive(recovered, out var proof), proof);
        var server = new RunnerApiHandler(lease);
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, options.RunnerId);
        var replacement = new RemoteTaskRunner(options, client, _ => { }, replacementStore);

        var exitCode = await replacement.ReattachAsync(recovered, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("/api/runner/lease/renew", server.Paths);
        Assert.Contains("/api/runner/logs", server.Paths);
        Assert.Contains("/api/runner/completion", server.Paths);
        Assert.Contains("/api/runner/lease/release", server.Paths);
        Assert.Contains("[[TASK_DONE]]", server.LogBodies.ToString());
        Assert.Empty(replacementStore.LoadAll());
        Assert.False(Directory.Exists(workspace.RepoPath));
    }

    [Fact]
    public async Task Restarted_runner_releases_a_dead_job_instead_of_adopting_its_lease()
    {
        var work = Path.Combine(_root, "runner-work");
        var stateRoot = Path.Combine(_root, "state");
        var worktree = Path.Combine(work, "dead-worktree");
        Directory.CreateDirectory(worktree);
        var options = Options(work, stateRoot, Path.Combine(_root, "unused-origin.git"));
        var lease = Lease();
        var originalStore = new RunnerStateStore(stateRoot);
        originalStore.Create(lease.TaskKey, lease, worktree);

        var replacementStore = new RunnerStateStore(stateRoot);
        var recovered = Assert.Single(replacementStore.LoadAll());
        Assert.False(DurableAgentProcess.VerifyLive(recovered, out var reason));
        Assert.Contains("no persisted process identity", reason);

        var server = new RunnerApiHandler(lease);
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, options.RunnerId);
        var replacement = new RemoteTaskRunner(options, client, _ => { }, replacementStore);

        var released = await replacement.ReleaseDeadAsync(recovered, reason);

        Assert.True(released);
        Assert.Equal(["/api/runner/lease/release"], server.Paths);
        Assert.Empty(replacementStore.LoadAll());
    }

    [Fact]
    public async Task Restarted_runner_accepts_an_expired_dead_job_lease_as_already_released()
    {
        var work = Path.Combine(_root, "runner-work");
        var stateRoot = Path.Combine(_root, "state");
        var worktree = Path.Combine(work, "expired-dead-worktree");
        Directory.CreateDirectory(worktree);
        var options = Options(work, stateRoot, Path.Combine(_root, "unused-origin.git"));
        var lease = Lease();
        var originalStore = new RunnerStateStore(stateRoot);
        originalStore.Create(lease.TaskKey, lease, worktree);

        var replacementStore = new RunnerStateStore(stateRoot);
        var recovered = Assert.Single(replacementStore.LoadAll());
        var server = new RunnerApiHandler(lease, releaseOutcome: "Expired");
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, options.RunnerId);
        var replacement = new RemoteTaskRunner(options, client, _ => { }, replacementStore);

        var released = await replacement.ReleaseDeadAsync(recovered, "lease expired during daemon downtime");

        Assert.True(released);
        Assert.Equal(["/api/runner/lease/release"], server.Paths);
        Assert.Empty(replacementStore.LoadAll());
    }

    private static RunnerOptions Options(string work, string stateRoot, string origin) => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "runner-restart-test",
        RunnerName = "runner-restart-test",
        Hostname = "test-host",
        BackendName = "test",
        GitRemote = origin,
        GitPushRemote = origin,
        WorkDir = work,
        StateDir = stateRoot,
        BaseBranch = "main",
        CliBin = "/bin/sh",
        CliArgs = "-c \"sleep 1; printf 'reattached-output\\n[[TASK_DONE]]\\n'\"",
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        RunTimeoutSeconds = 10,
        HostMaxParallelism = 1,
        PollSeconds = 1,
    };

    private static RunLeaseInfoDto Lease() => new(
        "AGT-REATTACH",
        "runner-restart-test",
        "runner-restart-test",
        "test-host",
        Environment.ProcessId,
        "test",
        "lease-reattach",
        7,
        DateTime.UtcNow,
        DateTime.UtcNow.AddMinutes(2));

    private static async Task CreateOriginAsync(string origin, string seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(origin)!);
        await GitAsync(_root: Path.GetDirectoryName(origin)!, "init", "--bare", origin);
        await GitAsync(_root: Path.GetDirectoryName(seed)!, "init", seed);
        await GitAsync(seed, "config", "user.name", "Runner Test");
        await GitAsync(seed, "config", "user.email", "runner@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed\n");
        await GitAsync(seed, "add", "README.md");
        await GitAsync(seed, "commit", "-m", "seed");
        await GitAsync(seed, "branch", "-M", "main");
        await GitAsync(seed, "remote", "add", "origin", origin);
        await GitAsync(seed, "push", "-u", "origin", "main");
        await GitAsync(origin, "symbolic-ref", "HEAD", "refs/heads/main");
    }

    private static async Task GitAsync(string _root, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, _root, ct: CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)} failed: {result.StdErr}");
    }

    private sealed class RunnerApiHandler(
        RunLeaseInfoDto lease,
        string releaseOutcome = "Released") : HttpMessageHandler
    {
        private readonly object _gate = new();
        public List<string> Paths { get; } = [];
        public StringBuilder LogBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                Paths.Add(path);
                if (path == "/api/runner/logs") LogBodies.Append(body);
            }

            var json = path switch
            {
                "/api/runner/lease/renew" => $$"""
                    {"outcome":"Renewed","granted":true,"lease":{{LeaseJson(lease)}}}
                    """,
                "/api/runner/logs" => $$"""
                    {"taskKey":"{{lease.TaskKey}}","appended":2}
                    """,
                "/api/runner/completion" => $$"""
                    {"taskKey":"{{lease.TaskKey}}","outcome":"Done","targetState":"4-auto-review"}
                    """,
                "/api/runner/lease/release" => $$"""
                    {"outcome":"{{releaseOutcome}}","granted":false,"lease":{{LeaseJson(lease)}}}
                    """,
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        private static string LeaseJson(RunLeaseInfoDto value)
            => System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}
