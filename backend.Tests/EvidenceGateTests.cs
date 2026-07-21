
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pure-policy coverage for <see cref="EvidenceGate"/> (ASS-764): the visual-
/// evidence requirement classifier, the on-disk evidence-presence probe, and
/// the reissue/escalate decision that replaces a bare accept-with-concerns for
/// unverified UI/bug work or an unclean tests-and-evidence aspect.
/// </summary>
public class EvidenceGateTests : IDisposable
{
    private readonly string _folder;

    public EvidenceGateTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "evidence-gate-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData("bug", true)]
    [InlineData("Bug", true)]
    [InlineData("feature", false)]
    [InlineData("chore", false)]
    [InlineData(null, false)]
    public void MatchesUiHeuristic_BugTaskTypeAlwaysMatches(string? taskType, bool expected)
    {
        Assert.Equal(expected,
            EvidenceGate.MatchesUiHeuristic(taskType, tags: null, title: "Do the thing"));
    }

    [Theory]
    [InlineData("Fix the Markdown rendering in the protocol panel", true)]
    [InlineData("Restyle the primary button component", true)]
    [InlineData("Add a Playwright e2e for the board", true)]
    [InlineData("Refactor the token aggregation service", false)]
    [InlineData("Cache the disk scan in the scanner", false)]
    public void MatchesUiHeuristic_TitleSignalWords(string title, bool expected)
    {
        Assert.Equal(expected,
            EvidenceGate.MatchesUiHeuristic(taskType: "chore", tags: null, title: title));
    }

    [Fact]
    public void MatchesUiHeuristic_FrontendTagTriggers()
    {
        Assert.True(EvidenceGate.MatchesUiHeuristic(
            taskType: "chore", tags: new[] { "area-backend", "frontend" }, title: "Some work"));
        Assert.False(EvidenceGate.MatchesUiHeuristic(
            taskType: "chore", tags: new[] { "area-backend" }, title: "Some work"));
    }

    [Theory]
    // Backend, docs, config, e2e specs, and non-component frontend TS carry no
    // rendered surface: a screenshot is impossible, so they must not require one.
    [InlineData("backend/Features/Runner/EvidenceGate.cs", false)]
    [InlineData("docs/plans/AGT-2195-planning.md", false)]
    [InlineData("frontend/src/app/services/task.service.ts", false)]
    [InlineData("frontend/src/app/features/board/state/board-filters.service.ts", false)]
    [InlineData("frontend/e2e/board.spec.ts", false)]
    [InlineData("frontend/src/app/components/task-card/task-card.component.spec.ts", false)]
    [InlineData("frontend/src/app/models/task.model.ts", false)]
    // Templates, styles, and component/directive TypeScript are UI surface.
    [InlineData("frontend/src/app/features/board/components/task-card/task-card.component.html", true)]
    [InlineData("frontend/src/app/features/board/components/task-card/task-card.component.scss", true)]
    [InlineData("frontend/src/app/features/board/components/task-card/task-card.component.ts", true)]
    [InlineData("frontend/src/app/components/auth-gate/auth-gate.ts", true)]
    [InlineData("frontend/src/styles.scss", true)]
    // Windows-style separators and a leading slash must normalise the same way.
    [InlineData("frontend\\src\\app\\components\\auth-gate\\auth-gate.html", true)]
    public void ChangeSetTouchesUi_ClassifiesPath(string path, bool expected)
    {
        Assert.Equal(expected, EvidenceGate.ChangeSetTouchesUi(new[] { path }));
    }

    [Fact]
    public void ChangeSetTouchesUi_NullOrEmpty_False()
    {
        Assert.False(EvidenceGate.ChangeSetTouchesUi(null));
        Assert.False(EvidenceGate.ChangeSetTouchesUi(Array.Empty<string>()));
    }

    [Fact]
    public void RequiresVisualEvidence_BackendBugWithoutUiDiff_NotRequired()
    {
        // AGT-2177: a backend feature/bug whose change-set never touches the UI
        // must not be blocked for a screenshot it cannot produce.
        var changed = new[] { "backend/Features/Runner/RunnerEndpoints.cs", "backend/Program.cs" };

        Assert.False(EvidenceGate.RequiresVisualEvidence(
            taskType: "bug", tags: new[] { "area-backend" }, title: "Fix the runner lease API", changed));
    }

