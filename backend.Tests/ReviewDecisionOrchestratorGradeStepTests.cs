using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.GeneratedFiles;
using OrchestratorApi.Services.Pipeline;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Review;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// End-to-end integration for the automatic quality-grade pipeline step
/// (ASS-1657). Unlike <see cref="CodeReviewStepServiceTests"/>, which exercise
/// the service in isolation, these drive the whole orchestrator tick
/// (<c>ProcessDoneAsync</c> -> <c>RunCodeReviewGradePostStepAsync</c>) against a
/// real seeded job folder with a real <see cref="CodeReviewStepService"/> wired
/// in. They pin the three things the reissue evidence-gate called out as
/// unverified:
/// <list type="number">
///   <item>the grade step actually executes in the pipeline flow and records a
///   <c>post-code-review-grade</c> row with the parsed grade verdict;</item>
///   <item>it invokes the strong model (Claude Opus 4.8) — and demonstrably NOT
///   the cheap Haiku the four aspect reviews run on, the exact ASS-855/ASS-916
///   asymmetry this feature exists to enforce;</item>
///   <item>it stamps a single <c>code-review:grade-*</c> tag on the real task
///   and leaves a rendered <c>code-review-grade-*.md</c> behind.</item>
/// </list>
/// The CLI is stubbed so the suite runs offline; the model id the orchestrator
/// hands the service is captured so the Opus-vs-Haiku assertion is on the real
/// wired value, not a constant.
/// </summary>
public class ReviewDecisionOrchestratorGradeStepTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";
    private const string CleanStatus = "## Summary\nDone and verified.\n\nResult: Success\n\n## Open Items\nNone\n";
    private const string PassVerdict = "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]";

    public ReviewDecisionOrchestratorGradeStepTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "orch-grade-tests-" + Guid.NewGuid().ToString("N"));
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
    public async Task GradeStep_RunsEndToEnd_InvokesOpus48NotHaiku_StampsGradeTag_AndRecordsPipelineRow()
    {
        SeedReviewJobWithDone("grade-e2e", CleanStatus, taskType: TaskTypes.Chore, withScreenshot: true);

        string? gradeModel = null;
        string? aspectModel = null;
        var orchestrator = BuildOrchestrator(
            aspectStub: (aspectId, model) =>
            {
                aspectModel = model;
                return PassVerdict;
            },
            gradeCli: (model) =>
            {
                gradeModel = model;
                return "Solid, one small gap.\n[[CODE_REVIEW_GRADE: grade=B; summary=Solid with a minor gap.]]\n[[TASK_DONE]]";
            },
            maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // The clean chore with a screenshot is accepted: it lands in 5-human-review,
        // carrying the grade evidence written before the lane move.
        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "grade-e2e");
        Assert.True(Directory.Exists(folder), "a clean accepted task should land in 5-human-review");

        // 1. The grade step ran on the strong model and demonstrably NOT on the
        //    cheap aspect model. This is the ASS-855(Haiku)/ASS-916(Opus) tension
        //    the feature resolves: aspects stay cheap, the grade goes Opus 4.8.
        Assert.Equal("claude-opus-4-8", gradeModel);
        Assert.Equal("claude-haiku-4-5", aspectModel);
        Assert.NotEqual(aspectModel, gradeModel);

        // 2. The pipeline records a post-code-review-grade row with the parsed grade.
        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.CodeReviewGradeStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"B\"", pipelineJson);

        // 3. Exactly one code-review:grade-* tag is stamped on the real task.json.
        var tags = ReadTags(folder);
        Assert.Contains("code-review:grade-b", tags);
        Assert.Single(tags, t => t.StartsWith("code-review:grade-"));

        // The rendered grade markdown is left behind for the detail pane.
        var gradeMd = Directory.GetFiles(folder, "code-review-grade-*.md");
        Assert.Single(gradeMd);
        var md = File.ReadAllText(gradeMd[0]);
        Assert.Contains("type: code-review-grade", md);
        Assert.Contains("Quality Grade: B", md);
    }

    [Fact]
    public async Task GradeStep_DefaultModelIsConfigurable_OverrideFlowsThrough()
    {
        SeedReviewJobWithDone("grade-config", CleanStatus, taskType: TaskTypes.Chore, withScreenshot: true);

        string? gradeModel = null;
        var orchestrator = BuildOrchestrator(
            aspectStub: (_, _) => PassVerdict,
            gradeCli: (model) =>
            {
                gradeModel = model;
                return "[[CODE_REVIEW_GRADE: grade=A; summary=ok]]\n[[TASK_DONE]]";
            },
            maxReissues: 3,
            extraConfig: new Dictionary<string, string?>
            {
                ["CodeReviewStep:DefaultModel"] = "claude-opus-4-7",
            });

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // The per-deployment override is honored over the Opus-4.8 default, so a
        // deployment can dial the grade model without code changes.
        Assert.Equal("claude-opus-4-7", gradeModel);
    }

    [Fact]
    public async Task GradeStep_DGrade_RecordsFailedRow_AndStampsGradeDTag()
    {
        SeedReviewJobWithDone("grade-d", CleanStatus, taskType: TaskTypes.Chore, withScreenshot: true);

        var orchestrator = BuildOrchestrator(
            aspectStub: (_, _) => PassVerdict,
            gradeCli: (_) => "Redundant, not wired.\n[[CODE_REVIEW_GRADE: grade=D; summary=Reimplements existing code.]]\n[[TASK_DONE]]",
            maxReissues: 3);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "grade-d");
        Assert.True(Directory.Exists(folder));

        // A D grade is reporting evidence, never a lane gate: it records a Failed
        // row so it stands out in the Overview, but the accept still proceeds.
        var pipelineJson = File.ReadAllText(Path.Combine(folder, PipelineExecutionLog.FileName));
        Assert.Contains("\"stepId\": \"" + PipelineCatalogue.CodeReviewGradeStepId + "\"", pipelineJson);
        Assert.Contains("\"verdict\": \"D\"", pipelineJson);

        Assert.Contains("code-review:grade-d", ReadTags(folder));
    }

    private void SeedReviewJobWithDone(
        string slug,
        string status,
        string taskType,
        bool withScreenshot = false)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\",\"taskType\":\"{taskType}\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing for {slug}.\n");
        File.WriteAllText(Path.Combine(dir, "status.md"), status);
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_DONE]]{Environment.NewLine}");

        if (withScreenshot)
        {
            var shots = Path.Combine(dir, "results", "playwright", "fix-spec");
            Directory.CreateDirectory(shots);
            File.WriteAllBytes(Path.Combine(shots, "after.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        }
    }

    private ReviewDecisionOrchestrator BuildOrchestrator(
        Func<string, string, string> aspectStub,
        Func<string, string> gradeCli,
        int maxReissues,
        IDictionary<string, string?>? extraConfig = null)
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
        if (extraConfig != null)
        {
            foreach (var kv in extraConfig) dict[kv.Key] = kv.Value;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspectRunner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        aspectRunner.CliRunner = (aspectId, _, model, _, _, _) => Task.FromResult(aspectStub(aspectId, model));

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

        var codeReviewStep = new CodeReviewStepService(
            prompts,
            NullLogger<CodeReviewStepService>.Instance);
        codeReviewStep.CliRunner = (_, model, _, _, _) => Task.FromResult(gradeCli(model));

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
            codeReviewStep: codeReviewStep);
    }

    private static List<string> ReadTags(string folder)
    {
        var jobJsonPath = Path.Combine(folder, "task.json");
        if (!File.Exists(jobJsonPath)) return new List<string>();
        var json = File.ReadAllText(jobJsonPath);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("tags", out var tagsEl)) return new List<string>();
        if (tagsEl.ValueKind != System.Text.Json.JsonValueKind.Array) return new List<string>();
        var list = new List<string>();
        foreach (var t in tagsEl.EnumerateArray())
        {
            if (t.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = t.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }
}
