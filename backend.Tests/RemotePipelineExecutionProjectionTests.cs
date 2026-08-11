using System.Text;

using Xunit;

namespace AgentStudio.Tests;

public sealed class RemotePipelineExecutionProjectionTests
{
    [Fact]
    public void Project_RemoteLifecycle_MapsCoreReviewSkipsAndLedgerWithoutWritingParallelState()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "remote-pipeline-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(
                Path.Combine(folder, "remote-review-grade-rat-2427.md"),
                """
                ---
                type: remote-review-grade
                attemptId: "rat-2427"
                subjectId: "rsub-2427"
                receivedAt: 2026-07-30T08:20:00.0000000Z
                outcome: "Pass"
                expectedResultSha: "1111111111111111111111111111111111111111"
                actualHead: "1111111111111111111111111111111111111111"
                reportSha256: "abc"
                ---

                # Remote Review Grade

                **Outcome:** Pass

                Review Plane grade passed on the immutable result.

                ## Aspect verdicts
                """,
                Encoding.UTF8);

            var pipeline = PipelineCatalogue.Standard;
            var local = new PipelineExecutionRecord
            {
                PipelineId = pipeline.Id,
                PipelineVersion = pipeline.Version,
                Project = "Agent Taskboard",
                JobId = "agt-2427",
                StartedAt = Utc(8, 18),
                Attempt = 1,
                Steps = pipeline.AllSteps.Select(step => new PipelineStepExecution
                {
                    StepId = step.Id,
                    Kind = step.Kind,
                    Attempt = 1,
                    Status = step.Stub ? PipelineStepStatus.Planned : PipelineStepStatus.Pending,
                }).Select(step => step.StepId == PipelineCatalogue.BuildTestGateStepId
                    ? step with
                    {
                        Status = PipelineStepStatus.Passed,
                        StartedAt = Utc(8, 20),
                        CompletedAt = Utc(8, 43),
                        DurationMs = 23 * 60 * 1000,
                        Reason = "Transactional integration gate passed.",
                    }
                    : step).ToList(),
            };
            var task = new TaskInfo
            {
                Id = "agt-2427",
                TaskKey = "AGT-2427",
                ProjectName = "Agent Taskboard",
                FolderPath = folder,
                CreatedAt = Utc(7, 59),
                State = TaskStates.Completed,
                Model = "gpt-5.4",
                ThinkingLevel = "high",
            };
            var sessions = new[]
            {
                new SessionEvent { Ts = Utc(8, 0), Kind = "start", Cli = "remote-runner" },
            };
            var timeline = new[]
            {
                new TimelineEvent
                {
                    Ts = Utc(8, 10),
                    Kind = TimelineEventKinds.AgentRunFinished,
                    Actor = TimelineActors.Agent,
                    RunId = "run-2427",
                    Summary = "remote run done on agent-runner-01",
                    Details = new Dictionary<string, string>
                    {
                        ["cli"] = "remote-runner",
                        ["status"] = "done",
                    },
                },
            };
            var summary = new TaskTokenSummary
            {
                Calls = 3,
                InputTokens = 600,
                OutputTokens = 60,
                TotalTokens = 660,
                EstimatedApiCostUsd = 0.66m,
                AllModelsPriced = true,
                LastModel = "gpt-5.4",
                LastUpdate = Utc(8, 19),
                Entries =
                [
                    Call(Utc(8, 5), "agent:codex", 100, 10, 0.11m),
                    Call(Utc(8, 9), "agent:codex", 200, 20, 0.22m),
                    Call(Utc(8, 19), "orchestrator:Agent Taskboard", 300, 30, 0.33m),
                ],
            };

            var projected = RemotePipelineExecutionProjection.Project(
                local,
                pipeline,
                task,
                sessions,
                timeline,
                summary);

            Assert.NotNull(projected.Execution);
            Assert.Equal(Utc(8, 0), projected.Execution!.StartedAt);
            Assert.Equal(Utc(8, 43), projected.Execution.CompletedAt);

            var core = Step(projected.Execution, PipelineCatalogue.CoreAgentRunStepId);
            Assert.Equal(PipelineStepStatus.Passed, core.Status);
            Assert.Equal(10 * 60 * 1000, core.DurationMs);
            Assert.Equal(300, core.InputTokens);
            Assert.Equal(30, core.OutputTokens);
            Assert.Equal("high", core.ThinkingLevel);
            Assert.Contains("Remote token ledger · 2 calls", core.TokenUsageSource);

            var decision = Step(projected.Execution, PipelineCatalogue.OrchestratorDecisionStepId);
            Assert.Equal(PipelineStepStatus.Passed, decision.Status);
            Assert.Equal("pass", decision.Verdict);
            Assert.Equal(300, decision.InputTokens);
            Assert.Contains("Review Plane grade passed", decision.Reason);

            var aspect = Step(projected.Execution, PipelineCatalogue.AspectStepIds[0]);
            Assert.Equal(PipelineStepStatus.Skipped, aspect.Status);
            Assert.Equal(RemotePipelineExecutionProjection.NotApplicableReason, aspect.Reason);
            Assert.Equal(
                PipelineStepStatus.Skipped,
                Step(projected.Execution, PipelineCatalogue.LoopGuardStepId).Status);

            var gate = Step(projected.Execution, PipelineCatalogue.BuildTestGateStepId);
            Assert.Equal(PipelineStepStatus.Passed, gate.Status);
            Assert.Equal(23 * 60 * 1000, gate.DurationMs);

            // A remote lifecycle that never ran this tool remains pending. The
            // frontend may therefore label it NOT RUN truthfully.
            Assert.Equal(
                PipelineStepStatus.Pending,
                Step(projected.Execution, PipelineCatalogue.RegressionRadarStepId).Status);

            var cost = PipelineCostCalculator.SummarizeWithLedger(
                projected.Execution,
                projected.LedgerCalls);
            Assert.Equal(660, cost.TotalTokens);
            Assert.Equal(0.66m, cost.TotalCostUsd);
            Assert.Equal(
                0.33m,
                cost.Steps.Single(step =>
                    step.StepId == PipelineCatalogue.CoreAgentRunStepId).CostUsd);
            Assert.Equal(
                0.33m,
                cost.Steps.Single(step =>
                    step.StepId == PipelineCatalogue.OrchestratorDecisionStepId).CostUsd);

            // Projection only reads the grade and returns an in-memory record.
            Assert.False(File.Exists(Path.Combine(folder, PipelineExecutionLog.FileName)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Project_LocalLifecycle_IsUnchanged()
    {
        var record = new PipelineExecutionRecord
        {
            PipelineId = PipelineCatalogue.Standard.Id,
            PipelineVersion = PipelineCatalogue.Standard.Version,
            JobId = "local",
            StartedAt = Utc(8, 0),
            Steps = [],
        };

        var projected = RemotePipelineExecutionProjection.Project(
            record,
            PipelineCatalogue.Standard,
            new TaskInfo { Id = "local", CreatedAt = Utc(8, 0) },
            [],
            [],
            null);

        Assert.Same(record, projected.Execution);
        Assert.Empty(projected.LedgerCalls);
    }

    [Fact]
    public void Project_RemoteLifecycleWithoutGrade_SkipsLocalReviewsButLeavesDecisionUnreached()
    {
        var pipeline = PipelineCatalogue.Standard;
        var task = new TaskInfo
        {
            Id = "remote-without-grade",
            CreatedAt = Utc(8, 0),
            State = TaskStates.Completed,
        };

        var projected = RemotePipelineExecutionProjection.Project(
            null,
            pipeline,
            task,
            [new SessionEvent { Ts = Utc(8, 0), Kind = "start", Cli = "remote-runner" }],
            [
                new TimelineEvent
                {
                    Ts = Utc(8, 10),
                    Kind = TimelineEventKinds.AgentRunFinished,
                    Summary = "remote run done",
                    Details = new Dictionary<string, string>
                    {
                        ["cli"] = "remote-runner",
                        ["status"] = "done",
                    },
                },
            ],
            null);

        Assert.NotNull(projected.Execution);
        Assert.Equal(
            PipelineStepStatus.Skipped,
            Step(projected.Execution!, PipelineCatalogue.AspectStepIds[0]).Status);
        Assert.Equal(
            PipelineStepStatus.Pending,
            Step(projected.Execution!, PipelineCatalogue.OrchestratorDecisionStepId).Status);
    }

    [Fact]
    public void Project_RemotePostStepTimeline_ProjectsHostAndWorkspaceInsteadOfSkippingAspect()
    {
        var pipeline = PipelineCatalogue.Standard;
        var task = new TaskInfo
        {
            Id = "remote-tool-aspect",
            ProjectName = "Agent Taskboard",
            CreatedAt = Utc(8, 0),
            State = TaskStates.HumanReview,
        };
        var started = new TimelineEvent
        {
            Ts = Utc(8, 11),
            Kind = TimelineEventKinds.PostStepStarted,
            RunId = "review-1",
            Details = new Dictionary<string, string>
            {
                ["step"] = PipelineCatalogue.AspectStepIds[0],
                ["executionLocation"] = "remote",
            },
        };
        var finished = new TimelineEvent
        {
            Ts = Utc(8, 13),
            Kind = TimelineEventKinds.PostStepFinished,
            RunId = "review-1",
            Summary = "requirement fit passed on runner-host-1",
            Details = new Dictionary<string, string>
            {
                ["step"] = PipelineCatalogue.AspectStepIds[0],
                ["stepClass"] = "aspect",
                ["executionLocation"] = "remote",
                ["executor"] = "review-runner-1",
                ["host"] = "runner-host-1",
                ["workspace"] = "workspace-proof-1",
                ["status"] = "passed",
                ["durationMs"] = "120000",
                ["verdict"] = "pass",
            },
        };

        var projected = RemotePipelineExecutionProjection.Project(
            null,
            pipeline,
            task,
            [new SessionEvent { Ts = Utc(8, 0), Kind = "start", Cli = "remote-runner" }],
            [
                new TimelineEvent
                {
                    Ts = Utc(8, 10),
                    Kind = TimelineEventKinds.AgentRunFinished,
                    Summary = "remote run done",
                    Details = new Dictionary<string, string>
                    {
                        ["cli"] = "remote-runner",
                        ["status"] = "done",
                    },
                },
                started,
                finished,
            ],
            null);

        var aspect = Step(projected.Execution!, PipelineCatalogue.AspectStepIds[0]);
        Assert.Equal(PipelineStepStatus.Passed, aspect.Status);
        Assert.Equal("remote", aspect.ExecutionLocation);
        Assert.Equal("review-runner-1", aspect.ExecutorId);
        Assert.Equal("runner-host-1", aspect.HostId);
        Assert.Equal("workspace-proof-1", aspect.WorkspaceIdentity);
        Assert.Equal(120000, aspect.DurationMs);
        Assert.Equal("pass", aspect.Verdict);
    }

    [Fact]
    public void Project_LaterLocalRun_DoesNotReuseEarlierRemoteEvidence()
    {
        var record = new PipelineExecutionRecord
        {
            PipelineId = PipelineCatalogue.Standard.Id,
            PipelineVersion = PipelineCatalogue.Standard.Version,
            JobId = "remote-then-local",
            StartedAt = Utc(9, 0),
            Steps = [],
        };

        var projected = RemotePipelineExecutionProjection.Project(
            record,
            PipelineCatalogue.Standard,
            new TaskInfo { Id = "remote-then-local", CreatedAt = Utc(8, 0) },
            [
                new SessionEvent { Ts = Utc(8, 0), Kind = "start", Cli = "remote-runner" },
                new SessionEvent { Ts = Utc(9, 0), Kind = "start", Cli = "codex" },
            ],
            [
                new TimelineEvent
                {
                    Ts = Utc(8, 10),
                    Kind = TimelineEventKinds.AgentRunFinished,
                    Summary = "remote run done",
                    Details = new Dictionary<string, string> { ["cli"] = "remote-runner" },
                },
                new TimelineEvent
                {
                    Ts = Utc(9, 10),
                    Kind = TimelineEventKinds.AgentRunFinished,
                    Summary = "local run done",
                    Details = new Dictionary<string, string> { ["cli"] = "codex" },
                },
            ],
            null);

        Assert.Same(record, projected.Execution);
        Assert.Empty(projected.LedgerCalls);
    }

    private static PipelineStepExecution Step(PipelineExecutionRecord record, string id) =>
        record.Steps.Single(step => string.Equals(
            step.StepId,
            id,
            StringComparison.OrdinalIgnoreCase));

    private static TaskTokenCall Call(
        DateTime at,
        string participant,
        long input,
        long output,
        decimal cost) =>
        new()
        {
            Ts = at,
            Model = "gpt-5.4",
            ParticipantId = participant,
            InputTokens = input,
            OutputTokens = output,
            EstimatedApiCostUsd = cost,
            ModelPriced = true,
        };

    private static DateTime Utc(int hour, int minute) =>
        new(2026, 7, 30, hour, minute, 0, DateTimeKind.Utc);
}