    [Fact]
    public void RequiresVisualEvidence_PlanningDocDespiteUiWordInTitle_NotRequired()
    {
        // AGT-2195: a planning doc whose title carries a UI signal word ("layout")
        // but whose change-set is docs-only must not demand a screenshot.
        var changed = new[] { "docs/mockups/dashboard-layout.md" };

        Assert.False(EvidenceGate.RequiresVisualEvidence(
            taskType: "feature", tags: null, title: "Plan the new dashboard layout", changed));
    }

    [Fact]
    public void RequiresVisualEvidence_RealUiBugWithUiDiff_StillRequired()
    {
        var changed = new[]
        {
            "backend/Features/Runner/EvidenceGate.cs",
            "frontend/src/app/features/board/components/task-card/task-card.component.html",
        };

        Assert.True(EvidenceGate.RequiresVisualEvidence(
            taskType: "bug", tags: null, title: "Card badge overlaps the title", changed));
    }

    [Fact]
    public void RequiresVisualEvidence_UnknownChangeSet_FallsBackToHeuristic()
    {
        // A null change-set (diff probe failed / remote run) must not silently
        // drop the gate: a bug still requires visual proof, a chore still does not.
        Assert.True(EvidenceGate.RequiresVisualEvidence(
            taskType: "bug", tags: null, title: "Something broke", changedFiles: null));
        Assert.False(EvidenceGate.RequiresVisualEvidence(
            taskType: "chore", tags: null, title: "Tidy the scanner", changedFiles: null));
    }

    [Fact]
    public void RequiresVisualEvidence_NonUiTaskWithUiDiff_NotRequired()
    {
        // The heuristic gates first: a chore that happens to touch a template is
        // not forced into a screenshot demand on the title/tag heuristic alone.
        var changed = new[] { "frontend/src/app/app.html" };

        Assert.False(EvidenceGate.RequiresVisualEvidence(
            taskType: "chore", tags: null, title: "Bump the copyright year", changed));
    }

    [Fact]
    public void HasVisualEvidence_NoResultsFolder_False()
    {
        Assert.False(EvidenceGate.HasVisualEvidence(_folder));
    }

    [Fact]
    public void HasVisualEvidence_ScreenshotPresent_True()
    {
        var nested = Path.Combine(_folder, "results", "playwright", "board-spec");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "after.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        Assert.True(EvidenceGate.HasVisualEvidence(_folder));
    }

