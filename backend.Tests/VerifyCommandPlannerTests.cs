using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2065: the build/test gate derives its verify commands per project instead
/// of hardcoding <c>backend/OrchestratorApi.csproj</c> (the Studio-specific path
/// that broke on every other repo layout - TE-2, 2026-07-10). These fixtures
/// exercise the four shapes named in the task (.NET-only, npm-only, mixed, empty)
/// plus the explicit build-profile override and the honest empty fallback.
/// </summary>
public sealed class VerifyCommandPlannerTests : IDisposable
{
    private readonly string _root;

    public VerifyCommandPlannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "verify-planner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ---- Fixture: .NET only ------------------------------------------------

    [Fact]
    public void DotNetOnly_Sln_DerivesBareBuildAndTest()
    {
        Write("MyApp.sln", "Microsoft Visual Studio Solution File");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Collection(plan.Commands,
            c => AssertCommand(c, VerifyEcosystem.DotNet, VerifyCommandKind.Build, "", "dotnet build"),
            c => AssertCommand(c, VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test"));
    }

    [Fact]
    public void DotNetOnly_RootCsproj_DerivesBareBuildAndTest()
    {
        Write("Service.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Equal(2, plan.Commands.Count);
        Assert.All(plan.Commands, c => Assert.Equal(VerifyEcosystem.DotNet, c.Ecosystem));
    }

    [Fact]
    public void DotNet_SlnxWithNoRootProject_DerivesCommandsAtRepositoryRoot()
    {
        // TE-3 / AGT-2099: the solution is at the worktree root while its
        // projects live below it. A target-less dotnet command is valid only
        // when the runner preserves that root as its cwd.
        Write("TokenEconomy.slnx", "<Solution><Project Path=\"src/App/App.csproj\" /></Solution>");
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Collection(plan.Commands,
            c => AssertCommandAtRepositoryRoot(c, VerifyCommandKind.Build, "dotnet build"),
            c => AssertCommandAtRepositoryRoot(c, VerifyCommandKind.Test, "dotnet test"));
    }

    [Fact]
    public void DotNet_NestedCsprojOnly_NotDerivable_HonestFallback()
    {
        // A project a level down cannot be resolved by a bare `dotnet build` at the
        // root, so we must not pretend it can - the plan is empty, not a wrong path.
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceNone, plan.Source);
        Assert.True(plan.IsEmpty);
    }

    // ---- Fixture: npm only -------------------------------------------------

    [Fact]
    public void NpmOnly_RootManifest_DerivesDeclaredScriptsOnly()
    {
        Write("package.json", """
            { "scripts": { "build": "tsc", "test": "vitest run", "lint": "eslint ." } }
            """);

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Collection(plan.Commands,
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Build, "", "npm run build"),
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Test, "", "npm test"),
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Lint, "", "npm run lint"));
    }

    [Fact]
    public void NpmOnly_OnlyTestScript_DerivesOnlyThatScript()
    {
        Write("package.json", """{ "scripts": { "test": "vitest run" } }""");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Single(plan.Commands);
        AssertCommand(plan.Commands[0], VerifyEcosystem.Node, VerifyCommandKind.Test, "", "npm test");
    }

    [Fact]
    public void NpmOnly_PlaceholderTestScript_IsIgnored()
    {
        // The scaffolded npm default must not turn the gate red.
        Write("package.json", """
            { "scripts": { "test": "echo \"Error: no test specified\" && exit 1" } }
            """);

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceNone, plan.Source);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Npm_SubdirManifest_DerivesCommandsScopedToThatSubdir()
    {
        Write("frontend/package.json", """{ "scripts": { "build": "ng build", "lint": "ng lint" } }""");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Collection(plan.Commands,
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Build, "frontend", "npm run build"),
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Lint, "frontend", "npm run lint"));
    }

    [Fact]
    public void Npm_NodeModulesManifest_IsNotScanned()
    {
        // A package.json inside a dependency folder must never be treated as a
        // project manifest.
        Write("node_modules/left-pad/package.json", """{ "scripts": { "build": "noop" } }""");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceNone, plan.Source);
        Assert.True(plan.IsEmpty);
    }

    // ---- Fixture: mixed (.NET + npm) --------------------------------------

    [Fact]
    public void Mixed_SlnAndFrontendManifest_DerivesBoth()
    {
        Write("agent-taskboard.sln", "Microsoft Visual Studio Solution File");
        Write("frontend/package.json", """{ "scripts": { "build": "ng build", "test": "ng test", "lint": "ng lint" } }""");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Collection(plan.Commands,
            c => AssertCommand(c, VerifyEcosystem.DotNet, VerifyCommandKind.Build, "", "dotnet build"),
            c => AssertCommand(c, VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test"),
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Build, "frontend", "npm run build"),
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Test, "frontend", "npm test"),
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Lint, "frontend", "npm run lint"));
    }

    // ---- Fixture: empty ----------------------------------------------------

    [Fact]
    public void Empty_NothingDerivable_HonestFallback()
    {
        Write("README.md", "# just docs");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceNone, plan.Source);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void MissingRepositoryPath_HonestFallback()
    {
        var plan = VerifyCommandPlanner.Plan(Path.Combine(_root, "does-not-exist"), profile: null);

        Assert.Equal(VerifyPlan.SourceNone, plan.Source);
        Assert.True(plan.IsEmpty);
    }

    // ---- Explicit build-profile override ----------------------------------

    [Fact]
    public void BuildProfile_WithCommands_OverridesAutoDiscovery()
    {
        // Even though the repo has a .sln that would auto-discover, the declared
        // profile's commands win outright.
        Write("agent-taskboard.sln", "Microsoft Visual Studio Solution File");
        var profile = new BuildProfile
        {
            BuildCmds = ["make build"],
            TestCmds = ["make test"],
        };

        var plan = VerifyCommandPlanner.Plan(_root, profile);

        Assert.Equal(VerifyPlan.SourceBuildProfile, plan.Source);
        Assert.Collection(plan.Commands,
            c => AssertCommand(c, VerifyEcosystem.Custom, VerifyCommandKind.Build, "", "make build"),
            c => AssertCommand(c, VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "make test"));
    }

    [Fact]
    public void BuildProfile_WithoutBuildOrTestCommands_FallsThroughToDiscovery()
    {
        // A profile that only declares install/lockfile metadata is not a verify
        // override; discovery still applies.
        Write("agent-taskboard.sln", "Microsoft Visual Studio Solution File");
        var profile = new BuildProfile { InstallCmd = "dotnet restore", Lockfiles = ["packages.lock.json"] };

        var plan = VerifyCommandPlanner.Plan(_root, profile);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.All(plan.Commands, c => Assert.Equal(VerifyEcosystem.DotNet, c.Ecosystem));
    }

    // ---- Real case: this repo (agent-studio) ------------------------------

    [SkippableFact]
    public void RealCase_ThisRepo_DerivesDotNetPlusFrontendNpm()
    {
        // The TE-2 finding was a real mixed repo escalating on a hardcoded path.
        // The Studio checkout is itself the mixed real case: a root
        // agent-taskboard.sln plus frontend/package.json. Deriving against the
        // actual tree (not a fixture) proves the bare, layout-driven commands.
        var repoRoot = FindRepoRoot();
        Skip.If(repoRoot is null, "agent-taskboard.sln not found above the test assembly");

        var plan = VerifyCommandPlanner.Plan(repoRoot!, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Contains(plan.Commands, c =>
            c.Ecosystem == VerifyEcosystem.DotNet && c.Kind == VerifyCommandKind.Build && c.Command == "dotnet build");
        Assert.Contains(plan.Commands, c =>
            c.Ecosystem == VerifyEcosystem.DotNet && c.Kind == VerifyCommandKind.Test && c.Command == "dotnet test");
        // frontend/package.json declares build/test/lint scripts.
        Assert.Contains(plan.Commands, c =>
            c.Ecosystem == VerifyEcosystem.Node && c.WorkingSubdir == "frontend" && c.Command == "npm run build");
        // No command references the old hardcoded backend/OrchestratorApi.csproj path.
        Assert.DoesNotContain(plan.Commands, c => c.Command.Contains("OrchestratorApi.csproj"));
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "agent-taskboard.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static void AssertCommand(
        VerifyCommand cmd, VerifyEcosystem ecosystem, VerifyCommandKind kind, string subdir, string command)
    {
        Assert.Equal(ecosystem, cmd.Ecosystem);
        Assert.Equal(kind, cmd.Kind);
        Assert.Equal(subdir, cmd.WorkingSubdir);
        Assert.Equal(command, cmd.Command);
    }

    private void AssertCommandAtRepositoryRoot(
        VerifyCommand cmd, VerifyCommandKind kind, string command)
    {
        AssertCommand(cmd, VerifyEcosystem.DotNet, kind, "", command);
        Assert.Equal(Path.GetFullPath(_root), BuildTestGateRunner.ResolveWorkingDirectory(_root, cmd));
    }
}

