using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Drives the <see cref="ReviewDecisionOrchestrator"/> tick against a temp
/// workspace. The fast-model CLI is stubbed: tests inject the response the
/// orchestrator should "receive", then assert the lane transition, the
/// chat-log line, the decision-journal entry, and (for escalate) the
/// human-decision intake creation.
/// </summary>
public class ReviewDecisionOrchestratorTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public ReviewDecisionOrchestratorTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in JobStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Reissue_TransitionsBackToProgress_AppendsChatLog_AndJournalsRecord()
    {
        SeedReviewJobWithNeedsInput("fix-layout", "which column is primary?");
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=reissue; reason=Roadmap names option A.]]\n[[TASK_DONE]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "fix-layout")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Review, "fix-layout")));

        var log = ReadCliLog(JobStates.Progress, "fix-layout");
        Assert.Contains("[orchestrator]", log);
        Assert.Contains("[reissue]", log);
        Assert.Contains("Roadmap names option A.", log);

        var followUp = Path.Combine(_watchPath, JobStates.Progress, "fix-layout", "orchestrator-follow-up.md");
        Assert.True(File.Exists(followUp));

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Reissue, record.Kind);
        Assert.Equal("fix-layout", record.JobId);
        Assert.Contains("option A", record.Reason);
        Assert.False(string.IsNullOrEmpty(record.FollowUp));
    }

    [Fact]
    public async Task Escalate_LeavesJobInReview_WritesSupervisorBanner_AndCreatesIntakeTask()
    {
        SeedReviewJobWithNeedsInput("auth-rewrite", "use OAuth or magic-link?");
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=escalate; reason=Needs strategic call.]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Review, "auth-rewrite")));

        var log = ReadCliLog(JobStates.Review, "auth-rewrite");
        Assert.Contains("[supervisor]", log);
        Assert.Contains("[escalate]", log);
        Assert.Contains("strategic call", log);

        var intake = Path.Combine(_watchPath, JobStates.Preparation, "human-decision-needed-auth-rewrite");
        Assert.True(Directory.Exists(intake));
        Assert.True(File.Exists(Path.Combine(intake, "job.json")));
        Assert.True(File.Exists(Path.Combine(intake, "prompt.md")));

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Escalate, record.Kind);
    }

    [Fact]
    public async Task AcceptAsDone_TransitionsToCompleted_AndJournalsRecord()
    {
        SeedReviewJobWithNeedsInput("doc-edit", "should I add screenshots?");
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=accept-as-done; reason=Work matches contract; question is courtesy.]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Completed, "doc-edit")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Review, "doc-edit")));

        var log = ReadCliLog(JobStates.Completed, "doc-edit");
        Assert.Contains("[orchestrator]", log);
        Assert.Contains("[decision]", log);
        Assert.Contains("accept-as-done", log);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.AcceptAsDone, record.Kind);
    }

    [Fact]
    public async Task DoesNotReprocess_OnceOrchestratorLineIsPresent()
    {
        SeedReviewJobWithNeedsInput("already-answered", "anything?");
        // Append an orchestrator follow-up so the parser treats the chain as resolved.
        var logPath = JobPathLog(JobStates.Review, "already-answered");
        File.AppendAllText(logPath,
            $"\n[12:00:30.000] [orchestrator] [reissue] previously answered{Environment.NewLine}");

        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "[[ORCHESTRATOR_DECISION: action=reissue; reason=should not run]]",
            onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Review, "already-answered")));
        Assert.False(File.Exists(ReviewDecisionLog.DecisionsFile(_workspace, Project)));
    }

    private string JobPathLog(string state, string slug) =>
        Path.Combine(_watchPath, state, slug, "logs", "cli-output.log");

    private string ReadCliLog(string state, string slug) =>
        File.ReadAllText(JobPathLog(state, slug));

    private void SeedReviewJobWithNeedsInput(string slug, string reason)
    {
        var dir = Path.Combine(_watchPath, JobStates.Review, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{JobStates.Review}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing.\n");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_NEEDS_INPUT: {reason}]]{Environment.NewLine}");
    }

    private ReviewDecisionRecord ReadOnlyDecisionRecord()
    {
        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Single(records);
        return records[0];
    }

    private ReviewDecisionOrchestrator BuildOrchestrator(string cliResponse, Action? onCall = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["ReviewDecisionOrchestrator:Enabled"] = "true",
                ["ReviewDecisionOrchestrator:CallsPerHour"] = "100"
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var stateMachine = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var orchestrator = new ReviewDecisionOrchestrator(
            scanner, stateMachine, chatLog, prompts, config,
            NullLogger<ReviewDecisionOrchestrator>.Instance);
        orchestrator.CliRunner = (cli, model, prompt, timeout, ct) =>
        {
            onCall?.Invoke();
            return Task.FromResult(cliResponse);
        };
        return orchestrator;
    }
}
