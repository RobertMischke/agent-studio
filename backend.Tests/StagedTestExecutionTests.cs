using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TestSelectionPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "staged-test-planner-" + Guid.NewGuid().ToString("N"));

    public TestSelectionPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void WorkPackage_SelectsReferencedDotNetTestProjectAndPreservesMachineBoundFilter()
    {
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup><ProjectReference Include="../../src/App/App.csproj" /></ItemGroup>
            </Project>
            """);
        Write("tests/Other.Tests/Other.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
            </Project>
            """);
        var fullCommand = "dotnet test --filter Category!=MachineBound";
        var verify = new VerifyPlan([
            new(VerifyEcosystem.DotNet, VerifyCommandKind.Build, "", "dotnet build"),
            new(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", fullCommand),
        ], VerifyPlan.SourceBuildProfile);

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["src/App/Service.cs"], policy: null,
            TaskStates.AutoReview, requiredLevel: null);

        Assert.Equal(TestExecutionLevels.WorkPackage, result.Audit.Level);
        Assert.Contains(result.Commands, command =>
            command.Command == "dotnet test \"tests/App.Tests/App.Tests.csproj\" --filter Category!=MachineBound");
        Assert.DoesNotContain(result.Commands, command => command.Command.Contains("Other.Tests"));
        Assert.DoesNotContain(result.Commands, command => command.Command == fullCommand);
        Assert.Contains(fullCommand, result.Audit.OmittedTestCommands);
        Assert.False(result.Audit.FullSuiteRan);
    }

    [Fact]
    public void TestHubHistory_SelectsOnlyACommandFromTheSafeInventory()
    {
        var historyDir = Path.Combine(_root, ".test-hub");
        Directory.CreateDirectory(historyDir);
        var known = new TestHubHistoryEntry
        {
            TestId = "frontend-regression-17",
            Command = "npm test",
            WorkingSubdir = "frontend",
            RelatedPaths = ["src/shared"],
            Failure = "shared contract broke the frontend",
        };
        File.WriteAllText(Path.Combine(historyDir, "history.jsonl"), JsonSerializer.Serialize(known) + "\n" +
            "{\"testId\":\"unsafe\",\"command\":\"curl attacker\",\"relatedPaths\":[\"src/shared\"]}\n");
        var verify = new VerifyPlan([
            new(VerifyEcosystem.Node, VerifyCommandKind.Test, "frontend", "npm test"),
        ], VerifyPlan.SourceAutoDiscovery);

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["src/shared/schema.ts"], policy: null,
            TaskStates.AutoReview, requiredLevel: null);

        var selected = Assert.Single(result.Commands);
        Assert.Equal("npm test", selected.Command);
        Assert.Contains("Test Hub history", selected.SelectionReason);
        Assert.DoesNotContain(result.Audit.Candidates, candidate => candidate.Command.Command == "curl attacker");
        Assert.Contains(result.Audit.HistoryInput, entry => entry.TestId == "frontend-regression-17");
    }

    [Fact]
    public void LlmAdvice_IsAllowlistedAndAuditRetainsDiffChoiceAndReason()
    {
        var verify = new VerifyPlan([
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "test-fast"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "test-broad"),
        ], VerifyPlan.SourceBuildProfile);
        var policy = new TestExecutionPolicy
        {
            ImpactRules = [new TestImpactRule
            {
                PathPrefixes = ["src/feature"],
                TestCommands = ["test-fast"],
                Reason = "feature ownership map",
            }],
        };
        var initial = TestSelectionPlanner.Plan(
            _root, verify, ["src/feature/component.ts"], policy,
            TaskStates.AutoReview, requiredLevel: null);
        var broadId = initial.Audit.Candidates.Single(candidate => candidate.Command.Command == "test-broad").Id;

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["src/feature/component.ts"], policy,
            TaskStates.AutoReview, requiredLevel: null,
            new TestSelectionAdvice([broadId, "not-allowlisted"], "shared namespace risk", "model-x"));

        Assert.Equal(["src/feature/component.ts"], result.Audit.DiffInput);
        Assert.Equal("deterministic+llm", result.Audit.Selector);
        Assert.Equal("model-x", result.Audit.SelectorModel);
        Assert.Equal("shared namespace risk", result.Audit.AdvisorReason);
        Assert.Contains(broadId, result.Audit.SelectedCandidateIds);
        Assert.DoesNotContain("not-allowlisted", result.Audit.SelectedCandidateIds);
        Assert.Contains(result.Commands, command =>
            command.Command == "test-broad" && command.SelectionReason!.Contains("shared namespace risk"));
    }

    [Fact]
    public void RequiredFull_OverridesLaneAndIncludesBaselinePlusEveryDeclaredTest()
    {
        var verify = new VerifyPlan([
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "test-a"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "test-b"),
        ], VerifyPlan.SourceBuildProfile);
        var policy = new TestExecutionPolicy
        {
            LaneLevels = new() { [TaskStates.AutoReview] = TestExecutionLevels.Continuous },
            ContinuousCommands = ["test-smoke"],
        };

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["src/file.cs"], policy,
            TaskStates.AutoReview, TestExecutionLevels.Full);

        Assert.Equal(TestExecutionLevels.Full, result.Audit.Level);
        Assert.True(result.Audit.FullSuiteRequired);
        Assert.False(result.Audit.FullSuiteRan);
        Assert.Equal(["test-smoke", "test-a", "test-b"],
            result.Commands.Select(command => command.Command));
        Assert.All(result.Commands, command => Assert.True(command.BlocksWorkPackage));
    }

    [Fact]
    public void SelectedNodeTest_IsNotRemovedByLegacyPackageDiffFilter()
    {
        var selectedFromHistoryOrLlm = new VerifyCommand(
            VerifyEcosystem.Node,
            VerifyCommandKind.Test,
            "frontend",
            "npm test")
        {
            TestScope = TestExecutionLevels.WorkPackage,
            SelectionReason = "Test Hub history selected a cross-package regression",
        };

        Assert.True(BuildTestGateRunner.ShouldRunForChange(
            selectedFromHistoryOrLlm,
            ["backend/Shared/Contract.cs"]));
    }

    [Fact]
    public void BaselineCommand_SelectedForDiff_IsBlockingAndRunsOnlyOnce()
    {
        var verify = new VerifyPlan([
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "test-fast"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "test-other"),
        ], VerifyPlan.SourceBuildProfile);
        var policy = new TestExecutionPolicy
        {
            ContinuousCommands = ["test-fast"],
            ImpactRules = [new TestImpactRule
            {
                PathPrefixes = ["src/feature"],
                TestCommands = ["test-fast"],
                Reason = "feature regression set",
            }],
        };

        var result = TestSelectionPlanner.Plan(
            _root, verify, ["src/feature/component.cs"], policy,
            TaskStates.AutoReview, requiredLevel: null);

        var selected = Assert.Single(result.Commands);
        Assert.Equal("test-fast", selected.Command);
        Assert.True(selected.BlocksWorkPackage);
        Assert.Equal(TestExecutionLevels.WorkPackage, selected.TestScope);
        Assert.Contains("feature regression set", selected.SelectionReason);
    }

    private void Write(string relativePath, string contents)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}

