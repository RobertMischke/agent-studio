using System.Text;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression coverage for the AUTHORITATIVE project-state snapshot the
/// project chat injects into every turn. Before this block existed the
/// global orchestrator session pulled stale counts out of session memory:
/// the user opened "Runbook" and was told it had "21 jobs: 1 in
/// preparation, 3 ready to pick up, and 17 archived", which were actually
/// counts from the Agent Task Processor board discussed earlier in the
/// session. These tests pin the contract the prompt-builder relies on:
///
///   1. The header names the active project and the exact total.
///   2. Empty projects render as "(no tasks)" rather than dropping the block.
///   3. The state breakdown is grouped by lane and sorted by lane name.
///   4. Both the "use these exact numbers" and the "tasks not jobs"
///      instructions are present every turn.
///
/// Structural unit test - necessary, not sufficient per ADR-0007. The live
/// behavioural check is "send a fresh chat turn against the active project
/// and confirm the reply uses the right counts and the word tasks".
/// </summary>
public class OrchestratorChatProjectStateSnapshotTests
{
    private static TaskInfo Task(string project, string state) =>
        new() { Id = $"t-{state}-{System.Guid.NewGuid():N}", ProjectName = project, State = state };

    [Fact]
    public void Snapshot_NamesProjectAndExactTotal()
    {
        var tasks = new[]
        {
            Task("Runbook", "1-preparation"),
            Task("Runbook", "2-ready"),
            Task("Runbook", "2-ready"),
        };

        var sb = new StringBuilder();
        OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook", tasks);
        var rendered = sb.ToString();

        Assert.Contains("AUTHORITATIVE current state of \"Runbook\" (3 tasks total):", rendered);
    }

    [Fact]
    public void Snapshot_EmptyProject_RendersNoTasksMarker()
    {
        var sb = new StringBuilder();
        OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook", System.Array.Empty<TaskInfo>());
        var rendered = sb.ToString();

        Assert.Contains("AUTHORITATIVE current state of \"Runbook\" (0 tasks total):", rendered);
        Assert.Contains("(no tasks)", rendered);
    }

    [Fact]
    public void Snapshot_GroupsByStateAndSortsByLane()
    {
        var tasks = new[]
        {
            Task("Runbook", "5-human-review"),
            Task("Runbook", "1-preparation"),
            Task("Runbook", "2-ready"),
            Task("Runbook", "2-ready"),
            Task("Runbook", "5-human-review"),
            Task("Runbook", "5-human-review"),
        };

        var sb = new StringBuilder();
        OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook", tasks);
        var rendered = sb.ToString();

        Assert.Contains("1-preparation: 1", rendered);
        Assert.Contains("2-ready: 2", rendered);
        Assert.Contains("5-human-review: 3", rendered);

        // Order matters - the model reads sequentially, and an out-of-order
        // breakdown is a hint that grouping silently broke.
        var prep = rendered.IndexOf("1-preparation", System.StringComparison.Ordinal);
        var ready = rendered.IndexOf("2-ready", System.StringComparison.Ordinal);
        var review = rendered.IndexOf("5-human-review", System.StringComparison.Ordinal);
        Assert.True(prep < ready && ready < review,
            $"Lane order broken: prep={prep}, ready={ready}, review={review}");
    }

    [Fact]
    public void Snapshot_InstructsAgentToUseTheseExactNumbers()
    {
        var sb = new StringBuilder();
        OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook", new[] { Task("Runbook", "2-ready") });

        Assert.Contains("Use these exact numbers", sb.ToString());
        Assert.Contains("stale", sb.ToString());
    }

    [Fact]
    public void Snapshot_InstructsAgentToUseTasksVocabulary()
    {
        var sb = new StringBuilder();
        OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook", new[] { Task("Runbook", "2-ready") });
        var rendered = sb.ToString();

        Assert.Contains("\"tasks\"", rendered);
        Assert.Contains("(not \"jobs\")", rendered);
    }

    [Fact]
    public void Snapshot_HeaderRespectsProjectName()
    {
        // Pinning project-name substitution: the wrong project name in the
        // header is exactly the failure mode the snapshot was added to
        // prevent. A regression that hardcoded the name would be silent
        // without this assertion.
        var sb = new StringBuilder();
        OrchestratorChatService.AppendProjectStateSnapshot(sb, "Agent Task Processor", new[] { Task("Agent Task Processor", "3-progress") });
        var rendered = sb.ToString();

        Assert.Contains("\"Agent Task Processor\"", rendered);
        Assert.DoesNotContain("\"Runbook\"", rendered);
    }
}
