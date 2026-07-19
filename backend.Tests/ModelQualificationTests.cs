using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelQualificationTests
{
    private static readonly CliModelCatalog Catalogue = new()
    {
        Source = "test-live-catalogue",
        FetchedAt = DateTime.UtcNow,
        Models =
        [
            Model("strong", "high", "max"),
            Model("balanced", "low", "medium", "high"),
            Model("economy", "low", "medium"),
        ],
    };

    private readonly ModelQualificationService _service = new(
        new CatalogueModelEconomyAdvisor(),
        new JsonlAppender(),
        NullLogger<ModelQualificationService>.Instance);

    [Fact]
    public void FrontendPolish_UsesEconomyRungAndLowReasoning()
    {
        var task = Task("polish", TaskTypes.Chore, modelExplicit: false, thinkingExplicit: false);

        var result = _service.Qualify(
            task,
            "Polish frontend spacing and tooltip copy in one SCSS component.",
            Catalogue,
            []);

        Assert.Equal("small", result.Complexity);
        Assert.Equal("economy", result.RecommendedModel);
        Assert.Equal("low", result.RecommendedThinkingLevel);
        Assert.Equal("qualification", result.SelectionSource);
        Assert.True(result.EstimatedSavingsPercent > 0);
    }

    [Fact]
    public void ArchitectureConcept_UsesTopRungAndHighReasoning()
    {
        var task = Task("architecture", TaskTypes.Feature, modelExplicit: false, thinkingExplicit: false)
            with { Mode = TaskModes.Planning };

        var result = _service.Qualify(
            task,
            "Design a cross-project backend orchestrator pipeline, state machine, schema migration, and API contract.",
            Catalogue,
            []);

        Assert.Equal("large", result.Complexity);
        Assert.Equal("strong", result.RecommendedModel);
        Assert.Equal("max", result.RecommendedThinkingLevel);
        Assert.Equal(0, result.EstimatedSavingsPercent);
    }

    [Fact]
    public void ExplicitCardModelAndReasoningAlwaysWinButRecommendationRemains()
    {
        var task = Task("override", TaskTypes.Chore, modelExplicit: true, thinkingExplicit: true) with
        {
            Model = "strong",
            ThinkingLevel = "max",
        };

        var result = _service.Qualify(task, "Polish CSS spacing.", Catalogue, []);

        Assert.Equal("economy", result.RecommendedModel);
        Assert.Equal("strong", result.SelectedModel);
        Assert.Equal("max", result.SelectedThinkingLevel);
        Assert.Equal("task-override", result.SelectionSource);
        Assert.Contains("card override wins", result.Reason);
    }

    private static CliModelInfo Model(string id, params string[] thinkingLevels) => new()
    {
        Id = id,
        Label = id,
        Vendor = "test",
        Available = true,
        ThinkingLevels = thinkingLevels.ToList(),
        DefaultThinkingLevel = thinkingLevels.FirstOrDefault(),
    };

    private static TaskInfo Task(string id, string taskType, bool modelExplicit, bool thinkingExplicit) => new()
    {
        Id = id,
        Title = id,
        ProjectName = "test-project",
        FolderPath = Path.GetTempPath(),
        CliType = CliTypes.Codex,
        Model = "strong",
        ThinkingLevel = "max",
        ModelExplicit = modelExplicit,
        ThinkingLevelExplicit = thinkingExplicit,
        TaskType = taskType,
    };
}
