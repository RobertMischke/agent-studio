using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteReviewWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "remote-review-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;
    private readonly string _reviewRoot;

    public RemoteReviewWorkspaceTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        _reviewRoot = Path.Combine(_root, "review");
    }

    [Fact]
    public async Task Exact_sha_workspace_records_head_tree_commands_environment_and_clean_after()
    {
        var sha = await SeedOriginAsync();
        var (workspace, _) = Workspace(
            "attempt-a", sha,
            [new ReviewCommandDto("verify", "build-tests", "git", ["rev-parse", "HEAD"])],
            24000);

        var prepared = await workspace.PrepareAsync(null!, default);
        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal(sha, prepared.ActualHead);
        Assert.False(prepared.DirtyBefore);
        Assert.Equal(sha, Assert.Single(evidence.Commands).HeadBefore);
        Assert.Equal("Pass", evidence.Outcome);
        Assert.False(evidence.Workspace.DirtyAfter);
        Assert.Equal("review-attempt-a-f1", evidence.Workspace.ResourceNamespace);
        Assert.Equal("24000", workspace.ProcessEnvironment()["PORT"]);
        Assert.StartsWith(workspace.AttemptRoot, workspace.ProcessEnvironment()["XDG_CACHE_HOME"]);
        var environment = workspace.EnvironmentEvidence();
        Assert.Contains("sha256=", environment.Toolchain["git"], StringComparison.Ordinal);
        Assert.Contains("sha256=", environment.Toolchain["command:verify"], StringComparison.Ordinal);
        Assert.True(await workspace.CleanupAsync());
        Assert.False(Directory.Exists(workspace.AttemptRoot));
    }

    [Fact]
    public async Task Wrong_sha_fails_before_any_review_command()
    {
        await SeedOriginAsync();
        var wrong = new string('a', 40);
        var (workspace, _) = Workspace(
            "attempt-wrong", wrong,
            [new ReviewCommandDto("must-not-run", "requirements", "git", ["status"])],
            24008);

        var error = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => workspace.PrepareAsync(null!, default));

        Assert.Equal("ShaMismatch", error.Classification);
        Assert.Empty(Directory.Exists(workspace.ArtifactPath)
            ? Directory.EnumerateFiles(workspace.ArtifactPath)
            : []);
    }

    [Fact]
    public async Task Review_mutation_is_detected_even_when_the_command_exits_zero()
    {
        var sha = await SeedOriginAsync();
        var (workspace, _) = Workspace(
            "attempt-mutates", sha,
            [new ReviewCommandDto(
                "mutation", "code-quality", "/bin/sh", ["-c", "printf mutation > unexpected.txt"])],
            24016);
        await workspace.PrepareAsync(null!, default);

        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.True(evidence.Workspace.DirtyAfter);
        Assert.Equal("Pass", evidence.Outcome);
    }

    [Fact]
    public async Task Three_parallel_reviews_use_distinct_workspaces_caches_ports_and_outputs()
    {
        var sha = await SeedOriginAsync();
        var items = Enumerable.Range(0, 3)
            .Select(index => Workspace(
                $"attempt-{index}", sha,
                [new ReviewCommandDto($"step-{index}", "artifacts", "git", ["status", "--short"])],
                25000 + index * 8))
            .ToArray();

        await Task.WhenAll(items.Select(item => item.Workspace.PrepareAsync(null!, default)));
        var evidence = await Task.WhenAll(items.Select(item => item.Workspace.ExecutePlanAsync(default)));

        Assert.Equal(3, items.Select(item => item.Workspace.AttemptRoot).Distinct().Count());
        Assert.Equal(3, items.Select(item => item.Workspace.CachePath).Distinct().Count());
        Assert.Equal(3, items.Select(item => item.Workspace.ProcessEnvironment()["PORT"]).Distinct().Count());
        Assert.Equal(6, evidence.SelectMany(item => item.Artifacts)
            .Select(item => item.Name).Distinct().Count());
        Assert.All(evidence, item => Assert.False(item.Workspace.DirtyAfter));
    }

    [Fact]
    public void Restart_takeover_of_one_attempt_uses_a_new_fenced_workspace()
    {
        var sha = new string('a', 40);
        var first = Workspace("attempt-restart", sha, [], 25100, fence: 1).Workspace;
        var takeover = Workspace("attempt-restart", sha, [], 25108, fence: 2).Workspace;

        Assert.NotEqual(first.AttemptRoot, takeover.AttemptRoot);
        Assert.NotEqual(
            first.ProcessEnvironment()["AGENT_REVIEW_NAMESPACE"],
            takeover.ProcessEnvironment()["AGENT_REVIEW_NAMESPACE"]);
    }

    [Fact]
    public async Task Review_child_process_does_not_inherit_unapproved_coding_credentials()
    {
        var sha = await SeedOriginAsync();
        const string variable = "AGENT_TEST_WRITE_CREDENTIAL";
        Environment.SetEnvironmentVariable(variable, "must-not-leak");
        try
        {
            var (workspace, _) = Workspace(
                "attempt-credentials", sha,
                [new ReviewCommandDto(
                    "credential-check", "evidence", "/bin/sh",
                    ["-c", $"test -z \"${{{variable}:-}}\""])],
                25032);
            await workspace.PrepareAsync(null!, default);

            var evidence = await workspace.ExecutePlanAsync(default);

            Assert.Equal("Pass", evidence.Outcome);
            Assert.Equal(0, Assert.Single(evidence.Commands).ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private (RemoteReviewWorkspace Workspace, ReviewSubjectDto Subject) Workspace(
        string attemptId,
        string sha,
        IReadOnlyList<ReviewCommandDto> commands,
        int portBase,
        long fence = 1)
    {
        var repositoryId = TaskServerClient.RepositoryIdentity(_origin)!;
        var subject = new ReviewSubjectDto(
            "subject-" + attemptId,
            "task-" + attemptId,
            "run-" + attemptId,
            repositoryId,
            _origin,
            sha,
            "refs/heads/main",
            null,
            null,
            "coding-host",
            "policy",
            new ReviewPlanDto(commands, commands.Select(command => command.Aspect).ToArray()),
            DateTime.UtcNow);
        var lease = new ReviewLeaseDto(
            "lease-" + attemptId,
            attemptId,
            subject.SubjectId,
            "review-executor",
            "review-instance",
            "review-host",
            fence,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(2),
            "active",
            $"review-{attemptId}-f{fence}",
            portBase);
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "review-executor",
            RunnerName = "review-executor",
            Hostname = "review-host",
            BackendName = "remote-review",
            Role = "review",
            WorkDir = Path.Combine(_root, "coding"),
            ReviewWorkDir = _reviewRoot,
            BaseBranch = "main",
            CliBin = "unused",
            CliArgs = "",
            TtlSeconds = 120,
            HeartbeatSeconds = 30,
        };
        return (new RemoteReviewWorkspace(options, subject, lease, _ => { }), subject);
    }

    private async Task<string> SeedOriginAsync()
    {
        Directory.CreateDirectory(_root);
        if (Directory.Exists(_origin))
            return (await GitAsync(_origin, "rev-parse", "refs/heads/main")).StdOut.Trim();
        var seed = Path.Combine(_root, "seed");
        await GitAsync(_root, "init", "--bare", _origin);
        await GitAsync(_root, "init", "-b", "main", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "product.txt"), "immutable product");
        await GitAsync(seed, "add", "product.txt");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "result");
        await GitAsync(seed, "remote", "add", "origin", _origin);
        await GitAsync(seed, "push", "origin", "main");
        return (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
    }

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory);
        Assert.True(result.Success, result.StdErr);
        return result;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
