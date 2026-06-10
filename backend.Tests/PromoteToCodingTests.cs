using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks in the "promote a finished planning task to a coding task" payload
/// (docs/research/planning-research-task-kinds-2026-05.md): title + prompt
/// extracted from the planning report, copied image attachments from both
/// <c>results/</c> and <c>attachments/</c>, mode=coding, state=1-preparation.
/// Split into pure-parser facts (the heading extraction) and scanner-level
/// facts (the whole plan, including image listing).
/// </summary>
public class PromoteToCodingTests : IDisposable
{
    private readonly string _watchPath;

    public PromoteToCodingTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "agent-taskboard-promote-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    private TaskScannerService BuildScanner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private string WriteJob(string slug, string mode, string state = "5-human-review", string title = "Plan the widget refactor")
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{title}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\",\"mode\":\"{mode}\"}}");
        return dir;
    }

    // ---------- pure parser ----------

    [Fact]
    public void ExtractProposedTaskPrompt_TakesSectionUpToNextHeading()
    {
        const string report = """
            # Planning report

            Some analysis here that should be ignored.

            ## Proposed task prompt

            Implement the widget refactor.

            ### Details

            Keep the public API stable.

            ## Risks

            This trailing section must be excluded.
            """;

        var body = PlanningPromotion.ExtractProposedTaskPrompt(report);

        Assert.Contains("Implement the widget refactor.", body);
        Assert.Contains("Keep the public API stable.", body); // level-3 sub-heading stays inside
        Assert.DoesNotContain("This trailing section", body);
        Assert.DoesNotContain("Some analysis here", body);
    }

    [Fact]
    public void ExtractProposedTaskPrompt_FallsBackToWholeReportWhenHeadingAbsent()
    {
        const string report = "# Findings\n\nNo proposed-prompt heading here.";
        var body = PlanningPromotion.ExtractProposedTaskPrompt(report);
        Assert.Equal(report, body);
    }

    [Fact]
    public void ExtractProposedTaskPrompt_EmptyForNullOrBlank()
    {
        Assert.Equal("", PlanningPromotion.ExtractProposedTaskPrompt(null));
        Assert.Equal("", PlanningPromotion.ExtractProposedTaskPrompt("   "));
    }

    [Fact]
    public void DeriveTitle_PrefersPlanningTitleThenHeadingThenId()
    {
        Assert.Equal("My planning task", PlanningPromotion.DeriveTitle("My planning task", "## Proposed task prompt\n\nx", "id-1"));
        Assert.Equal("Report heading", PlanningPromotion.DeriveTitle("  ", "# Report heading\n\nbody", "id-1"));
        Assert.Equal("id-1", PlanningPromotion.DeriveTitle(null, null, "id-1"));
    }

    // ---------- scanner plan ----------

    [Fact]
    public void BuildPlan_PopulatesTitlePromptModeAndState()
    {
        var dir = WriteJob("plan-1", TaskModes.Planning, title: "Refactor the widget");
        File.WriteAllText(Path.Combine(dir, "status.md"),
            "# Planning report\n\nanalysis\n\n## Proposed task prompt\n\nDo the refactor cleanly.\n");

        var plan = BuildScanner().BuildPromoteToCodingPlan("plan-1", _watchPath);

        Assert.NotNull(plan);
        Assert.Equal("Refactor the widget", plan!.Title);
        Assert.Equal("Do the refactor cleanly.", plan.PromptMarkdown);
        Assert.Equal(TaskModes.Coding, plan.Mode);
        Assert.Equal(TaskStates.Preparation, plan.TargetState);
        Assert.Equal(_watchPath, plan.WatchPath);
    }

    [Fact]
    public void BuildPlan_ListsImagesFromResultsAndAttachmentsDedupedByName()
    {
        var dir = WriteJob("plan-2", TaskModes.Planning);
        File.WriteAllText(Path.Combine(dir, "status.md"), "## Proposed task prompt\n\nx");

        var results = Path.Combine(dir, "results");
        var attachments = Path.Combine(dir, "attachments");
        Directory.CreateDirectory(results);
        Directory.CreateDirectory(attachments);
        File.WriteAllBytes(Path.Combine(results, "shot.png"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(results, "notes.txt"), "not an image");
        File.WriteAllBytes(Path.Combine(attachments, "diagram.jpg"), new byte[] { 4, 5, 6 });
        File.WriteAllBytes(Path.Combine(attachments, "shot.png"), new byte[] { 7, 8, 9 }); // dup name -> excluded

        var plan = BuildScanner().BuildPromoteToCodingPlan("plan-2", _watchPath);

        Assert.NotNull(plan);
        var byName = plan!.Attachments.ToDictionary(a => a.FileName, a => a.Source);
        Assert.Equal(2, plan.Attachments.Count);
        Assert.Equal("results", byName["shot.png"]);       // results listed first wins the dedupe
        Assert.Equal("attachments", byName["diagram.jpg"]);
        Assert.DoesNotContain(plan.Attachments, a => a.FileName == "notes.txt");
    }

    [Fact]
    public void BuildPlan_NoImages_ReturnsEmptyAttachmentList()
    {
        var dir = WriteJob("plan-3", TaskModes.Planning);
        File.WriteAllText(Path.Combine(dir, "status.md"), "## Proposed task prompt\n\nx");

        var plan = BuildScanner().BuildPromoteToCodingPlan("plan-3", _watchPath);

        Assert.NotNull(plan);
        Assert.Empty(plan!.Attachments);
    }

    [Fact]
    public void BuildPlan_UnknownJob_ReturnsNull()
    {
        Assert.Null(BuildScanner().BuildPromoteToCodingPlan("does-not-exist", _watchPath));
    }
}
