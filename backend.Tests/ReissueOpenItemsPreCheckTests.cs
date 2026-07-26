using System.Linq;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the deterministic reissue open-items pre-check: it intervenes only when
/// a run is a re-issue AND still carries open items, and is a no-op otherwise
/// (the two acceptance-criteria branches). Also covers the open-items
/// extraction (checklist boxes, aspect concerns, follow-up fallback) and the
/// escalate threshold.
/// </summary>
public class ReissueOpenItemsPreCheckTests
{
    private static ReissueOpenItemsPreCheck.PreCheckInput Reissue(
        string followUp = "", string[]? concerns = null, int priorRunCount = 1) => new()
    {
        HasReissueTag = true,
        PriorRunCompleted = true,
        PriorRunCount = priorRunCount,
        FollowUpText = followUp,
        AspectConcernSummaries = concerns ?? [],
    };

    [Fact]
    public void NotAReissue_NoTag_IsNoOp()
    {
        var decision = ReissueOpenItemsPreCheck.Evaluate(new ReissueOpenItemsPreCheck.PreCheckInput
        {
            HasReissueTag = false,
            PriorRunCompleted = true,
            FollowUpText = "- [ ] something still open",
        });

        Assert.False(decision.IsReissue);
        Assert.Equal(ReissueOpenItemsPreCheck.PreCheckAction.None, decision.Action);
        Assert.False(decision.Intervenes);
        Assert.Null(decision.ForegroundBlock);
    }

    [Fact]
    public void FirstRun_TagButNoPriorCompletedRun_IsNoOp()
    {
        // A card may carry the tag before its first attempt finishes; without a
        // completed prior run it is not yet a re-issue restart.
        var decision = ReissueOpenItemsPreCheck.Evaluate(new ReissueOpenItemsPreCheck.PreCheckInput
        {
            HasReissueTag = true,
            PriorRunCompleted = false,
            FollowUpText = "- [ ] something still open",
        });

        Assert.False(decision.IsReissue);
        Assert.Equal(ReissueOpenItemsPreCheck.PreCheckAction.None, decision.Action);
    }

    [Fact]
    public void Reissue_NoOpenItems_IsNoOp()
    {
        // AC3: a re-issue with nothing left open must not intervene.
        var decision = ReissueOpenItemsPreCheck.Evaluate(Reissue(followUp: "", concerns: []));

        Assert.True(decision.IsReissue);
        Assert.False(decision.HasOpenItems);
        Assert.Equal(ReissueOpenItemsPreCheck.PreCheckAction.None, decision.Action);
        Assert.Null(decision.ForegroundBlock);
    }

