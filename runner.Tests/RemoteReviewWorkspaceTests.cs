using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteReviewWorkspaceTests : IDisposable
{
    [Trait("Category", "ReviewFlaky")]
    private sealed class ReviewFlakyClassTraitFixture
    {
    }

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "remote-review-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;
    private readonly string _reviewRoot;

    public RemoteReviewWorkspaceTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        _reviewRoot = Path.Combine(_root, "review");
    }

    public static TheoryData<string, string[]> NodeFailureFixtures => new()
    {
        {
            "npm-jest.stdout.txt",
            ["src/cart/cart.service.spec.ts > CartService > calculates the total"]
        },
        {
            "ng-karma.stdout.txt",
            ["CartComponent removes an item after confirmation"]
        },
        {
            "npm-vitest.stderr.txt",
            [
                "src/math.spec.ts > arithmetic > adds positive values",
                "src/math.spec.ts > arithmetic > rejects NaN",
            ]
        },
        {
            "node-test.stdout.txt",
            ["rejects an expired lease", "returns the current lease"]
        },
        {
            "npm-lifecycle.stderr.txt",
            ["npm script test:unit"]
        },
        {
            "npm-legacy.stderr.txt",
            ["npm script test"]
        },
    };

    [Theory]
    [MemberData(nameof(NodeFailureFixtures))]
    public void Parsed_test_failures_understands_node_test_runner_output(
        string fixtureName,
        string[] expected)
    {
        var output = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "review-failures",
            fixtureName));
        var result = fixtureName.Contains("stderr", StringComparison.Ordinal)
            ? new ProcessResult(1, string.Empty, output)
            : new ProcessResult(1, output, string.Empty);

        Assert.Equal(expected, RemoteReviewWorkspace.ParsedTestFailures(result));
    }

    [Fact]
    public void Parsed_test_failures_ignores_failure_text_when_process_succeeds()
    {
        var result = new ProcessResult(0, "FAIL src/example.spec.ts > suite > test", string.Empty);

        Assert.Empty(RemoteReviewWorkspace.ParsedTestFailures(result));
    }

    [Fact]
    public void Parsed_test_failures_strips_terminal_colours_and_ignores_tap_todo_items()
    {
        var result = new ProcessResult(
            1,
            "\u001b[31m FAIL  src/example.spec.ts > suite > fails\u001b[39m\n" +
            "not ok 2 - planned follow-up # TODO pending implementation",
            string.Empty);

        Assert.Equal(
            ["src/example.spec.ts > suite > fails"],
            RemoteReviewWorkspace.ParsedTestFailures(result));
    }

    [Fact]
    public void Review_flaky_trait_index_reads_xunit_class_and_method_traits()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));

        var index = ReviewFlakyTestIndex.Discover(repositoryRoot);

        Assert.True(index.Contains(
            "AgentRunner.Tests.RemoteTaskRunnerRestartTests.Restarted_runner_follows_fake_job_and_delivers_completion_without_a_zombie_lease"));
        Assert.True(index.Contains(
            "AgentRunner.Tests.RemoteReviewWorkspaceTests.ReviewFlakyClassTraitFixture.Any_test_method"));
        Assert.False(index.Contains(
            "AgentRunner.Tests.RemoteTaskRunnerRestartTests.Persisted_slot_state_written_before_the_base_sha_field_still_loads"));
    }

    [Fact]
    public void Review_failure_parser_quarantines_only_a_marked_failure_that_passes_its_retry()
    {
        const string marked = "Product.Tests.ProcessTiming";
        const string ordinary = "Product.Tests.ProductRule";
        var initial = BaselineComparison.Create(
            new string('a', 40),
            [],
            [marked, ordinary],
            cacheHit: false);
        var index = new ReviewFlakyTestIndex(methods: [marked]);

        var retried = initial.Reclassify([], index);
        var verdict = RemoteReviewWorkspace.BaselineVerdict(
            BaselineCommand("exit 0"),
            retried);

        Assert.Empty(retried.NewFailures);
        Assert.Equal([marked], retried.FlakyQuarantinedFailures);
        Assert.Equal("pass", verdict.Status);
        Assert.Equal("FlakyQuarantine", verdict.Classification);
        Assert.Contains(marked, verdict.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(ordinary, verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_failure_parser_keeps_a_reproduced_marked_failure_blocking()
    {
        const string marked = "Product.Tests.ProcessTiming";
        var initial = BaselineComparison.Create(
            new string('a', 40),
            [],
            [marked],
            cacheHit: false);

        var retried = initial.Reclassify(
            [marked],
            new ReviewFlakyTestIndex(methods: [marked]));
        var verdict = RemoteReviewWorkspace.BaselineVerdict(
            BaselineCommand("exit 1"),
            retried);

        Assert.Equal([marked], retried.NewFailures);
        Assert.Empty(retried.FlakyQuarantinedFailures);
        Assert.Equal("block", verdict.Status);
        Assert.Equal("NewTestFailures", verdict.Classification);
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
        Assert.Equal(workspace.BaselineCacheRoot, environment.Isolation["baselineResultCache"]);
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
                "mutation", "code-quality", PosixShell.RequirePath(),
                ["-c", "printf mutation > unexpected.txt"])],
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
                    "credential-check", "evidence", PosixShell.RequirePath(),
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

    [Fact]
    public async Task Baseline_comparison_blocks_only_new_test_failures_and_names_them()
    {
        var (_, subjectSha) = await SeedSubjectBranchAsync();
        var command = BaselineCommand(
            "if grep -q subject product.txt; then " +
            "printf '  Failed Product.ExistingFailure [1 ms]\\n  Failed Product.NewFailure [1 ms]\\n'; " +
            "else printf '  Failed Product.ExistingFailure [1 ms]\\n'; fi; exit 1");
        var (workspace, _) = Workspace(
            "attempt-new-failure",
            subjectSha,
            [command],
            26000,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main");
        await workspace.PrepareAsync(null!, default);

        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal("ProductFailure", evidence.Outcome);
        var commandEvidence = Assert.Single(evidence.Commands);
        Assert.Equal(["Product.NewFailure"], commandEvidence.NewFailures);
        Assert.Equal(["Product.ExistingFailure"], commandEvidence.PreExistingFailures);
        Assert.True(commandEvidence.RetryPerformed);
        var verdict = Assert.Single(evidence.Verdicts);
        Assert.Equal("block", verdict.Status);
        Assert.Contains("1 new failures: Product.NewFailure", verdict.Summary, StringComparison.Ordinal);
        Assert.Contains("1 pre-existing failures: Product.ExistingFailure", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Baseline_comparison_blocks_only_new_vitest_failures_and_names_them()
    {
        var (_, subjectSha) = await SeedSubjectBranchAsync();
        var command = BaselineCommand(
            "if grep -q subject product.txt; then " +
            "printf ' FAIL  src/math.spec.ts > arithmetic > existing failure\\n" +
            " FAIL  src/math.spec.ts > arithmetic > new failure\\n'; " +
            "else printf ' FAIL  src/math.spec.ts > arithmetic > existing failure\\n'; fi; exit 1");
        var (workspace, _) = Workspace(
            "attempt-new-vitest-failure",
            subjectSha,
            [command],
            26004,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main");
        await workspace.PrepareAsync(null!, default);

        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal("ProductFailure", evidence.Outcome);
        var commandEvidence = Assert.Single(evidence.Commands);
        Assert.Equal(
            ["src/math.spec.ts > arithmetic > new failure"],
            commandEvidence.NewFailures);
        Assert.Equal(
            ["src/math.spec.ts > arithmetic > existing failure"],
            commandEvidence.PreExistingFailures);
    }

    [Fact]
    public async Task Baseline_comparison_classifies_shared_red_tests_as_pre_existing()
    {
        var (_, subjectSha) = await SeedSubjectBranchAsync();
        var command = BaselineCommand(
            "printf '  Failed Product.ExistingFailure [1 ms]\\n'; exit 1");
        var (workspace, _) = Workspace(
            "attempt-pre-existing",
            subjectSha,
            [command],
            26008,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main");
        await workspace.PrepareAsync(null!, default);

        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal("Pass", evidence.Outcome);
        var commandEvidence = Assert.Single(evidence.Commands);
        Assert.Empty(commandEvidence.NewFailures!);
        Assert.Equal(["Product.ExistingFailure"], commandEvidence.PreExistingFailures);
        Assert.False(commandEvidence.RetryPerformed);
        var verdict = Assert.Single(evidence.Verdicts);
        Assert.Equal("pass", verdict.Status);
        Assert.Contains("0 new failures", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Baseline_result_is_reused_for_same_repository_sha_and_command()
    {
        var (_, subjectSha) = await SeedSubjectBranchAsync();
        var command = BaselineCommand(
            "printf '  Failed Product.ExistingFailure [1 ms]\\n'; exit 1");
        var first = Workspace(
            "attempt-cache-fill",
            subjectSha,
            [command],
            26016,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main").Workspace;
        await first.PrepareAsync(null!, default);
        var firstEvidence = await first.ExecutePlanAsync(default);
        await first.CleanupAsync();

        var second = Workspace(
            "attempt-cache-hit",
            subjectSha,
            [command],
            26024,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main").Workspace;
        await second.PrepareAsync(null!, default);
        var secondEvidence = await second.ExecutePlanAsync(default);

        Assert.False(Assert.Single(firstEvidence.Commands).BaselineCacheHit);
        Assert.True(Assert.Single(secondEvidence.Commands).BaselineCacheHit);
        Assert.Single(Directory.EnumerateFiles(second.BaselineCacheRoot, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Review_flaky_new_failure_gets_one_retry_and_is_quarantined_when_it_disappears()
    {
        const string failure =
            "AgentRunner.Tests.RemoteTaskRunnerRestartTests.Restarted_runner_follows_fake_job_and_delivers_completion_without_a_zombie_lease";
        var (_, subjectSha) = await SeedSubjectBranchAsync();
        var command = BaselineCommand(
            "if grep -q subject product.txt; then " +
            "if test ! -f \"$TMPDIR/retry-seen\"; then touch \"$TMPDIR/retry-seen\"; " +
            $"printf '  Failed {failure} [1 ms]\\n'; exit 1; fi; fi; exit 0");
        var (workspace, _) = Workspace(
            "attempt-flake",
            subjectSha,
            [command],
            26032,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main");
        await workspace.PrepareAsync(null!, default);
        var assemblyDirectory = Path.Combine(workspace.RepositoryPath, ".git", "bin");
        Directory.CreateDirectory(assemblyDirectory);
        File.Copy(
            typeof(RemoteReviewWorkspaceTests).Assembly.Location,
            Path.Combine(assemblyDirectory, "AgentRunner.Tests.dll"));

        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal("Pass", evidence.Outcome);
        var commandEvidence = Assert.Single(evidence.Commands);
        Assert.True(commandEvidence.RetryPerformed);
        Assert.Empty(commandEvidence.NewFailures!);
        Assert.Equal([failure], commandEvidence.FlakyQuarantinedFailures);
        Assert.Equal(0, commandEvidence.ExitCode);
        Assert.False(evidence.Workspace.DirtyAfter);
        Assert.Equal("FlakyQuarantine", Assert.Single(evidence.Verdicts).Classification);
    }

    private static ReviewCommandDto BaselineCommand(string shell)
        => new(
            "verify-2",
            "build-tests",
            PosixShell.RequirePath(),
            ["-c", shell],
            CompareToBaseline: true);

    private (RemoteReviewWorkspace Workspace, ReviewSubjectDto Subject) Workspace(
        string attemptId,
        string sha,
        IReadOnlyList<ReviewCommandDto> commands,
        int portBase,
        long fence = 1,
        string? resultRef = null,
        string? integrationRef = null)
    {
        var repositoryId = TaskServerClient.RepositoryIdentity(_origin)!;
        var subject = new ReviewSubjectDto(
            "subject-" + attemptId,
            "task-" + attemptId,
            "run-" + attemptId,
            repositoryId,
            _origin,
            sha,
            resultRef ?? "refs/heads/main",
            null,
            null,
            "coding-host",
            "policy",
            new ReviewPlanDto(
                commands,
                commands.Select(command => command.Aspect).ToArray(),
                IntegrationRef: integrationRef),
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

    private async Task<(string BaselineSha, string SubjectSha)> SeedSubjectBranchAsync()
    {
        var baselineSha = await SeedOriginAsync();
        var seed = Path.Combine(_root, "seed");
        var branch = await GitAsync(seed, "branch", "--list", "task/new-failure");
        if (branch.StdOut.Length == 0)
        {
            await GitAsync(seed, "checkout", "-b", "task/new-failure");
            await File.WriteAllTextAsync(Path.Combine(seed, "product.txt"), "subject product");
            await GitAsync(seed, "add", "product.txt");
            await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                "commit", "-m", "subject");
            await GitAsync(seed, "push", "origin", "task/new-failure");
        }
        var subjectSha = (await GitAsync(seed, "rev-parse", "task/new-failure")).StdOut.Trim();
        return (baselineSha, subjectSha);
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
        ResilientDirectory.TryDelete(_root);
    }
}