/// <summary>
/// Behavior of <see cref="BuildTestGateRunner"/> around the derived plan: the
/// skip branches, the honest "no verify commands derivable" fallback, and the
/// end-to-end command loop driven through the build-profile override (trivial
/// shell commands so the test needs no real toolchain).
/// </summary>
public sealed class BuildTestGateRunnerBehaviorTests : IDisposable
{
    private readonly string _root;
    private readonly BuildTestGateRunner _runner = new(NullLogger<BuildTestGateRunner>.Instance);

    public BuildTestGateRunnerBehaviorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "verify-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private Task<BuildTestGateResult> Run(
        BuildProfile? profile,
        IReadOnlyList<string>? changedFiles = null,
        PostStepMode mode = PostStepMode.Fail)
        => _runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false),
            changedFiles, profile, mode, TimeSpan.FromSeconds(30), CancellationToken.None);

    [Fact]
    public async Task ModeOff_Skips()
    {
        var r = await Run(profile: null, mode: PostStepMode.Off);
        Assert.Equal(BuildTestGateVerdict.Skipped, r.Verdict);
        Assert.Equal("mode=off", r.Reason);
    }

    [Fact]
    public async Task NoCodeDiff_Skips()
    {
        var r = await Run(profile: null, changedFiles: ["docs/note.md", "README.md"]);
        Assert.Equal(BuildTestGateVerdict.Skipped, r.Verdict);
        Assert.Equal("no code diff", r.Reason);
    }

    [Fact]
    public async Task NoDerivableCommands_SkipsWithHonestReason()
    {
        // Empty repo, no profile -> the gate runs without a build check and says so.
        var r = await Run(profile: null);
        Assert.Equal(BuildTestGateVerdict.Skipped, r.Verdict);
        Assert.Equal("no verify commands derivable", r.Reason);
        Assert.False(r.RanBackendBuild);
        Assert.False(r.RanFrontendBuild);
    }

    [Fact]
    public async Task ProfileOverride_AllGreen_ReturnsOk()
    {
        var profile = new BuildProfile { BuildCmds = ["exit 0"], TestCmds = ["exit 0"] };

        var r = await Run(profile);

        Assert.Equal(BuildTestGateVerdict.Ok, r.Verdict);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.RanBackendBuild);
        Assert.Contains("build-profile", r.Reason);
    }

    [Fact]
    public async Task CommandExecution_CanonicalizesRelativeRepositoryPathAsCwd()
    {
        var printWorkingDirectory = OperatingSystem.IsWindows() ? "cd" : "pwd";
        var relativeRoot = Path.GetRelativePath(Environment.CurrentDirectory, _root);
        var profile = new BuildProfile { BuildCmds = [printWorkingDirectory] };

        var r = await _runner.RunAsync(
            new BuildTestGateRequest(relativeRoot, null, "test", RequireExactSubject: false),
            changedFiles: null, profile, PostStepMode.Fail,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, r.Verdict);
        Assert.Contains(Path.GetFullPath(_root), r.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProfileOverride_FirstFailure_StopsAndFails()
    {
        var profile = new BuildProfile { BuildCmds = ["exit 7", "exit 0"] };

        var r = await Run(profile, mode: PostStepMode.Fail);

        Assert.Equal(BuildTestGateVerdict.Fail, r.Verdict);
        Assert.Equal(7, r.ExitCode);
        // The second command must never run once the first fails.
        Assert.DoesNotContain("exit 0", r.Output);
    }

    [Fact]
    public async Task ProfileOverride_Failure_InWarnMode_ReturnsWarn()
    {
        var profile = new BuildProfile { BuildCmds = ["exit 3"] };

        var r = await Run(profile, mode: PostStepMode.Warn);

        Assert.Equal(BuildTestGateVerdict.Warn, r.Verdict);
        Assert.Equal(3, r.ExitCode);
    }

    [Fact]
    public async Task ThreeParallelExactSubjectGatesSerializeAndUseDistinctWorkspaces()
    {
        var sha = InitializeGitRepository();
        var profile = new BuildProfile
        {
            BuildCmds =
            [
                OperatingSystem.IsWindows()
                    ? "ping 127.0.0.1 -n 2 > nul"
                    : "sleep 1",
            ],
        };
        var tasks = Enumerable.Range(1, 3).Select(index => _runner.RunAsync(
            new BuildTestGateRequest(_root, sha, $"executor-{index}")
            {
                AttemptChainId = $"attempt-{index}",
                InfrastructureTimeout = TimeSpan.FromSeconds(15),
            },
            changedFiles: null,
            profile,
            PostStepMode.Fail,
            TimeSpan.FromSeconds(10),
            CancellationToken.None)).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, result =>
        {
            Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
            Assert.Equal(sha, result.ExpectedSha);
            Assert.Equal(sha, result.TestedSha);
            Assert.False(string.IsNullOrWhiteSpace(result.Workspace));
            Assert.NotEqual(Path.GetFullPath(_root), Path.GetFullPath(result.Workspace!));
        });
        Assert.Equal(3, results.Select(result => result.Workspace).Distinct().Count());
        Assert.True(results.Count(result => result.GateCollisionDetected) >= 2);
        Assert.True(results.Where(result => result.GateCollisionDetected).All(result => result.GateQueueWaitMs > 0));
        Assert.Empty(Directory.EnumerateDirectories(BuildTestGateRunner.ReviewWorkspaceRoot));
    }

    [Fact]
    public async Task MissingExactSubjectFailsClosedAsReviewInfrastructure()
    {
        InitializeGitRepository();

        var result = await _runner.RunAsync(
            new BuildTestGateRequest(_root, null, "executor"),
            changedFiles: null,
            new BuildProfile { BuildCmds = ["exit 0"] },
            PostStepMode.Fail,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.MissingSource, result.FailureKind);
        Assert.True(result.IsInfrastructureFailure);
        Assert.Empty(result.Processes);
        Assert.Null(result.Workspace);
    }

    [Fact]
    public async Task MachineGateWaitIsBoundedByInfrastructureSla()
    {
        const string activeFile = "machine-gate-sla-active.tmp";
        var leader = Run(new BuildProfile
        {
            BuildCmds =
            [
                OperatingSystem.IsWindows()
                    ? $"type nul > {activeFile} & ping 127.0.0.1 -n 3 > nul & del {activeFile}"
                    : $"touch {activeFile}; sleep 2; rm -f {activeFile}",
            ],
        });
        var activePath = Path.Combine(_root, activeFile);
        for (var index = 0; index < 100 && !File.Exists(activePath); index++)
            await Task.Delay(25);
        Assert.True(File.Exists(activePath));

        var follower = await _runner.RunAsync(
            new BuildTestGateRequest(_root, null, "bounded", RequireExactSubject: false)
            {
                InfrastructureTimeout = TimeSpan.FromMilliseconds(100),
            },
            changedFiles: null,
            new BuildProfile { BuildCmds = ["exit 0"] },
            PostStepMode.Fail,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.Equal(BuildTestGateFailureKind.Timeout, follower.FailureKind);
        Assert.True(follower.GateCollisionDetected);
        Assert.True(follower.GateQueueWaitMs >= 50);
        Assert.Equal(BuildTestGateVerdict.Ok, (await leader).Verdict);
    }

    [Fact]
    public async Task LateLockEvidenceSurvivesRingBufferAndTimeoutTestNameIsNotMisclassified()
    {
        var command = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,350) do @echo line-%i & echo error MSB3027: file is locked 1>&2 & exit /b 1"
            : "i=1; while [ $i -le 350 ]; do echo line-$i; i=$((i+1)); done; echo 'error MSB3027: file is locked' >&2; exit 1";

        var result = await Run(new BuildProfile { BuildCmds = [command] });

        Assert.Equal(BuildTestGateFailureKind.Lock, result.FailureKind);
        Assert.Contains("MSB3027", result.Output);
        Assert.DoesNotContain("line-1\n", result.Output);
        var process = Assert.Single(result.Processes);
        Assert.Contains("line-1", process.StandardOutput);
        Assert.Contains("line-350", process.StandardOutput);
        Assert.Equal(BuildTestGateFailureKind.None,
            BuildTestGateRunner.ClassifyFailure("TimeoutBehaviorTests passed"));
    }

    [Fact]
    public async Task ConcurrentRuns_ForSameRepository_DoNotOverlapCommands()
    {
        const string activeFile = "build-gate-active.tmp";
        const string overlapFile = "build-gate-overlap.tmp";
        var holdCommand = OperatingSystem.IsWindows()
            ? $"type nul > {activeFile} & ping 127.0.0.1 -n 3 > nul & del {activeFile}"
            : $"touch {activeFile}; sleep 2; rm -f {activeFile}";
        var probeCommand = OperatingSystem.IsWindows()
            ? $"if exist {activeFile} type nul > {overlapFile}"
            : $"if [ -e {activeFile} ]; then touch {overlapFile}; fi";

        var first = Run(new BuildProfile { BuildCmds = [holdCommand] });
        var activePath = Path.Combine(_root, activeFile);
        for (var i = 0; i < 100 && !File.Exists(activePath); i++)
            await Task.Delay(25);
        Assert.True(File.Exists(activePath), "The first gate command did not enter its critical section.");

        var second = Run(new BuildProfile { BuildCmds = [probeCommand] });
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict));
        Assert.False(File.Exists(Path.Combine(_root, overlapFile)),
            "Two verification command loops ran concurrently in the same repository.");
    }

    // MachineBound 19.07.: TCS/Cancellation-Timing flakt unter Parallellast im Karten-Gate.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task CancellationDuringHostLoadWait_ReleasesRepositoryAdmission()
    {
        var loadThrottle = new CancelFirstLoadThrottle();
        var runner = new BuildTestGateRunner(
            NullLogger<BuildTestGateRunner>.Instance,
            loadThrottle);
        var profile = new BuildProfile { BuildCmds = ["exit 0"] };
        using var firstCancellation = new CancellationTokenSource();

        var canceled = runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false),
            changedFiles: null, profile, PostStepMode.Fail,
            TimeSpan.FromSeconds(30), firstCancellation.Token);
        await loadThrottle.FirstWaitEntered.WaitAsync(TimeSpan.FromSeconds(5));
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled);

        using var followupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var followup = await runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false),
            changedFiles: null, profile, PostStepMode.Fail,
            TimeSpan.FromSeconds(30), followupCancellation.Token);

        Assert.Equal(BuildTestGateVerdict.Ok, followup.Verdict);
    }

    // MachineBound 19.07.: Queue-Cancellation-Timing flakt unter Parallellast im Karten-Gate.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task CancellationWhileQueued_DoesNotOpenAdmissionForAnotherRun()
    {
        const string activeFile = "build-gate-queued-active.tmp";
        const string overlapFile = "build-gate-queued-overlap.tmp";
        var holdCommand = OperatingSystem.IsWindows()
            ? $"type nul > {activeFile} & ping 127.0.0.1 -n 3 > nul & del {activeFile}"
            : $"touch {activeFile}; sleep 2; rm -f {activeFile}";
        var probeCommand = OperatingSystem.IsWindows()
            ? $"if exist {activeFile} type nul > {overlapFile}"
            : $"if [ -e {activeFile} ]; then touch {overlapFile}; fi";

        var leader = Run(new BuildProfile { BuildCmds = [holdCommand] });
        var activePath = Path.Combine(_root, activeFile);
        for (var i = 0; i < 100 && !File.Exists(activePath); i++)
            await Task.Delay(25);
        Assert.True(File.Exists(activePath), "The leader gate did not enter its critical section.");

        using var queuedCancellation = new CancellationTokenSource();
        var queued = _runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false),
            changedFiles: null, new BuildProfile { BuildCmds = ["exit 0"] },
            PostStepMode.Fail, TimeSpan.FromSeconds(30), queuedCancellation.Token);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);

        var follower = Run(new BuildProfile { BuildCmds = [probeCommand] });
        var results = await Task.WhenAll(leader, follower);

        Assert.All(results, result => Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict));
        Assert.False(File.Exists(Path.Combine(_root, overlapFile)),
            "Canceling a queued gate released an admission it did not own.");
    }

    private sealed class CancelFirstLoadThrottle : ILoadThrottleGate
    {
        private readonly TaskCompletionSource _firstWaitEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public LoadThrottleDecision Current => new(false, 0, TimeSpan.Zero);
        public bool WasRecentlyActive => false;
        public Task FirstWaitEntered => _firstWaitEntered.Task;

        public async Task WaitUntilReadyAsync(string reason, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) != 1) return;
            _firstWaitEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private string InitializeGitRepository()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "test@example.invalid");
        RunGit("config", "user.name", "Gate Test");
        File.WriteAllText(Path.Combine(_root, "subject.txt"), "exact subject");
        RunGit("add", "subject.txt");
        RunGit("commit", "-q", "-m", "exact subject");
        return RunGit("rev-parse", "HEAD").Trim();
    }

    private string RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }
}