    [Fact]
    public void Reissue_WithChecklistItems_ForegroundsThem()
    {
        var followUp =
            "# Orchestrator follow-up\n\n" +
            "Please finish these:\n" +
            "- [ ] Wire the save button\n" +
            "- [x] Already done item\n" +
            "- [ ] Add the regression test\n";

        var decision = ReissueOpenItemsPreCheck.Evaluate(Reissue(followUp));

        Assert.True(decision.IsReissue);
        Assert.Equal(ReissueOpenItemsPreCheck.PreCheckAction.ForegroundOpenItems, decision.Action);
        Assert.Equal(new[] { "Wire the save button", "Add the regression test" }, decision.OpenItems);
        Assert.DoesNotContain("Already done item", decision.OpenItems);
        Assert.NotNull(decision.ForegroundBlock);
        Assert.Contains("Wire the save button", decision.ForegroundBlock);
        Assert.Contains("resolve these open items first", decision.ForegroundBlock,
            System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reissue_WithAspectConcerns_ForegroundsConcernSummaries()
    {
        var decision = ReissueOpenItemsPreCheck.Evaluate(Reissue(
            followUp: "# Orchestrator follow-up\n\nAspect review found issues.\n",
            concerns: new[] { "Missing tests for the new endpoint", "Component exceeds size budget" }));

        Assert.Equal(ReissueOpenItemsPreCheck.PreCheckAction.ForegroundOpenItems, decision.Action);
        Assert.Contains("Missing tests for the new endpoint", decision.OpenItems);
        Assert.Contains("Component exceeds size budget", decision.OpenItems);
    }

    [Fact]
    public void Reissue_PlainFollowUpReason_FallsBackToReasonAsOneItem()
    {
        // No checklist, no aspect concern, but the re-issue still has a reason -
        // it becomes the single open item so the bounce reason is never dropped.
        var decision = ReissueOpenItemsPreCheck.Evaluate(Reissue(
            followUp: "# Orchestrator follow-up\n\nThe agent never answered the decision request.\n"));

        Assert.Equal(ReissueOpenItemsPreCheck.PreCheckAction.ForegroundOpenItems, decision.Action);
        Assert.Single(decision.OpenItems);
        Assert.Equal("The agent never answered the decision request.", decision.OpenItems[0]);
    }

    [Fact]
    public void Reissue_PastBounceBudget_Escalates()
    {
        var decision = ReissueOpenItemsPreCheck.Evaluate(Reissue(
            followUp: "- [ ] Still broken\n",
            priorRunCount: ReissueOpenItemsPreCheck.EscalateAfterReissues));

        Assert.Equal(ReissueOpenItemsPreCheck.PreCheckAction.Escalate, decision.Action);
        Assert.NotNull(decision.ForegroundBlock);
        Assert.Contains("[[TASK_BLOCKED", decision.ForegroundBlock);
        Assert.Contains("human review", decision.Note!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reissue_UnderBounceBudget_ForegroundsWithoutEscalating()
    {
        var decision = ReissueOpenItemsPreCheck.Evaluate(Reissue(
            followUp: "- [ ] Still broken\n",
            priorRunCount: ReissueOpenItemsPreCheck.EscalateAfterReissues - 1));

        Assert.Equal(ReissueOpenItemsPreCheck.PreCheckAction.ForegroundOpenItems, decision.Action);
        Assert.DoesNotContain("[[TASK_BLOCKED", decision.ForegroundBlock!);
    }

    [Fact]
    public void ExtractOpenItems_DedupesAndCaps()
    {
        var manyLines = string.Join("\n",
            Enumerable.Range(0, ReissueOpenItemsPreCheck.MaxOpenItems + 5).Select(i => $"- [ ] item {i}"));
        // Add a duplicate of the first item to prove de-duplication.
        var followUp = manyLines + "\n- [ ] item 0\n";

        var items = ReissueOpenItemsPreCheck.ExtractOpenItems(followUp, null);

        Assert.Equal(ReissueOpenItemsPreCheck.MaxOpenItems, items.Count);
        Assert.Equal(items.Count, items.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ExtractOpenItems_EmptyInputs_ReturnsEmpty()
    {
        Assert.Empty(ReissueOpenItemsPreCheck.ExtractOpenItems(null, null));
        Assert.Empty(ReissueOpenItemsPreCheck.ExtractOpenItems("   ", []));
        // A follow-up that is only the heading yields no items.
        Assert.Empty(ReissueOpenItemsPreCheck.ExtractOpenItems("# Orchestrator follow-up\n", []));
    }

    [Fact]
    public void ExperimentAssignment_IsStablePerTask_AndVersioned()
    {
        var first = ReissuePromptExperiment.Assign(
            "AGT-42", 2, "deterministic-gate", "evidence-gate", 2);
        var later = ReissuePromptExperiment.Assign(
            "AGT-42", 5, "deterministic-gate", "evidence-gate", 1);

        Assert.Equal(first.Arm, later.Arm);
        Assert.Equal(first.AssignmentHash, later.AssignmentHash);
        Assert.Equal(ReissuePromptExperiment.ExperimentId, first.ExperimentId);
        Assert.Contains(first.TemplateVersion, new[]
        {
            ReissuePromptExperiment.ControlTemplateVersion,
            ReissuePromptExperiment.TreatmentTemplateVersion,
        });
        Assert.Equal(2, first.Attempt);
        Assert.Equal(5, later.Attempt);
    }

    [Fact]
    public void ExperimentLog_RecordsArmTemplateFamilyCauseAndRoute()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "reissue-prompt-experiment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var assignment = ReissuePromptExperiment.Assign(
                "Agent Studio/AGT-42", 2, "deterministic-gate", "evidence-gate", 2);

            ReissuePromptExperimentLog.Append(
                folder,
                "Agent Studio",
                "AGT-42",
                assignment,
                new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc),
                "gpt-fixed",
                "medium",
                NullLogger.Instance);

            var line = Assert.Single(File.ReadAllLines(
                Path.Combine(folder, "logs", ReissuePromptExperimentLog.FileName)));
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            Assert.Equal(assignment.Arm, root.GetProperty("arm").GetString());
            Assert.Equal(assignment.TemplateVersion, root.GetProperty("templateVersion").GetString());
            Assert.Equal("deterministic-gate", root.GetProperty("promptFamily").GetString());
            Assert.Equal("evidence-gate", root.GetProperty("cause").GetString());
            Assert.Equal("gpt-fixed", root.GetProperty("codingModel").GetString());
            Assert.Equal("medium", root.GetProperty("thinkingLevel").GetString());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TreatmentFindings_AreNumberedStructured_AndDoNotMixRawEvidence()
    {
        var rendered = ReissuePromptExperiment.BuildTreatmentFindings(new[]
        {
            "Fix the null guard in `ProjectRunner.RenderPrompt`.",
            "Add focused coverage for the missing retry branch.",
        }, escalate: false);

        Assert.Contains("1.", rendered);
        Assert.Contains("2.", rendered);
        Assert.Contains("Exact deficiency:", rendered);
        Assert.Contains("File, symbol, or artifact: `ProjectRunner.RenderPrompt`", rendered);
        Assert.Contains("Required change:", rendered);
        Assert.Contains("Focused verification or acceptance evidence:", rendered);
        Assert.DoesNotContain("Full reissue context", rendered);
    }

    [Theory]
    [InlineData("evidence-gate", "deterministic-gate")]
    [InlineData("multi-aspect-block", "model-review-finding")]
    [InlineData("no-completion-signal", "execution-protocol")]
    [InlineData("legacy-unknown", "other-reissue")]
    public void PromptFamily_IsDerivedFromTypedCause(string cause, string expected)
        => Assert.Equal(expected, ReissuePromptExperiment.ResolvePromptFamily(cause));
}