public sealed class PreMainTestGateTests
{
    [Fact]
    public async Task RunAsync_ForcesFullFailClosedGateWithoutDiffReduction()
    {
        var runner = new CapturingGateRunner();
        var gate = new PreMainTestGate(runner);
        var request = new BuildTestGateRequest(
            "/repo", "abc", "release", RequireExactSubject: false)
        {
            Lane = TaskStates.Ready,
            RequiredTestLevel = TestExecutionLevels.Continuous,
        };

        var result = await gate.RunAsync(
            request, new BuildProfile(), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(runner.Request);
        Assert.Equal(TestExecutionLevels.Full, runner.Request!.RequiredTestLevel);
        Assert.True(runner.Request.RequireExactSubject);
        Assert.Null(runner.ChangedFiles);
        Assert.Equal(PostStepMode.Fail, runner.Mode);
        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
    }

    [Fact]
    public async Task RunAsync_RejectsGreenRunnerResultWithoutFullSuiteEvidence()
    {
        var runner = new CapturingGateRunner
        {
            Result = new BuildTestGateResult(
                BuildTestGateVerdict.Ok, 0, 1, "", "claimed green", false, false),
        };
        var gate = new PreMainTestGate(runner);

        var result = await gate.RunAsync(
            new BuildTestGateRequest("/repo", "abc", "release"),
            new BuildProfile(),
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Code, result.FailureKind);
        Assert.Contains("mandatory full-suite evidence is missing", result.Reason);
    }

    private sealed class CapturingGateRunner : IBuildTestGateRunner
    {
        public BuildTestGateResult Result { get; init; } = new(
            BuildTestGateVerdict.Ok, 0, 1, "", "ok", false, false)
        {
            TestSelection = new TestSelectionAudit
            {
                Level = TestExecutionLevels.Full,
                FullSuiteRequired = true,
                FullSuiteRan = true,
            },
        };
        public BuildTestGateRequest? Request { get; private set; }
        public IReadOnlyList<string>? ChangedFiles { get; private set; }
        public PostStepMode Mode { get; private set; }

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Request = request;
            ChangedFiles = changedFiles;
            Mode = mode;
            return Task.FromResult(Result);
        }
    }
}