    [Fact]
    public void HasVisualEvidence_ZeroByteScreenshot_False()
    {
        var nested = Path.Combine(_folder, "results", "playwright", "board-spec");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "after.png"), Array.Empty<byte>());

        Assert.False(EvidenceGate.HasVisualEvidence(_folder));
    }

    [Fact]
    public void HasVisualEvidence_EmptyResultsFolder_False()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "results"));
        File.WriteAllText(Path.Combine(_folder, "results", "notes.txt"), "not an image");

        Assert.False(EvidenceGate.HasVisualEvidence(_folder));
    }

    [Fact]
    public void HasVisualEvidence_ReviewEvidenceLogPresent_True()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "results"));
        File.WriteAllText(Path.Combine(_folder, "results", "review-evidence.jsonl"),
            "{\"id\":\"e1\",\"title\":\"Fix verified via screenshot\"}\n");

        Assert.True(EvidenceGate.HasVisualEvidence(_folder));
    }

    [Fact]
    public void HasVisualEvidence_EmptyReviewEvidenceLog_False()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "results"));
        File.WriteAllText(Path.Combine(_folder, "results", "review-evidence.jsonl"), "");

        Assert.False(EvidenceGate.HasVisualEvidence(_folder));
    }

    [Fact]
    public void HasVisualEvidence_WhitespaceReviewEvidenceLog_False()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "results"));
        File.WriteAllText(Path.Combine(_folder, "results", "review-evidence.jsonl"), " \r\n\t\r\n");

        Assert.False(EvidenceGate.HasVisualEvidence(_folder));
    }

    [Fact]
    public void TestsAndEvidenceAspectId_MatchesAspectRunnerCatalogue()
    {
        Assert.True(
            AspectRunnerService.Catalogue.TryGetValue(EvidenceGate.TestsAndEvidenceAspectId, out var definition),
            "EvidenceGate.TestsAndEvidenceAspectId must match the AspectRunnerService catalogue id.");
        Assert.Equal(EvidenceGate.TestsAndEvidenceAspectId, definition.Id);
    }

    [Fact]
    public void Evaluate_MissingVisualEvidence_BudgetLeft_Reissues()
    {
        var report = ReportWith(("tests-and-evidence", AspectStatus.Pass, "ok"));

        var decision = EvidenceGate.Evaluate(
            requiresVisualEvidence: true, hasVisualEvidence: false,
            report, priorReissues: 0, maxReissues: 3);

        Assert.Equal(EvidenceGate.EvidenceGateAction.Reissue, decision.Action);
        Assert.True(decision.MissingVisualEvidence);
        Assert.Single(decision.Findings);
    }

    [Fact]
    public void Evaluate_TestsAndEvidenceConcerns_WithEvidence_Reissues()
    {
        var report = ReportWith(
            ("code-quality", AspectStatus.Pass, "ok"),
            ("tests-and-evidence", AspectStatus.Concerns, "Build failed with error CS0246; no test added."));

        var decision = EvidenceGate.Evaluate(
            requiresVisualEvidence: true, hasVisualEvidence: true,
            report, priorReissues: 0, maxReissues: 3);

        Assert.Equal(EvidenceGate.EvidenceGateAction.Reissue, decision.Action);
        Assert.False(decision.MissingVisualEvidence);
        Assert.Single(decision.Findings);
        Assert.Contains("CS0246", decision.Findings[0]);
    }

    [Fact]
    public void Evaluate_CleanRunWithEvidence_Passes()
    {
        var report = ReportWith(("tests-and-evidence", AspectStatus.Pass, "ok"));

        var decision = EvidenceGate.Evaluate(
            requiresVisualEvidence: true, hasVisualEvidence: true,
            report, priorReissues: 0, maxReissues: 3);

        Assert.Equal(EvidenceGate.EvidenceGateAction.Pass, decision.Action);
        Assert.False(decision.IsBlocking);
    }

    [Fact]
    public void Evaluate_NonVisualTaskCleanAspects_Passes()
    {
        var report = ReportWith(("tests-and-evidence", AspectStatus.Pass, "ok"));

        var decision = EvidenceGate.Evaluate(
            requiresVisualEvidence: false, hasVisualEvidence: false,
            report, priorReissues: 0, maxReissues: 3);

        Assert.Equal(EvidenceGate.EvidenceGateAction.Pass, decision.Action);
    }

    [Fact]
    public void Evaluate_OtherAspectConcerns_NotBlocked()
    {
        // A non-tests-and-evidence concern on a task that already shipped proof
        // is normal accept-with-concerns territory; the gate must not block it.
        var report = ReportWith(
            ("code-quality", AspectStatus.Concerns, "Helper duplicated."),
            ("tests-and-evidence", AspectStatus.Pass, "clean"));

        var decision = EvidenceGate.Evaluate(
            requiresVisualEvidence: true, hasVisualEvidence: true,
            report, priorReissues: 0, maxReissues: 3);

        Assert.Equal(EvidenceGate.EvidenceGateAction.Pass, decision.Action);
    }

    [Fact]
    public void Evaluate_BudgetExhausted_Escalates()
    {
        var report = ReportWith(("tests-and-evidence", AspectStatus.Pass, "ok"));

        var decision = EvidenceGate.Evaluate(
            requiresVisualEvidence: true, hasVisualEvidence: false,
            report, priorReissues: 3, maxReissues: 3);

        Assert.Equal(EvidenceGate.EvidenceGateAction.Escalate, decision.Action);
    }

    [Fact]
    public void BuildFollowUp_MissingVisual_DemandsScreenshot()
    {
        var report = ReportWith(("tests-and-evidence", AspectStatus.Pass, "ok"));
        var decision = EvidenceGate.Evaluate(
            requiresVisualEvidence: true, hasVisualEvidence: false,
            report, priorReissues: 0, maxReissues: 3);

        var followUp = EvidenceGate.BuildFollowUp(decision);

        Assert.Contains("[[TASK_DONE]]", followUp);
        Assert.Contains("screenshot", followUp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[[TASK_BLOCKED", followUp);
    }

    private static AspectRunReport ReportWith(params (string Aspect, AspectStatus Status, string Summary)[] verdicts)
    {
        var list = verdicts
            .Select(v => new AspectVerdict(
                v.Aspect, v.Status, v.Summary, Body: "",
                ConcernTagId: v.Status == AspectStatus.Pass ? null : "quality:concerns"))
            .ToList();
        return AspectRunReport.From(list);
    }
}
