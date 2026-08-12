using Xunit;

namespace AgentStudio.Tests;

public sealed class VisualQaPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "visual-qa-policy-tests-" + Guid.NewGuid().ToString("N"));

    public VisualQaPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Applicable_RequiresAgtCardAndFrontendChange_WhenDiffIsKnown()
    {
        Assert.True(VisualQaPolicy.IsApplicable("AGT-2654", null));
        Assert.True(VisualQaPolicy.IsApplicable("AGT-2654", ["frontend/src/app/app.ts"]));
        Assert.False(VisualQaPolicy.IsApplicable("AGT-2654", ["backend/Host/Program.cs"]));
        Assert.False(VisualQaPolicy.IsApplicable("OPS-12", ["frontend/src/app/app.ts"]));
    }

    [Fact]
    public void Routes_CombineCardMetadataComponentMappingAndTaskFallback()
    {
        var routes = VisualQaPolicy.ResolveRoutes(
            "AGT-2654",
            "PROJ-002",
            "Verify /#/feed and http://localhost:4011/#/workspace/settings.",
            ["frontend/src/app/features/board/task-card.component.html"]);

        Assert.Contains(routes, route => route.Path == "/#/feed");
        Assert.Contains(routes, route => route.Path == "/#/workspace/settings");
        Assert.Contains(routes, route => route.Path == "/#/board");
        Assert.Contains(routes, route => route.Path == "/?task=AGT-2654");
        Assert.True(routes.Count <= VisualQaPolicy.MaxRoutes);
    }

    [Fact]
    public void ClearDefect_GetsExactlyOneAutomaticSteerThenEscalates()
    {
        var verdict = VisualQaPolicy.ParseVerdict("""
            {
              "status": "clear-defect",
              "summary": "The title is visibly clipped.",
              "defects": [{
                "category": "truncation",
                "description": "The task title clips after two words.",
                "screenshot": "round-001/01-task-detail--real.png"
              }]
            }
            """);

        var first = VisualQaPolicy.Decide(verdict, priorAutomaticRetries: 0);
        var second = VisualQaPolicy.Decide(verdict, priorAutomaticRetries: 1);

        Assert.Equal(VisualQaAction.RetryWithSteer, first.Action);
        Assert.Contains("truncation", first.SteerPrompt);
        Assert.Equal(VisualQaAction.EscalateToHumanReview, second.Action);
        Assert.Null(second.SteerPrompt);
    }

    [Fact]
    public void RetryReceipt_IsDurableAndCountedAcrossProcessRestarts()
    {
        var firstRound = Path.Combine(_root, "round-001");
        Directory.CreateDirectory(firstRound);
        File.WriteAllText(Path.Combine(firstRound, "verdict.json"), """
            { "action": "retry-with-steer" }
            """);
        var secondRound = Path.Combine(_root, "round-002");
        Directory.CreateDirectory(secondRound);
        File.WriteAllText(Path.Combine(secondRound, "verdict.json"), """
            { "action": "escalate-human-review" }
            """);

        Assert.Equal(1, VisualQaService.CountPriorAutomaticRetries(_root));
    }

    [Fact]
    public void MalformedOrCategoryFreeDefectVerdict_IsUnavailable()
    {
        var verdict = VisualQaPolicy.ParseVerdict("""
            { "status": "clear-defect", "summary": "bad", "defects": [] }
            """);

        Assert.Equal(VisualQaVerdictStatus.Unavailable, verdict.Status);
        Assert.Equal(
            VisualQaAction.EscalateToHumanReview,
            VisualQaPolicy.Decide(verdict, priorAutomaticRetries: 0).Action);
    }
}