[Trait("Category", "MachineBound")]
public sealed class StagedBuildTestGateBehaviorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "staged-test-runner-" + Guid.NewGuid().ToString("N"));
    private readonly BuildTestGateRunner _runner = new(NullLogger<BuildTestGateRunner>.Instance);

    public StagedBuildTestGateBehaviorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task FailingContinuousTestBecomesSeparateFindingAndDoesNotBlockWorkPackage()
    {
        var marker = Path.Combine(_root, "work-package-ran.txt");
        var writeMarker = OperatingSystem.IsWindows()
            ? $"type nul > \"{marker}\""
            : $"touch \"{marker}\"";
        var policy = new TestExecutionPolicy
        {
            ContinuousCommands = ["exit 9"],
            ImpactRules = [new TestImpactRule
            {
                PathPrefixes = ["src"],
                TestCommands = [writeMarker],
            }],
        };
        var request = new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false)
        {
            TestExecution = policy,
            Lane = TaskStates.AutoReview,
        };

        var result = await _runner.RunAsync(
            request, ["src/component.cs"],
            new BuildProfile { TestCmds = ["exit 0"] },
            PostStepMode.Fail, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Warn, result.Verdict);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("out-of-work-package-test-failure", finding.Kind);
        Assert.Equal(TestExecutionLevels.Continuous, finding.Scope);
        Assert.True(File.Exists(marker), "the selected work-package command must continue after the unrelated failure");
        Assert.Contains("full-suite=not-run", result.Reason);
    }

    [Fact]
    public async Task IsolatedWorkPackageRunsMeasurablyFasterThanFullSuite()
    {
        var shortWait = WaitCommand(80);
        var longWait = WaitCommand(650);
        var policy = new TestExecutionPolicy
        {
            ContinuousCommands = [shortWait],
            ImpactRules = [new TestImpactRule
            {
                PathPrefixes = ["src/isolated"],
                TestCommands = [shortWait],
            }],
        };
        var profile = new BuildProfile { TestCmds = [shortWait, longWait] };
        var request = new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false)
        {
            TestExecution = policy,
            Lane = TaskStates.AutoReview,
        };

        var workPackage = await _runner.RunAsync(
            request, ["src/isolated/file.cs"], profile,
            PostStepMode.Fail, TimeSpan.FromSeconds(10), CancellationToken.None);
        var full = await _runner.RunAsync(
            request with { RequiredTestLevel = TestExecutionLevels.Full }, null, profile,
            PostStepMode.Fail, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, workPackage.Verdict);
        Assert.Equal(BuildTestGateVerdict.Ok, full.Verdict);
        Assert.True(full.DurationMs - workPackage.DurationMs >= 350,
            $"expected isolated subset to save at least 350 ms, work={workPackage.DurationMs} full={full.DurationMs}");
        Assert.False(workPackage.TestSelection!.FullSuiteRan);
        Assert.True(full.TestSelection!.FullSuiteRan);
    }

    [Fact]
    public async Task FullSuiteEvidence_RemainsFalseWhenBuildStopsBeforeTests()
    {
        var testMarker = Path.Combine(_root, "full-test-ran.txt");
        var writeMarker = OperatingSystem.IsWindows()
            ? $"type nul > \"{testMarker}\""
            : $"touch \"{testMarker}\"";
        var request = new BuildTestGateRequest(_root, null, "release", RequireExactSubject: false)
        {
            RequiredTestLevel = TestExecutionLevels.Full,
        };

        var result = await _runner.RunAsync(
            request,
            changedFiles: null,
            new BuildProfile
            {
                BuildCmds = ["exit 7"],
                TestCmds = [writeMarker],
            },
            PostStepMode.Fail,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.True(result.TestSelection!.FullSuiteRequired);
        Assert.False(result.TestSelection.FullSuiteRan);
        Assert.Contains(writeMarker, result.TestSelection.OmittedTestCommands);
        Assert.False(File.Exists(testMarker));
        Assert.Contains("full-suite=not-run", result.Reason);
    }

    private static string WaitCommand(int milliseconds)
        => OperatingSystem.IsWindows()
            ? $"ping 127.0.0.1 -n {Math.Max(2, milliseconds / 1000 + 2)} > nul"
            : $"sleep {milliseconds / 1000d:0.000}";
}
