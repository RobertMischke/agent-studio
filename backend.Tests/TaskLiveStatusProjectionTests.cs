using AgentStudio.Cli;
using AgentStudio.Projects;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskLiveStatusProjectionTests
{
    private static readonly DateTime Now = new(2026, 7, 24, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_UsesCurrentAttemptRunningStepAndItsRecordedPromptProvenance()
    {
        var pipeline = Pipeline();
        var execution = new PipelineExecutionRecord
        {
            Attempt = 3,
            StartedAt = Now.AddMinutes(-2),
            Steps =
            [
                Step("core", PipelineStepStatus.Passed, Now.AddMinutes(-2), Now.AddMinutes(-1)),
                Step("aspect-tests", PipelineStepStatus.Running, Now.AddSeconds(-40)),
                Step("grade", PipelineStepStatus.Pending),
                Step("gate", PipelineStepStatus.Pending),
                Step("merge", PipelineStepStatus.Pending),
            ],
        };
        var prompts = new[]
        {
            new StepPromptEntry
            {
                At = Now.AddSeconds(-40),
                StepId = "aspect-tests",
                Cli = "codex",
                Model = "gpt-5.4-mini",
            },
        };

        var result = TaskLiveStatusProjection.Build(
            Job(), pipeline, execution, new ProjectSettings(), () => prompts, queue: null);

        Assert.Equal(3, result.Attempt);
        Assert.Equal("aspect-tests", result.ActiveStep?.StepId);
        Assert.Equal("Tests and evidence", result.ActiveStep?.DisplayName);
        Assert.Equal("codex", result.ActiveStep?.CliType);
        Assert.Equal("gpt-5.4-mini", result.ActiveStep?.Model);
        Assert.Equal(new[] { "Grade", "Gate", "Merge" }, result.NextSteps.Select(step => step.DisplayName));
    }

    [Fact]
    public void Build_NeverPromotesRunningStepFromPreviousAttemptIntoLiveStatus()
    {
        var previous = new PipelineExecutionRecord
        {
            Attempt = 6,
            StartedAt = Now.AddHours(-1),
            Steps = [Step("aspect-tests", PipelineStepStatus.Running, Now.AddHours(-1))],
        };
        var current = new PipelineExecutionRecord
        {
            Attempt = 7,
            StartedAt = Now.AddMinutes(-12),
            Steps =
            [
                Step("core", PipelineStepStatus.Passed, Now.AddMinutes(-12), Now.AddMinutes(-11)),
                Step("aspect-tests", PipelineStepStatus.Pending),
                Step("grade", PipelineStepStatus.Pending),
            ],
            PreviousAttempts = [previous],
        };

        var result = TaskLiveStatusProjection.Build(
            Job(), Pipeline(), current, new ProjectSettings(), () => [], queue: null);

        Assert.Equal(7, result.Attempt);
        Assert.Null(result.ActiveStep);
        Assert.Equal("Tests and evidence", result.NextSteps[0].DisplayName);
    }

    [Fact]
    public void Build_ReadyTaskTreatsExistingRootAsPreviousAndPreviewsFreshChain()
    {
        var completed = new PipelineExecutionRecord
        {
            Attempt = 4,
            StartedAt = Now.AddMinutes(-8),
            CompletedAt = Now.AddMinutes(-1),
            Steps =
            [
                Step("core", PipelineStepStatus.Passed, Now.AddMinutes(-8), Now.AddMinutes(-6)),
                Step("aspect-tests", PipelineStepStatus.Passed, Now.AddMinutes(-6), Now.AddMinutes(-5)),
                Step("grade", PipelineStepStatus.Passed, Now.AddMinutes(-5), Now.AddMinutes(-4)),
            ],
        };
        var ready = Job() with { State = TaskStates.Ready };

        var result = TaskLiveStatusProjection.Build(
            ready,
            Pipeline(),
            completed,
            new ProjectSettings(),
            () => [],
            new TaskLiveQueue { Kind = "runner", Position = 2 });

        Assert.Equal(5, result.Attempt);
        Assert.Null(result.ActiveStep);
        Assert.Equal("runner", result.Queue?.Kind);
        Assert.Equal(new[] { "Agent execution", "Tests and evidence", "Grade" },
            result.NextSteps.Select(step => step.DisplayName));
    }

    [Fact]
    public void Queue_ReportsAndClearsExistingReviewSlotPosition()
    {
        var queue = new AgentStudio.Runner.AutoReviewPostProcessingQueue();
        var first = Request("one", Now);
        var second = Request("two", Now.AddSeconds(1));

        Assert.True(queue.Enqueue(first));
        Assert.True(queue.Enqueue(second));
        Assert.Equal(1, queue.PositionOf("demo", "one"));
        Assert.Equal(2, queue.PositionOf("demo", "two"));

        queue.MarkStarted(first);

        Assert.Null(queue.PositionOf("demo", "one"));
        Assert.Equal(1, queue.PositionOf("demo", "two"));
    }

    private static TaskInfo Job() => new()
    {
        Id = "AGT-2315",
        TaskKey = "demo::AGT-2315",
        ProjectName = "demo",
        State = TaskStates.AutoReview,
        LastActivity = Now.AddSeconds(-20),
        CliType = "codex",
    };

    private static TaskPipeline Pipeline() => new()
    {
        Id = "test",
        DisplayName = "Test",
        Core =
        [
            Definition("core", "Agent execution", StepKind.Core),
        ],
        Post =
        [
            Definition("aspect-tests", "Tests and evidence", StepKind.Aspect),
            Definition("grade", "Grade", StepKind.Orchestrator),
            Definition("gate", "Gate", StepKind.Tool),
            Definition("merge", "Merge", StepKind.Tool),
        ],
    };

    private static PipelineStep Definition(string id, string name, StepKind kind) => new()
    {
        Id = id,
        DisplayName = name,
        Kind = kind,
        DefaultEnabled = true,
    };

    private static PipelineStepExecution Step(
        string id,
        PipelineStepStatus status,
        DateTime? started = null,
        DateTime? completed = null) => new()
    {
        StepId = id,
        Kind = id == "core" ? StepKind.Core : StepKind.Aspect,
        Status = status,
        StartedAt = started,
        CompletedAt = completed,
    };

    private static AgentStudio.Runner.AutoReviewPostProcessingRequest Request(string id, DateTime at) => new(
        ProjectName: "demo",
        JobId: id,
        WatchPath: "/workspace",
        EnqueuedAtUtc: at,
        Source: "test");
}
