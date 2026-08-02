using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

public sealed class RunTimelineEventFactoryTests
{
    [Fact]
    public void AgentRunFinished_LeavesCompletionFactsToTheTitleProjection()
    {
        var timelineEvent = RunTimelineEventFactory.AgentRunFinished(new CliExecution
        {
            Status = "completed",
            DurationSeconds = 247.6,
            Model = "gpt-5.6-sol",
        }, "run-1");

        Assert.Equal(TimelineEventKinds.AgentRunFinished, timelineEvent.Kind);
        Assert.Equal("Run completed after 247.6s", timelineEvent.Summary);
        Assert.Equal("completed", timelineEvent.Details["status"]);
        Assert.Equal("247.6", timelineEvent.Details["durationSeconds"]);
        Assert.DoesNotContain("cli", timelineEvent.Details.Keys);
        Assert.DoesNotContain("codex", timelineEvent.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecutionContext_EnrichesModelThinkingAndExactSourceItemsWithoutDefaults()
    {
        var described = new CliExecutionContext
        {
            Cli = "codex",
            Source = "convention",
            PermissionMode = "yolo",
            Sources =
            [
                new CliContextSource
                {
                    Kind = CliContextSourceKinds.Memory,
                    Label = "Project instructions",
                    Path = "/work/AGENTS.md",
                    Exists = true,
                },
                new CliContextSource
                {
                    Kind = CliContextSourceKinds.GlobalConfig,
                    Label = "Codex config",
                    Path = "/home/operator/.codex/config.toml",
                    Exists = true,
                },
            ],
        };

        var result = RunTimelineEventFactory.ExecutionContext("codex", new CliExecution
        {
            Model = "gpt-5.6-sol",
            ThinkingLevel = "high",
        }, described, "run-1");

        Assert.Equal("gpt-5.6-sol", result.Context.Model);
        Assert.Equal("high", result.Context.ThinkingLevel);
        Assert.Equal("Execution context: model gpt-5.6-sol, thinking high, 2 sources", result.Event.Summary);
        Assert.Equal("gpt-5.6-sol", result.Event.Details["model"]);
        Assert.Equal("high", result.Event.Details["thinkingLevel"]);
        Assert.DoesNotContain("permissionMode", result.Event.Details.Keys);
        Assert.DoesNotContain("mcp", result.Event.Details.Keys);
        Assert.DoesNotContain("yolo", result.Event.Summary, StringComparison.OrdinalIgnoreCase);

        using var sourceItems = JsonDocument.Parse(result.Event.Details["sourceItems"]);
        Assert.Equal("Project instructions", sourceItems.RootElement[0].GetProperty("label").GetString());
        Assert.Equal("memory", sourceItems.RootElement[0].GetProperty("kind").GetString());
        Assert.Equal("/work/AGENTS.md", sourceItems.RootElement[0].GetProperty("path").GetString());
    }

    [Fact]
    public void ExecutionContext_KeepsNonZeroMcpCount()
    {
        var described = new CliExecutionContext
        {
            Cli = "claude",
            Source = "init-frame",
            Sources =
            [
                new CliContextSource
                {
                    Kind = CliContextSourceKinds.Mcp,
                    Label = "GitHub",
                    Detail = "connected",
                },
            ],
        };

        var result = RunTimelineEventFactory.ExecutionContext(
            "claude",
            new CliExecution { Model = "claude-opus-4-1", ThinkingLevel = "high" },
            described,
            "run-2");

        Assert.Equal("1", result.Event.Details["mcp"]);
    }
}
