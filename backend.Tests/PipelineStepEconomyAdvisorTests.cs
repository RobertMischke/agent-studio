using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PipelineStepEconomyAdvisorTests
{
    [Fact]
    public async Task OptedInAspect_UsesSuggestModelAgainstLiveSparkSubset()
    {
        var economy = new CapturingEconomyAdvisor();
        var service = new PipelineStepEconomyAdvisor(
            economy,
            new StubCatalogues(Catalogue(
                Model("gpt-5.6-sol", "GPT-5.6 Sol"),
                Model("gpt-5.6-codex-spark", "GPT-5.6 Codex Spark"))),
            NullLogger<PipelineStepEconomyAdvisor>.Instance);
        var settings = Settings(new PipelineStepSetting { EconomyModel = true });

        var result = await service.SuggestModelAsync(settings, "aspect-code-quality", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(CliTypes.Codex, result!.CliType);
        Assert.Equal("gpt-5.6-codex-spark", result.Model);
        Assert.Equal(TaskComplexity.Small, economy.Complexity);
        Assert.Single(economy.Models);
        Assert.Contains("spark", economy.Models[0].Id, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitStepModel_WinsWithoutCallingSuggestModel()
    {
        var economy = new CapturingEconomyAdvisor();
        var service = new PipelineStepEconomyAdvisor(
            economy,
            new StubCatalogues(Catalogue(Model("gpt-5.6-codex-spark", "Spark"))),
            NullLogger<PipelineStepEconomyAdvisor>.Instance);

        var result = await service.SuggestModelAsync(
            Settings(new PipelineStepSetting { EconomyModel = true, Model = "pinned" }),
            "aspect-code-quality",
            CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(economy.Models);
    }

    [Fact]
    public async Task MissingSparkModel_PreservesRuntimeDefault()
    {
        var service = new PipelineStepEconomyAdvisor(
            new CapturingEconomyAdvisor(),
            new StubCatalogues(Catalogue(Model("gpt-5.6-sol", "Sol"))),
            NullLogger<PipelineStepEconomyAdvisor>.Instance);

        var result = await service.SuggestModelAsync(
            Settings(new PipelineStepSetting { EconomyModel = true }),
            "aspect-code-quality",
            CancellationToken.None);

        Assert.Null(result);
    }

    private static ProjectSettings Settings(PipelineStepSetting setting) => new()
    {
        PipelineSteps = new Dictionary<string, PipelineStepSetting>
        {
            ["aspect-code-quality"] = setting,
        },
    };

    private static CliModelCatalog Catalogue(params CliModelInfo[] models) => new()
    {
        Source = "live-test",
        Models = models.ToList(),
    };

    private static CliModelInfo Model(string id, string label) => new()
    {
        Id = id,
        Label = label,
        Available = true,
        ThinkingLevels = ["minimal", "low"],
        DefaultThinkingLevel = "minimal",
    };

    private sealed class StubCatalogues(CliModelCatalog catalogue) : IPipelineModelCatalogueProvider
    {
        public Task<CliModelCatalog> GetAsync(string cliType, CancellationToken ct)
            => Task.FromResult(catalogue);
    }

    private sealed class CapturingEconomyAdvisor : IModelEconomyAdvisor
    {
        public IReadOnlyList<CliModelInfo> Models { get; private set; } = [];
        public TaskComplexity Complexity { get; private set; }

        public ModelEconomySuggestion SuggestModel(IReadOnlyList<CliModelInfo> availableModels, TaskComplexity complexity)
        {
            Models = availableModels;
            Complexity = complexity;
            var selected = availableModels[^1];
            return new ModelEconomySuggestion(selected.Id, "minimal", 65, "test SuggestModel");
        }
    }
}
