using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Endpoints.Jobs;
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
///
/// ADR-0025 routing: tasks land in <c>4-auto-review</c>; reissue moves
/// back to <c>3-progress</c>; accept-as-done and escalate both promote
/// to <c>5-human-review</c>.
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
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.AutoReview, "fix-layout")));

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
    public async Task Escalate_PromotesToHumanReview_WritesSupervisorBanner_AndCreatesIntakeTask()
    {
        SeedReviewJobWithNeedsInput("auth-rewrite", "use OAuth or magic-link?");
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=escalate; reason=Needs strategic call.]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // ADR-0025: escalate moves the task to 5-human-review (the user
        // sees one lane that means "needs me"); the legacy auto-review
        // folder must no longer hold the job.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.HumanReview, "auth-rewrite")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.AutoReview, "auth-rewrite")));

        var log = ReadCliLog(JobStates.HumanReview, "auth-rewrite");
        Assert.Contains("[supervisor]", log);
        Assert.Contains("[escalate]", log);
        Assert.Contains("strategic call", log);
        Assert.Contains("5-human-review", log);

        var intake = Path.Combine(_watchPath, JobStates.Preparation, "human-decision-needed-auth-rewrite");
        Assert.True(Directory.Exists(intake));
        Assert.True(File.Exists(Path.Combine(intake, "job.json")));
        Assert.True(File.Exists(Path.Combine(intake, "prompt.md")));

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Escalate, record.Kind);
    }

    [Fact]
    public async Task AcceptAsDone_PromotesToHumanReview_NotDirectlyToCompleted_AndJournalsRecord()
    {
        SeedReviewJobWithNeedsInput("doc-edit", "should I add screenshots?");
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=accept-as-done; reason=Work matches contract; question is courtesy.]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // ADR-0025 contract: accept-as-done routes to 5-human-review,
        // never directly to 6-completed. The user always confirms.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.HumanReview, "doc-edit")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Completed, "doc-edit")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.AutoReview, "doc-edit")));

        var log = ReadCliLog(JobStates.HumanReview, "doc-edit");
        Assert.Contains("[orchestrator]", log);
        Assert.Contains("[decision]", log);
        Assert.Contains("accept-as-done", log);
        Assert.Contains("5-human-review", log);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.AcceptAsDone, record.Kind);
    }

    [Fact]
    public async Task DoesNotReprocess_OnceOrchestratorLineIsPresent()
    {
        SeedReviewJobWithNeedsInput("already-answered", "anything?");
        // Append an orchestrator follow-up so the parser treats the chain as resolved.
        var logPath = JobPathLog(JobStates.AutoReview, "already-answered");
        File.AppendAllText(logPath,
            $"\n[12:00:30.000] [orchestrator] [reissue] previously answered{Environment.NewLine}");

        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "[[ORCHESTRATOR_DECISION: action=reissue; reason=should not run]]",
            onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.AutoReview, "already-answered")));
        Assert.False(File.Exists(ReviewDecisionLog.DecisionsFile(_workspace, Project)));
    }

    [Fact]
    public async Task NoOp_WithRealPrompt_ReissuesWithSharpenedFraming_WithoutSpendingFastModel()
    {
        SeedReviewJobWithNoOp("flesh-out-readme",
            title: "Add product overview to README",
            promptBody: "# Add product overview\n\nWrite a clear two-paragraph product overview at the top of README.md describing the kanban board, the agent loop, and the watch-path model.\n");
        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // No fast-model call should have been made.
        Assert.Equal(0, calls);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "flesh-out-readme")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.AutoReview, "flesh-out-readme")));

        var log = ReadCliLog(JobStates.Progress, "flesh-out-readme");
        Assert.Contains("[orchestrator]", log);
        Assert.Contains("[reissue]", log);
        Assert.Contains("NOOP recovery", log);

        var followUpPath = Path.Combine(_watchPath, JobStates.Progress, "flesh-out-readme", "orchestrator-follow-up.md");
        Assert.True(File.Exists(followUpPath));
        var followUp = File.ReadAllText(followUpPath);
        Assert.Contains("Do not reply 'task done'", followUp);
        Assert.Contains("Add product overview", followUp);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Reissue, record.Kind);
    }

    [Fact]
    public async Task NoOp_WithEmptyPrompt_PromotesToHumanReview_AndCreatesIntake()
    {
        SeedReviewJobWithNoOp("placeholder-task",
            title: "TODO: fill in",
            promptBody: "# TODO\n\nplaceholder\n");
        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);

        // ADR-0025: NOOP escalations also move to 5-human-review.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.HumanReview, "placeholder-task")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.AutoReview, "placeholder-task")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "placeholder-task")));

        var log = ReadCliLog(JobStates.HumanReview, "placeholder-task");
        Assert.Contains("[supervisor]", log);
        Assert.Contains("[escalate]", log);
        Assert.Contains("empty or placeholder", log);

        var intake = Path.Combine(_watchPath, JobStates.Preparation, "human-decision-needed-placeholder-task");
        Assert.True(Directory.Exists(intake));
        Assert.True(File.Exists(Path.Combine(intake, "job.json")));
        var intakePrompt = File.ReadAllText(Path.Combine(intake, "prompt.md"));
        Assert.Contains("[[TASK_NOOP]]", intakePrompt);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Escalate, record.Kind);
    }

    [Fact]
    public async Task NoOp_AfterReissueBudgetExhausted_EscalatesInsteadOfReissuing()
    {
        SeedReviewJobWithNoOp("repeated-noop",
            title: "Implement caching layer",
            promptBody: "# Implement caching\n\nAdd an LRU cache in front of the JobScannerService.GetJobs call to avoid the O(N) disk scan on every poll.\n");

        // Pre-populate the journal with two prior reissues for this job
        // (default budget = 2). Any further reissue should escalate.
        for (var i = 0; i < 2; i++)
        {
            ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow.AddMinutes(-10 + i),
                JobId: "repeated-noop",
                Project: Project,
                Kind: ReviewDecisionKind.Reissue,
                Reason: "prior reissue",
                Prompt: "(test seed)",
                Response: "(test seed)",
                FollowUp: "(test seed)"));
        }

        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        // Budget-exhausted NOOP must escalate to 5-human-review (ADR-0025).
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.HumanReview, "repeated-noop")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.AutoReview, "repeated-noop")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "repeated-noop")));

        var intake = Path.Combine(_watchPath, JobStates.Preparation, "human-decision-needed-repeated-noop");
        Assert.True(Directory.Exists(intake));

        var log = ReadCliLog(JobStates.HumanReview, "repeated-noop");
        Assert.Contains("[supervisor]", log);
        Assert.Contains("[escalate]", log);
        Assert.Contains("prior orchestrator reissue", log);

        // Three records: two seed reissues, plus this tick's escalate.
        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Equal(3, records.Count);
        Assert.Equal(ReviewDecisionKind.Escalate, records[^1].Kind);
    }

    [Fact]
    public async Task Blocked_PromotesToHumanReview_AndCreatesIntake_WithoutSpendingFastModel()
    {
        SeedReviewJobWithBlocked("bug-commit-hangs", "awaiting user decision A/B/C");
        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // BLOCKED is deterministic: no fast-model call.
        Assert.Equal(0, calls);

        // ADR-0025: BLOCKED escalations move the task to 5-human-review.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.HumanReview, "bug-commit-hangs")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.AutoReview, "bug-commit-hangs")));
        var intake = Path.Combine(_watchPath, JobStates.Preparation, "human-decision-needed-bug-commit-hangs");
        Assert.True(Directory.Exists(intake));
        var intakePrompt = File.ReadAllText(Path.Combine(intake, "prompt.md"));
        Assert.Contains("[[TASK_BLOCKED]]", intakePrompt);

        var log = ReadCliLog(JobStates.HumanReview, "bug-commit-hangs");
        Assert.Contains("[supervisor]", log);
        Assert.Contains("[escalate]", log);
        Assert.Contains("BLOCKED", log);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Escalate, record.Kind);
        Assert.Contains("awaiting user decision A/B/C", record.Reason);
    }

    [Fact]
    public async Task Blocked_DoesNotReprocess_OnceSupervisorEscalateLineIsPresent()
    {
        SeedReviewJobWithBlocked("already-escalated", "needs human");
        // Append a supervisor escalate so the parser treats the chain as resolved.
        var logPath = JobPathLog(JobStates.AutoReview, "already-escalated");
        File.AppendAllText(logPath,
            $"[12:00:30.000] [supervisor] [escalate] previously escalated{Environment.NewLine}");

        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.False(File.Exists(ReviewDecisionLog.DecisionsFile(_workspace, Project)));
    }

    [Fact]
    public async Task BootSweep_ProcessesPreExistingAutoReviewItem_Immediately()
    {
        // The audit case: a BLOCKED job that landed in 4-auto-review while
        // the backend was offline and is now stuck. The boot sweep
        // (one-shot call to TickOnceAsync before the recurring loop
        // starts) must pick it up on the very first tick rather than
        // waiting for the 30-second interval.
        SeedReviewJobWithBlocked("audit-stuck-blocked", "awaiting user input");

        var orchestrator = BuildOrchestrator(cliResponse: "");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Escalate, record.Kind);
        Assert.Equal("audit-stuck-blocked", record.JobId);
    }

    [Fact]
    public void BuildOrchestratorVerdictLookup_ReturnsLatestKindPerJob()
    {
        // Two journal entries for the same job: the latest one wins.
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow.AddMinutes(-5),
            JobId: "job-a", Project: Project,
            Kind: ReviewDecisionKind.Reissue,
            Reason: "first try", Prompt: "p", Response: "r", FollowUp: "f"));
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: "job-a", Project: Project,
            Kind: ReviewDecisionKind.Escalate,
            Reason: "gave up", Prompt: "p", Response: "r", FollowUp: ""));
        // A separate job with one acceptAsDone record.
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: "job-b", Project: Project,
            Kind: ReviewDecisionKind.AcceptAsDone,
            Reason: "looks good", Prompt: "p", Response: "r", FollowUp: ""));

        var jobs = new[]
        {
            new JobInfo { Id = "job-a", JobKey = $"{_watchPath}::job-a", ProjectName = Project, WatchPath = _watchPath, State = JobStates.HumanReview },
            new JobInfo { Id = "job-b", JobKey = $"{_watchPath}::job-b", ProjectName = Project, WatchPath = _watchPath, State = JobStates.HumanReview },
            new JobInfo { Id = "job-c", JobKey = $"{_watchPath}::job-c", ProjectName = Project, WatchPath = _watchPath, State = JobStates.AutoReview },
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();

        var lookup = JobEndpointHelpers.BuildOrchestratorVerdictLookup(jobs, config);

        Assert.Equal("escalate", lookup[$"{_watchPath}::job-a"]);
        Assert.Equal("accept",   lookup[$"{_watchPath}::job-b"]);
        Assert.False(lookup.ContainsKey($"{_watchPath}::job-c"));
    }

    [Fact]
    public void IsPromptUsable_SeparatesRealPromptsFromPlaceholders()
    {
        // Heuristic guard: any future change to the placeholder rules
        // must keep these classifications stable so the NOOP branch logic
        // continues to route correctly.
        Assert.False(ReviewDecisionOrchestrator.IsPromptUsable("", "Real body with twenty plus characters of detail."));
        Assert.False(ReviewDecisionOrchestrator.IsPromptUsable("Real title", ""));
        Assert.False(ReviewDecisionOrchestrator.IsPromptUsable("TODO: fill in", "# TODO\n\nplaceholder\n"));
        Assert.False(ReviewDecisionOrchestrator.IsPromptUsable("Real title", "# Heading only\n"));
        Assert.True(ReviewDecisionOrchestrator.IsPromptUsable("Add caching layer",
            "# Add caching\n\nWrap GetJobs in an LRU cache to avoid the disk scan on every poll.\n"));
    }

    private void SeedReviewJobWithBlocked(string slug, string reason)
    {
        var dir = Path.Combine(_watchPath, JobStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{JobStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing.\n");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_BLOCKED: {reason}]]{Environment.NewLine}");
    }

    private void SeedReviewJobWithNoOp(string slug, string title, string promptBody)
    {
        var dir = Path.Combine(_watchPath, JobStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":{System.Text.Json.JsonSerializer.Serialize(title)},\"state\":\"{JobStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_NOOP]]{Environment.NewLine}");
    }

    private string JobPathLog(string state, string slug) =>
        Path.Combine(_watchPath, state, slug, "logs", "cli-output.log");

    private string ReadCliLog(string state, string slug) =>
        File.ReadAllText(JobPathLog(state, slug));

    private void SeedReviewJobWithNeedsInput(string slug, string reason)
    {
        var dir = Path.Combine(_watchPath, JobStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{JobStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
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
