using AgentStudio.Orchestrator;
using AgentStudio.Shared;

using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestratorTaskPromptContextComposerTests
{
    [Fact]
    public void Compose_IncludesTaskArtifactsAndLastRunOutcomeWithNamedLimits()
    {
        var detail = new TaskDetail
        {
            Info = new TaskInfo
            {
                Id = "agt-2517",
                Key = "AGT-2517",
                TaskKey = "/workspace/3-progress/agt-2517",
                Title = "Make task chat context reliable",
                State = "3-progress",
                Execution = new CliExecution
                {
                    Status = "completed",
                    RunOutcome = "success",
                    StartedAt = new DateTime(2026, 8, 8, 8, 0, 0, DateTimeKind.Utc),
                    ExitCode = 0,
                },
            },
            PromptMarkdown = "Read the active task prompt.",
            StatusMarkdown = "The status file explains the delivered fix.",
        };

        var composed = OrchestratorTaskPromptContextComposer.Compose(detail);

        Assert.Equal("AGT-2517", composed.TaskKey);
        Assert.Equal(
            ["task metadata", "prompt.md", "status.md", "last run outcome"],
            composed.IncludedBlocks);
        Assert.Contains("=== ACTIVE TASK CONTEXT ===", composed.PromptBlock);
        Assert.Contains("Key: AGT-2517", composed.PromptBlock);
        Assert.Contains("Title: Make task chat context reliable", composed.PromptBlock);
        Assert.Contains("Lane: 3-progress", composed.PromptBlock);
        Assert.Contains("--- prompt.md (limit: 1800 estimated tokens; truncated: no) ---", composed.PromptBlock);
        Assert.Contains("--- status.md (limit: 1800 estimated tokens; truncated: no) ---", composed.PromptBlock);
        Assert.Contains("Terminal outcome: success", composed.PromptBlock);
    }

    [Fact]
    public void Compose_MarksMissingStatusAndTruncatesEachBoundedArtifact()
    {
        var detail = new TaskDetail
        {
            Info = new TaskInfo
            {
                Id = "AGT-2517",
                Key = "AGT-2517",
                Title = "Bound context",
                State = "2-ready",
            },
            PromptMarkdown = new string('x', OrchestratorTaskPromptContextComposer.PromptTokenLimit * 4 + 50),
            StatusMarkdown = null,
        };

        var composed = OrchestratorTaskPromptContextComposer.Compose(detail);

        Assert.DoesNotContain("status.md", composed.IncludedBlocks);
        Assert.Contains("status.md: missing or empty", composed.PromptBlock);
        Assert.Contains("prompt.md (limit: 1800 estimated tokens; truncated: yes)", composed.PromptBlock);
        Assert.Contains("[content truncated]", composed.PromptBlock);
        Assert.Contains("No run outcome is recorded", composed.PromptBlock);
    }

    [Fact]
    public void ResolveTaskIdentity_PrefersWireTaskKeyOverRouteAndLegacyId()
    {
        Assert.True(OrchestratorContextKey.TryParse("task:Quality Studio/QS-53", out var route));
        var navigation = new ChatNavigationContext(
            CurrentTaskId: "legacy-folder-id",
            CurrentTaskKey: "QS-54");

        var identity = OrchestratorTaskPromptContextComposer.ResolveTaskIdentity(navigation, route);

        Assert.Equal("QS-54", identity);
    }

    [Fact]
    public void Compose_IncludesTheCurrentAgentPlanAsQueryableTaskContext()
    {
        var folder = Path.Combine(Path.GetTempPath(), "orchestrator-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        try
        {
            File.WriteAllText(
                Path.Combine(folder, "logs", "plan-snapshots.jsonl"),
                """{"ts":"2026-08-11T10:00:00Z","seq":1,"source":"codex/todo_list","items":[{"id":"inspect","title":"Inspect Activity","status":"done"},{"id":"integrate","title":"Integrate progress","status":"active"},{"id":"verify","title":"Run tests","status":"pending"}]}""" + Environment.NewLine);
            var detail = new TaskDetail
            {
                Info = new TaskInfo
                {
                    Id = "AGT-2641",
                    Key = "AGT-2641",
                    Title = "Live TODO list",
                    State = "3-progress",
                    FolderPath = folder,
                },
            };

            var composed = OrchestratorTaskPromptContextComposer.Compose(detail);

            Assert.Contains("current agent plan", composed.IncludedBlocks);
            Assert.Contains("CURRENT AGENT PLAN", composed.PromptBlock);
            Assert.Contains("Progress: 1/3 complete", composed.PromptBlock);
            Assert.Contains("[active] Integrate progress", composed.PromptBlock);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
