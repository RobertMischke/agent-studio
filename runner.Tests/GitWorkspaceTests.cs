using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class GitWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-runner-git-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;
    private readonly string _workDir;

    public GitWorkspaceTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        _workDir = Path.Combine(_root, "runner");
    }

    [Fact]
    public async Task Teardown_commits_and_pushes_uncommitted_changes_before_removal()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(workspace.RepoPath, "work.txt"), "valuable work");

        var result = await workspace.TeardownAsync("Unknown", CancellationToken.None);

        Assert.True(result.SecuredWork);
        Assert.Equal("runner/runner-test/AGT-2147", result.Branch);
        Assert.False(Directory.Exists(workspace.RepoPath));
        Assert.Equal("valuable work", (await GitAsync(_origin,
            "show", $"refs/heads/{result.Branch}:work.txt")).StdOut);
        Assert.Equal("wip(runner): salvage before teardown - outcome Unknown",
            (await GitAsync(_origin, "log", "-1", "--format=%s", $"refs/heads/{result.Branch}")).StdOut);
    }

    [Fact]
    public async Task Teardown_pushes_an_unpushed_local_commit_before_removal()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(workspace.RepoPath, "committed.txt"), "local commit");
        await GitAsync(workspace.RepoPath, "add", "--all");
        await GitAsync(workspace.RepoPath, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "local work");
        var localHead = (await GitAsync(workspace.RepoPath, "rev-parse", "HEAD")).StdOut;

        var result = await workspace.TeardownAsync("Done", CancellationToken.None);

        Assert.True(result.SecuredWork);
        Assert.False(Directory.Exists(workspace.RepoPath));
        Assert.Equal(localHead,
            (await GitAsync(_origin, "rev-parse", $"refs/heads/{result.Branch}")).StdOut);
    }

    [Fact]
    public async Task Teardown_removes_a_clean_worktree_without_creating_a_salvage_branch()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);

        var result = await workspace.TeardownAsync("Done", CancellationToken.None);

        Assert.False(result.SecuredWork);
        Assert.False(string.IsNullOrWhiteSpace(result.ResultSha));
        Assert.Equal((await GitAsync(_origin, "rev-parse", "refs/heads/main")).StdOut, result.ResultSha);
        Assert.False(Directory.Exists(workspace.RepoPath));
        var branch = await RunGitAsync(_origin, "show-ref", "--verify", "--quiet",
            "refs/heads/runner/runner-test/AGT-2147");
        Assert.False(branch.Success);
    }

    [Fact]
    public async Task Teardown_kills_processes_with_cwd_in_worktree_before_removal()
    {
        if (!OperatingSystem.IsLinux()) return;
        await SeedOriginAsync();
        var logs = new List<string>();
        var workspace = CreateWorkspace(logs.Add);
        await workspace.PrepareAsync(CancellationToken.None);
        var processTask = ProcessRunner.RunAsync(
            "/bin/sh",
            ["-c", "sleep 300 & wait"],
            workingDirectory: workspace.RepoPath,
            isolateProcessGroup: true);
        for (var attempt = 0;
             attempt < 100 && WorktreeProcessReaper.FindByCwd(workspace.RepoPath).Count == 0;
             attempt++)
            await Task.Delay(20);
        Assert.NotEmpty(WorktreeProcessReaper.FindByCwd(workspace.RepoPath));

        await workspace.TeardownAsync("Done", CancellationToken.None);
        var process = await processTask;

        Assert.NotEqual(0, process.ExitCode);
        Assert.False(Directory.Exists(workspace.RepoPath));
        var reapStart = logs.FindIndex(line => line.Contains("worktree-process-reap-started", StringComparison.Ordinal));
        var teardownDone = logs.FindIndex(line => line.Contains("worktree-teardown-completed", StringComparison.Ordinal));
        Assert.True(reapStart >= 0 && teardownDone > reapStart);
    }

    [Fact]
    public async Task Teardown_keeps_the_worktree_when_the_salvage_push_fails()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(workspace.RepoPath, "work.txt"), "must survive");
        var offlineOrigin = _origin + ".offline";
        Directory.Move(_origin, offlineOrigin);

        var error = await Assert.ThrowsAsync<WorktreeSalvageException>(
            () => workspace.TeardownAsync("Unknown", CancellationToken.None));

        Assert.Equal(workspace.RepoPath, error.WorktreePath);
        Assert.True(Directory.Exists(workspace.RepoPath));
        Assert.Equal("must survive", await File.ReadAllTextAsync(Path.Combine(workspace.RepoPath, "work.txt")));
        Directory.Move(offlineOrigin, _origin);
    }

    [Fact]
    public async Task Durable_handoff_keeps_worktree_until_matching_ack_and_publishes_immutable_ref()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(workspace.RepoPath, "result.txt", "durable", "durable result");
        const string runId = "run_test";

        var secured = await workspace.SecureForHandoffAsync(
            "Done", runId, CancellationToken.None);

        Assert.True(Directory.Exists(workspace.RepoPath));
        Assert.Equal(
            $"refs/heads/agent-studio/results/{runId}/{secured.ResultSha}",
            secured.ImmutableResultRef);
        Assert.Equal(
            secured.ResultSha,
            (await GitAsync(_origin, "rev-parse", secured.ImmutableResultRef!)).StdOut);
        var expectedDigest = new string('a', 64);
        var wrongAck = new ResultHandoffAck(
            runId,
            5,
            new string('b', 64),
            "acknowledged",
            DateTime.UtcNow,
            DateTime.UtcNow.AddDays(30),
            false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.TeardownAfterHandoffAsync(
                secured, wrongAck, runId, expectedDigest, CancellationToken.None));
        Assert.True(Directory.Exists(workspace.RepoPath));

        await workspace.TeardownAfterHandoffAsync(
            secured,
            wrongAck with { EnvelopeDigest = expectedDigest },
            runId,
            expectedDigest,
            CancellationToken.None);

        Assert.False(Directory.Exists(workspace.RepoPath));
    }

    [Fact]
    public async Task Durable_handoff_refuses_cleanup_when_worktree_changes_after_envelope_publication()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(workspace.RepoPath, "result.txt", "durable", "durable result");
        const string runId = "run_post_handoff_change";
        var secured = await workspace.SecureForHandoffAsync(
            "Done", runId, CancellationToken.None);
        var digest = new string('a', 64);
        var acknowledgement = new ResultHandoffAck(
            runId,
            5,
            digest,
            "acknowledged",
            DateTime.UtcNow,
            DateTime.UtcNow.AddDays(30),
            false);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.RepoPath, "late-work.txt"),
            "must not be discarded");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.TeardownAfterHandoffAsync(
                secured,
                acknowledgement,
                runId,
                digest,
                CancellationToken.None));

        Assert.Contains("changed after handoff", error.Message);
        Assert.True(Directory.Exists(workspace.RepoPath));
        Assert.Equal(
            "must not be discarded",
            await File.ReadAllTextAsync(Path.Combine(workspace.RepoPath, "late-work.txt")));
    }

    [Fact]
    public async Task Requeue_resumes_and_relinks_the_existing_salvage_branch()
    {
        await SeedOriginAsync();
        var first = CreateWorkspace();
        await first.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(first.RepoPath, "work.txt"), "first run");
        var salvaged = await first.TeardownAsync("Unknown", CancellationToken.None);

        var requeue = CreateWorkspace();
        var sourceBranch = await requeue.PrepareAsync(CancellationToken.None);
        Assert.Equal(salvaged.Branch, sourceBranch);
        Assert.Equal("first run", await File.ReadAllTextAsync(Path.Combine(requeue.RepoPath, "work.txt")));

        var result = await requeue.TeardownAsync("Done", CancellationToken.None);

        Assert.True(result.SecuredWork);
        Assert.Equal(salvaged.Branch, result.Branch);
        Assert.Equal(salvaged.CommitSha, result.CommitSha);
    }

    [Fact]
    public async Task Retained_local_ahead_tip_fast_forwards_canonical_salvage_ref_and_starts_pickup()
    {
        await SeedOriginAsync();
        const string branch = "runner/runner-test/AGT-2147";
        await GitAsync(_origin, "branch", branch, "main");
        var retained = CreateWorkspace();
        await retained.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(retained.RepoPath, "local.txt", "local ahead", "local ahead");
        var localHead = (await GitAsync(retained.RepoPath, "rev-parse", "HEAD")).StdOut;

        var pickup = CreateWorkspace();
        var source = await pickup.PrepareAsync(CancellationToken.None);

        Assert.Equal(branch, source);
        Assert.Equal(localHead, (await GitAsync(_origin, "rev-parse", $"refs/heads/{branch}")).StdOut);
        Assert.Equal("local ahead", await File.ReadAllTextAsync(Path.Combine(pickup.RepoPath, "local.txt")));
    }

    [Fact]
    public async Task Retained_tip_creates_missing_canonical_ref_and_uses_it_as_authoritative_base()
    {
        await SeedOriginAsync();
        const string branch = "runner/runner-test/AGT-2147";
        var retained = CreateWorkspace();
        await retained.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(retained.RepoPath, "retained.txt", "retained history", "retained side");
        var retainedHead = (await GitAsync(retained.RepoPath, "rev-parse", "HEAD")).StdOut;

        var pickup = CreateWorkspace();
        var source = await pickup.PrepareAsync(CancellationToken.None);

        Assert.Equal(branch, source);
        Assert.Equal(retainedHead, (await GitAsync(_origin, "rev-parse", $"refs/heads/{branch}")).StdOut);
        Assert.Equal(retainedHead, (await GitAsync(pickup.RepoPath, "rev-parse", "HEAD")).StdOut);
        Assert.Equal("retained history", await File.ReadAllTextAsync(Path.Combine(pickup.RepoPath, "retained.txt")));
        Assert.Equal("local-ahead", pickup.PickupReconciliation?.Kind);
        Assert.Equal(retainedHead, pickup.PickupReconciliation?.AuthoritativeBaseSha);
    }

    [Fact]
    public async Task Retained_remote_ahead_tip_uses_remote_tip_as_authoritative_base_without_republishing_local()
    {
        await SeedOriginAsync();
        const string branch = "runner/runner-test/AGT-2147";
        await GitAsync(_origin, "branch", branch, "main");
        var retained = CreateWorkspace();
        await retained.PrepareAsync(CancellationToken.None);
        var localHead = (await GitAsync(retained.RepoPath, "rev-parse", "HEAD")).StdOut;
        var remoteHead = await PublishRemoteCommitAsync(branch, "remote.txt", "remote ahead", "remote ahead");

        var pickup = CreateWorkspace();
        var source = await pickup.PrepareAsync(CancellationToken.None);

        Assert.Equal(branch, source);
        Assert.NotEqual(localHead, remoteHead);
        Assert.Equal(remoteHead, (await GitAsync(pickup.RepoPath, "rev-parse", "HEAD")).StdOut);
        Assert.Equal("remote ahead", await File.ReadAllTextAsync(Path.Combine(pickup.RepoPath, "remote.txt")));
    }

    [Fact]
    public async Task Divergent_retained_tip_is_published_to_deterministic_collision_ref_and_pickup_is_idempotent()
    {
        await SeedOriginAsync();
        const string branch = "runner/runner-test/AGT-2147";
        await GitAsync(_origin, "branch", branch, "main");
        var retained = CreateWorkspace();
        await retained.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(retained.RepoPath, "local.txt", "valuable local history", "local side");
        await File.WriteAllTextAsync(Path.Combine(retained.RepoPath, "dirty.txt"), "valuable dirty work");
        var remoteHead = await PublishRemoteCommitAsync(branch, "remote.txt", "canonical remote history", "remote side");
        var logs = new List<string>();

        var pickup = CreateWorkspace(logs.Add);
        var source = await pickup.PrepareAsync(CancellationToken.None);
        var collisionRefs = await CollisionRefsAsync(branch);

        Assert.Equal(branch, source);
        var collisionRef = Assert.Single(collisionRefs);
        var collisionHead = (await GitAsync(_origin, "rev-parse", collisionRef)).StdOut;
        Assert.Equal(remoteHead, (await GitAsync(_origin, "rev-parse", $"refs/heads/{branch}")).StdOut);
        Assert.Equal("canonical remote history", await File.ReadAllTextAsync(Path.Combine(pickup.RepoPath, "remote.txt")));
        Assert.Equal("valuable local history", (await GitAsync(_origin, "show", $"{collisionRef}:local.txt")).StdOut);
        Assert.Equal("valuable dirty work", (await GitAsync(_origin, "show", $"{collisionRef}:dirty.txt")).StdOut);
        Assert.Contains(logs, line =>
            line.Contains("worktree-salvage-reconciled kind=divergent", StringComparison.Ordinal)
            && line.Contains($"canonicalSha={remoteHead}", StringComparison.Ordinal)
            && line.Contains($"recoveryRef={collisionRef}", StringComparison.Ordinal)
            && line.Contains($"recoverySha={collisionHead}", StringComparison.Ordinal));

        var repeated = CreateWorkspace();
        await repeated.PrepareAsync(CancellationToken.None);

        Assert.Equal(remoteHead, (await GitAsync(repeated.RepoPath, "rev-parse", "HEAD")).StdOut);
        Assert.Equal(collisionRefs, await CollisionRefsAsync(branch));
        Assert.Equal("valuable local history", (await GitAsync(_origin, "show", $"{collisionRef}:local.txt")).StdOut);
    }

    [Fact]
    public async Task Clean_divergent_retained_tip_preserves_both_histories_and_starts_from_canonical_tip()
    {
        await SeedOriginAsync();
        const string branch = "runner/runner-test/AGT-2147";
        await GitAsync(_origin, "branch", branch, "main");
        var retained = CreateWorkspace();
        await retained.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(retained.RepoPath, "clean-local.txt", "clean local history", "clean local side");
        Assert.Equal(string.Empty, (await GitAsync(retained.RepoPath, "status", "--porcelain")).StdOut);
        var localHead = (await GitAsync(retained.RepoPath, "rev-parse", "HEAD")).StdOut;
        var remoteHead = await PublishRemoteCommitAsync(
            branch, "clean-remote.txt", "clean canonical history", "clean remote side");

        var pickup = CreateWorkspace();
        await pickup.PrepareAsync(CancellationToken.None);

        var collisionRef = Assert.Single(await CollisionRefsAsync(branch));
        Assert.Equal(localHead, (await GitAsync(_origin, "rev-parse", collisionRef)).StdOut);
        Assert.Equal(remoteHead, (await GitAsync(_origin, "rev-parse", $"refs/heads/{branch}")).StdOut);
        Assert.Equal(remoteHead, (await GitAsync(pickup.RepoPath, "rev-parse", "HEAD")).StdOut);
        Assert.Equal("clean local history", (await GitAsync(_origin, "show", $"{collisionRef}:clean-local.txt")).StdOut);
        Assert.Equal("clean canonical history", await File.ReadAllTextAsync(Path.Combine(pickup.RepoPath, "clean-remote.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private GitWorkspace CreateWorkspace(Action<string>? log = null)
        => new(new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = _origin,
            WorkDir = _workDir,
            StateDir = Path.Combine(_workDir, ".runner-state"),
            BaseBranch = "main",
            CliBin = "test",
            CliArgs = "",
        }, "AGT-2147", log ?? (_ => { }));

    private async Task CommitFileAsync(string repo, string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, path), content);
        await GitAsync(repo, "add", "--all");
        await GitAsync(repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", message);
    }

    private async Task<string> PublishRemoteCommitAsync(string branch, string path, string content, string message)
    {
        var publisher = Path.Combine(_root, "publisher-" + Guid.NewGuid().ToString("N"));
        await GitAsync(_root, "clone", _origin, publisher);
        await GitAsync(publisher, "checkout", "-B", "publish", $"origin/{branch}");
        await CommitFileAsync(publisher, path, content, message);
        await GitAsync(publisher, "push", "origin", $"HEAD:refs/heads/{branch}");
        return (await GitAsync(publisher, "rev-parse", "HEAD")).StdOut;
    }

    private async Task<List<string>> CollisionRefsAsync(string branch)
    {
        var refs = await GitAsync(_origin, "for-each-ref", "--format=%(refname)",
            $"refs/heads/{branch}-collision-*");
        return refs.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private async Task SeedOriginAsync()
    {
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        await GitAsync(_root, "init", "--bare", _origin);
        await GitAsync(_root, "init", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed");
        await GitAsync(seed, "add", "--all");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "seed");
        await GitAsync(seed, "branch", "-M", "main");
        await GitAsync(seed, "remote", "add", "origin", _origin);
        await GitAsync(seed, "push", "-u", "origin", "main");
    }

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args)
    {
        var result = await RunGitAsync(workingDirectory, args);
        Assert.True(result.Success,
            $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr}");
        return new ProcessResult(result.ExitCode, result.StdOut.Trim(), result.StdErr.Trim());
    }

    private static Task<ProcessResult> RunGitAsync(string workingDirectory, params string[] args)
        => ProcessRunner.RunAsync("git", args, workingDirectory: workingDirectory);
}
