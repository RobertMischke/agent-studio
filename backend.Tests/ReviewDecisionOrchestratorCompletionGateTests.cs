using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Pipeline;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Covers the deterministic completion gate wired into
/// <c>ProcessDoneAsync</c> (requirement 4): a DONE task whose own close-out
/// still lists unfinished work must never be silently accepted. With budget
/// left it reissues to 2-ready with the findings foregrounded; with the
/// reissue budget spent it escalates to 5-human-review. Both record the
/// post-core <see cref="PipelineCatalogue.OrchestratorReviewStepId"/> row so
/// the gate's ruling is visible in the Overview pipeline, and both short-
/// circuit BEFORE the parallel aspect review runs.
/// </summary>
public class ReviewDecisionOrchestratorCompletionGateTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public ReviewDecisionOrchestratorCompletionGateTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "orch-gate-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task TaskDone_OpenItemsInStatus_BudgetLeft_ReissuesWithGateVerdict()
    {
        SeedReviewJobWithDone("open-items-job",
            status: "## Open Items\n- [ ] Wire the new route into the shell\n");
        var aspect = new CountingAspect();
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Ready, "open-items-job");
        Assert.True(Directory.Exists(folder), "open items with budget left should reissue to 2-ready");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.OrchestratorReviewStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"reissue\"", pipelineJson);

        Assert.Equal(0, aspect.Invocations);
    }

    [Fact]
    public async Task TaskDone_SuccessButBuildFailed_BudgetLeft_Reissues()
    {
        // ASS-764: the run claims Result: Success while its own Notes report a
        // build failure. The contradiction rule must catch it rather than let
        // auto-review accept it with concerns.
        SeedReviewJobWithDone("contradiction-job",
            status: "Result: Success\n\n## Notes\nFinal build failed with error CS0246.\n");
        var aspect = new CountingAspect();
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Ready, "contradiction-job");
        Assert.True(Directory.Exists(folder), "a success claim contradicted by a build failure should reissue");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"verdict\": \"reissue\"", pipelineJson);
        Assert.Equal(0, aspect.Invocations);
    }

    [Fact]
    public async Task TaskDone_OpenItems_BudgetExhausted_EscalatesToHumanReview()
    {
        SeedReviewJobWithDone("escalate-job",
            status: "## Open Items\n- [ ] Finish the migration\n");
        var aspect = new CountingAspect();
        // maxReissues=0 -> the budget is exhausted at the first encounter, so the
        // gate must escalate instead of reissuing.
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 0);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "escalate-job");
        Assert.True(Directory.Exists(folder), "open items with no budget left should escalate to 5-human-review");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.OrchestratorReviewStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"escalate\"", pipelineJson);
        Assert.Equal(0, aspect.Invocations);
    }

    [Fact]
    public async Task TaskDone_CleanCloseOut_PassesGate_AndRunsAspects()
    {
        // Control: a clean close-out records the post-core Orchestrator-Review row
        // as "complete" and falls through to the aspect review + final decision.
        SeedReviewJobWithDone("clean-job",
            status: "## Summary\nDone and verified.\n\nResult: Success\n\n## Open Items\nNone\n");
        var aspect = new CountingAspect();
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "clean-job");
        Assert.True(Directory.Exists(folder), "a clean accept should promote to 5-human-review");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.OrchestratorReviewStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"complete\"", pipelineJson);
        Assert.True(aspect.Invocations > 0, "a clean gate must let the aspect review run");
    }

    [Fact]
    public async Task TaskDone_BuildTestGateFails_BudgetLeft_ReissuesBeforeAspects()
    {
        SeedReviewJobWithDone("build-red-job",
            status: "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        var aspect = new CountingAspect();
        var buildGate = new FakeBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 123,
            "TaskJsonFile.cs(10,20): error CS1061: missing member",
            "dotnet build exit 1", true, false));
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3, buildGate);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.Ready, "build-red-job");
        Assert.True(Directory.Exists(folder), "a red deterministic build gate should reissue to 2-ready");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.BuildTestGateStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"fail\"", pipelineJson);
        Assert.Contains("\"verdict\": \"reissue\"", pipelineJson);
        Assert.Equal(0, aspect.Invocations);
    }

    [Fact]
    public async Task TaskDone_BuildTestGateGreen_ContinuesToAspects()
    {
        SeedReviewJobWithDone("build-green-job",
            status: "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        var aspect = new CountingAspect();
        var buildGate = new FakeBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 123, "build passed", "build gate passed", true, false));
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3, buildGate);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "build-green-job");
        Assert.True(Directory.Exists(folder), "a green deterministic build gate should continue through auto-review");

        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.BuildTestGateStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"ok\"", pipelineJson);
        Assert.True(aspect.Invocations > 0, "a green build gate must let the aspect review run");
    }

    [Fact]
    public void BuildTestGate_DocOnlyDiff_IsSkippedByDiffClassifier()
    {
        Assert.False(BuildTestGateRunner.HasCodeDiff([
            "docs/research/note.md",
            "README.md",
            ".orchestrator/status.md",
        ]));
        Assert.True(BuildTestGateRunner.HasCodeDiff([
            "src/AgentTaskboard.Shared/Models/TaskModels.cs",
        ]));
        Assert.True(BuildTestGateRunner.HasCodeDiff([
            "frontend/src/app/app.component.ts",
        ]));
    }

    private sealed class CountingAspect
    {
        private int _invocations;
        public int Invocations => Volatile.Read(ref _invocations);

        public Task<string> Cli(
            string aspectId, string cli, string model, string prompt, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref _invocations);
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");
        }
    }

    private delegate Task<string> AspectCli(
        string aspectId, string cli, string model, string prompt, TimeSpan timeout, CancellationToken ct);

    private sealed class FakeBuildTestGateRunner : IBuildTestGateRunner
    {
        private readonly BuildTestGateResult _result;

        public FakeBuildTestGateRunner(BuildTestGateResult result)
        {
            _result = result;
        }

        public Task<BuildTestGateResult> RunAsync(
            string repositoryPath,
            IReadOnlyList<string>? changedFiles,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct) => Task.FromResult(_result);
    }

    private void SeedReviewJobWithDone(string slug, string status)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing for {slug}.\n");
        File.WriteAllText(Path.Combine(dir, "status.md"), status);
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_DONE]]{Environment.NewLine}");
    }

    private ReviewDecisionOrchestrator BuildOrchestrator(
        AspectCli aspectCli,
        int maxReissues,
        IBuildTestGateRunner? buildGate = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _watchPath,
            ["WatchPaths:0:RepositoryPath"] = _watchPath,
            ["ReviewDecisionOrchestrator:Enabled"] = "true",
            ["ReviewDecisionOrchestrator:CallsPerHour"] = "100",
            ["ReviewDecisionOrchestrator:AspectsEnabled"] = "true",
            ["ReviewDecisionOrchestrator:MaxParallelReviews"] = "4",
            ["ReviewDecisionOrchestrator:MaxAutoReissueAttempts"] = maxReissues.ToString(),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspectRunner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        aspectRunner.CliRunner = (aspectId, cli, model, prompt, timeout, ct) =>
            aspectCli(aspectId, cli, model, prompt, timeout, ct);

        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var mutations = new TaskMutationService(
            scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var transitions = new TaskTransitionService(scanner, stateMachine, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var taskAccess = new OrchestratorApi.Services.TaskAccess.TaskAccessService(
            scanner, mutations, stateMachine, transitions, indexCache,
            NullLogger<OrchestratorApi.Services.TaskAccess.TaskAccessService>.Instance);

        var pipelineLog = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);

        return new ReviewDecisionOrchestrator(
            scanner, stateMachine, taskAccess, chatLog, prompts, aspectRunner,
            new AutoReviewStatusSnapshot(), config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            usage: null,
            oneShotRegistry: null,
            sessions: null,
            git: null,
            pipelineLog: pipelineLog,
            lintScssRunner: null,
            buildTestGateRunner: buildGate);
    }
}
