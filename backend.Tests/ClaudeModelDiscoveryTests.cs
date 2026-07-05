

using Xunit;

namespace AgentStudio.Tests;

public class ClaudeModelDiscoveryTests
{
    [Fact]
    public void ParsePickerSnapshot_MapsKnownClaudeLabelsThroughRegistry()
    {
        var models = ClaudeModelDiscovery.ParsePickerSnapshot("""
        Select Model
        > Claude Opus 4.8
          Sonnet 4.6
          Claude Haiku 4.5
        """);

        Assert.Contains(models, m => m.Id == ModelIds.ClaudeOpus48 && m.Label == "Claude Opus 4.8");
        Assert.Contains(models, m => m.Id == ModelIds.ClaudeSonnet46 && m.Vendor == "anthropic");
        Assert.Contains(models, m => m.Id == ModelIds.ClaudeHaiku45);
    }

    [Fact]
    public void ParsePickerSnapshot_MapsSonnet5WithFullThinkingLadder()
    {
        var models = ClaudeModelDiscovery.ParsePickerSnapshot("""
        Select Model
        > Claude Opus 4.8
          Sonnet 5
        """);

        var sonnet5 = Assert.Single(models, m => m.Id == ModelIds.ClaudeSonnet5);
        Assert.Equal("Claude Sonnet 5", sonnet5.Label);
        Assert.True(sonnet5.Available);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, sonnet5.ThinkingLevels);
    }

    [Fact]
    public void Reconcile_MarksRegistryModelsMissingFromCliUnavailable()
    {
        var discovered = new[]
        {
            ModelMetadataRegistry.ToCliModelInfo(
                ModelMetadataRegistry.Find(ModelIds.ClaudeOpus48)!,
                CliTypes.Claude)
        };

        var reconciled = ClaudeModelDiscovery.Reconcile(discovered);

        var missing = Assert.Single(reconciled, m => m.Id == ModelIds.ClaudeOpus47);
        Assert.False(missing.Available);
        Assert.True(missing.Deprecated);
        Assert.NotNull(missing.AvailabilityNote);
        Assert.DoesNotContain(reconciled, m => m.Id == ModelIds.ClaudeOpus47 && m.IsDefault);
    }

    [Fact]
    public void FallbackCatalog_OffersAvailableRegistryModels()
    {
        var catalog = ClaudeModelDiscovery.FallbackCatalog();

        Assert.NotEmpty(catalog.Models);
        Assert.All(catalog.Models, m => Assert.True(m.Available));
        Assert.Single(catalog.Models, m => m.IsDefault);
        Assert.Contains(catalog.Models, m => m.Id == ModelIds.ClaudeOpus48);
    }

    [Fact]
    public void FallbackCatalog_SelectableModelsHavePriceAndContextMetadata()
    {
        var catalog = ClaudeModelDiscovery.FallbackCatalog();

        foreach (var model in catalog.Models.Where(m => m.Available))
        {
            Assert.True(TokenPricing.Catalog.ContainsKey(model.Id), model.Id);
            Assert.NotNull(ModelMetadataRegistry.ContextWindowFor(model.Id));
        }
    }
}
