

using Xunit;

namespace AgentStudio.Tests;

public class CodexModelDiscoveryTests
{
    [Fact]
    public void ParseDebugModelsJson_ReturnsVisibleModelsInPriorityOrder()
    {
        const string output = """
        leading terminal noise
        {"models":[
          {"slug":"gpt-5.4-mini","display_name":"GPT-5.4-Mini","visibility":"list","priority":4},
          {"slug":"codex-auto-review","display_name":"Codex Auto Review","visibility":"hide","priority":29},
          {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","priority":0},
          {"slug":"gpt-5.4","display_name":"gpt-5.4","visibility":"list","priority":2}
        ]}
        trailing terminal noise
        """;

        var models = CodexModelDiscovery.ParseDebugModelsJson(output, activeModel: "gpt-5.4");

        Assert.Equal(["gpt-5.5", "gpt-5.4", "gpt-5.4-mini"],
            models.Where(m => m.Available).Select(m => m.Id));
        Assert.DoesNotContain(models, m => m.Id == "codex-auto-review");
        Assert.Equal("gpt-5.4", Assert.Single(models, m => m.IsDefault).Id);
        Assert.All(models, m => Assert.Equal("openai", m.Vendor));
        Assert.Equal(["minimal", "low", "medium", "high", "xhigh"],
            Assert.Single(models, m => m.Id == "gpt-5.5").ThinkingLevels);
        Assert.Equal(["minimal", "low", "medium", "high"],
            Assert.Single(models, m => m.Id == "gpt-5.4").ThinkingLevels);
        Assert.Equal(["minimal", "low", "medium", "high"],
            Assert.Single(models, m => m.Id == "gpt-5.4-mini").ThinkingLevels);
        // Per-model default reasoning is the top of each model's CLI ladder
        // (AGT-2025): gpt-5.5 exposes xhigh; the gpt-5.4 family tops at high.
        Assert.Equal("xhigh", Assert.Single(models, m => m.Id == "gpt-5.5").DefaultThinkingLevel);
        Assert.Equal("high", Assert.Single(models, m => m.Id == "gpt-5.4").DefaultThinkingLevel);
        Assert.Equal("high", Assert.Single(models, m => m.Id == "gpt-5.4-mini").DefaultThinkingLevel);
    }

    [Fact]
    public void ParseDebugModelsJson_UsesCliProvidedAstraLadderAndDefault()
    {
        var output = ReadModelFixture("models-0.153.4.json");

        var models = CodexModelDiscovery.ParseDebugModelsJson(
            output,
            activeModel: ModelIds.Gpt6Astra,
            cliVersion: "codex-cli 0.153.4");

        var astra = Assert.Single(models, model => model.Id == ModelIds.Gpt6Astra);
        Assert.True(astra.Available);
        Assert.True(astra.IsDefault);
        Assert.Equal("GPT-6-Astra", astra.Label);
        Assert.Equal(["low", "medium", "high", "xhigh", "max", "ultra"], astra.ThinkingLevels);
        Assert.Equal("medium", astra.DefaultThinkingLevel);
        Assert.Null(astra.AvailabilityNote);

        ModelMetadataRegistry.SetDiscoveredThinkingCapabilities(CliTypes.Codex, [astra]);
        Assert.Equal("max", ModelMetadataRegistry.ResolveThinkingLevel(
            CliTypes.Codex,
            ModelIds.Gpt6Astra,
            "max"));
        Assert.Equal("medium", ModelMetadataRegistry.ResolveThinkingLevel(
            CliTypes.Codex,
            ModelIds.Gpt6Astra,
            null));
    }

    [Fact]
    public void ParseDebugModelsJson_AppendsRegistryAstraAsUnavailableWhenOlderCliOmitsIt()
    {
        var output = ReadModelFixture("models-0.151.0.json");

        var models = CodexModelDiscovery.ParseDebugModelsJson(
            output,
            activeModel: ModelIds.Gpt55,
            cliVersion: "0.151.0");

        var astra = Assert.Single(models, model => model.Id == ModelIds.Gpt6Astra);
        Assert.False(astra.Available);
        Assert.False(astra.Deprecated);
        Assert.False(astra.IsDefault);
        Assert.Equal("Not offered by the installed codex-cli 0.151.0", astra.AvailabilityNote);
    }

    [Fact]
    public void ParseDebugModelsJson_SurfacesGpt56_FollowingTheLiveCli()
    {
        // Mirrors the live `codex debug models` shape (codex-cli 0.144.0): the
        // gpt-5.6 family is list-visible and ranks first. The catalog must
        // surface it with the extended reasoning ladder (xhigh + ultra) and a
        // top-of-ladder default, so nothing about gpt-5.6 is hardwired here.
        const string output = """
        {"models":[
          {"slug":"gpt-5.6-sol","display_name":"GPT-5.6-Sol","visibility":"list","priority":1},
          {"slug":"gpt-5.6-terra","display_name":"GPT-5.6-Terra","visibility":"list","priority":2},
          {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","priority":7},
          {"slug":"codex-auto-review","display_name":"Codex Auto Review","visibility":"hide","priority":43}
        ]}
        """;

        var models = CodexModelDiscovery.ParseDebugModelsJson(output, activeModel: "gpt-5.6-sol");

        Assert.Equal(["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.5"],
            models.Where(m => m.Available).Select(m => m.Id));
        Assert.DoesNotContain(models, m => m.Id == "codex-auto-review");
        var sol = Assert.Single(models, m => m.Id == "gpt-5.6-sol");
        Assert.True(sol.IsDefault);
        Assert.Equal("Discovered from CLI; missing registry metadata.", sol.AvailabilityNote);
        Assert.Equal(["minimal", "low", "medium", "high", "xhigh", "ultra"], sol.ThinkingLevels);
        Assert.Equal("ultra", sol.DefaultThinkingLevel);
    }

    [Fact]
    public void PickDetectedDefault_ReturnsHighestPriorityGpt56_WhenNoneFlagged()
    {
        var cat = Catalog(
            Model("gpt-5.6-sol", isDefault: false),
            Model("gpt-5.6-terra", isDefault: false),
            Model("gpt-5.5", isDefault: false));

        Assert.Equal("gpt-5.6-sol", CodexModelDiscovery.PickDetectedDefault(cat));
    }

    [Fact]
    public void PickDetectedDefault_FollowsTheCliFlaggedDefault_WhenItIsGpt56()
    {
        // config.toml pins gpt-5.6-terra even though sol ranks first: respect it.
        var cat = Catalog(
            Model("gpt-5.6-sol", isDefault: false),
            Model("gpt-5.6-terra", isDefault: true),
            Model("gpt-5.5", isDefault: false));

        Assert.Equal("gpt-5.6-terra", CodexModelDiscovery.PickDetectedDefault(cat));
    }

    [Fact]
    public void PickDetectedDefault_IgnoresNonGpt56FlaggedDefault()
    {
        // The CLI's active model is an older gpt-5.5, but a gpt-5.6 is list-
        // visible, so "as soon as 5.6 is detected" it becomes the default.
        var cat = Catalog(
            Model("gpt-5.5", isDefault: true),
            Model("gpt-5.6-sol", isDefault: false));

        Assert.Equal("gpt-5.6-sol", CodexModelDiscovery.PickDetectedDefault(cat));
    }

    [Fact]
    public void PickDetectedDefault_ReturnsNull_WhenNoGpt56Present()
    {
        var cat = Catalog(
            Model("gpt-5.5", isDefault: true),
            Model("gpt-5.4", isDefault: false));

        Assert.Null(CodexModelDiscovery.PickDetectedDefault(cat));
    }

    [Fact]
    public void FallbackCatalog_OffersRegistryOpenAiModels_WithGpt55Default_AndKnownAstra()
    {
        // Task item 1: with no CLI and no cache, the model surface falls back to
        // today's static registry list. gpt-5.6 is detection-only, so it must
        // NOT appear here, and gpt-5.5 stays the default.
        var catalog = CodexModelDiscovery.FallbackCatalog();

        Assert.NotEmpty(catalog.Models);
        Assert.All(catalog.Models, m => Assert.Equal("openai", m.Vendor));
        Assert.Contains(catalog.Models, m => m.Id == ModelIds.Gpt55);
        Assert.Contains(catalog.Models, m => m.Id == ModelIds.Gpt6Astra);
        Assert.DoesNotContain(catalog.Models, m => m.Id.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ModelIds.Gpt55, Assert.Single(catalog.Models, m => m.IsDefault).Id);
        // And it advertises no gpt-5.6 default, so the published default stays gpt-5.5.
        Assert.Null(CodexModelDiscovery.PickDetectedDefault(catalog));
    }

    private static CliModelCatalog Catalog(params CliModelInfo[] models)
        => new() { Models = models.ToList(), Source = "test", FetchedAt = DateTime.UtcNow };

    private static CliModelInfo Model(string id, bool isDefault)
        => new() { Id = id, Label = id, Vendor = "openai", IsDefault = isDefault };

    [Fact]
    public void ParseDebugModelsJson_FallsBackToFirstVisibleModelAsDefault()
    {
        const string output = """
        {"models":[
          {"slug":"gpt-5.4","display_name":"gpt-5.4","visibility":"list","priority":2},
          {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","priority":0}
        ]}
        """;

        var models = CodexModelDiscovery.ParseDebugModelsJson(output);

        Assert.Equal("gpt-5.5", Assert.Single(models, m => m.IsDefault).Id);
    }

    [Fact]
    public void WithCurrentCodexCapabilities_PreservesCliProvidedThinkingLevelsInCachedCatalogs()
    {
        var stale = new CliModelCatalog
        {
            Source = "disk-cache",
            FetchedAt = DateTime.UtcNow,
            Models =
            [
                new CliModelInfo
                {
                    Id = "gpt-5.5",
                    Label = "GPT-5.5",
                    Vendor = "openai",
                    ThinkingLevels = ["minimal", "low", "medium", "high"],
                    DefaultThinkingLevel = "medium"
                },
                new CliModelInfo
                {
                    Id = "gpt-5-codex",
                    Label = "GPT-5 Codex",
                    Vendor = "openai",
                    ThinkingLevels = ["minimal", "low", "medium", "high", "xhigh"],
                    DefaultThinkingLevel = "medium"
                }
            ]
        };

        var updated = CodexModelDiscovery.WithCurrentCodexCapabilities(stale);

        Assert.Equal(["minimal", "low", "medium", "high"],
            Assert.Single(updated.Models, m => m.Id == "gpt-5.5").ThinkingLevels);
        Assert.Equal(["minimal", "low", "medium", "high", "xhigh"],
            Assert.Single(updated.Models, m => m.Id == "gpt-5-codex").ThinkingLevels);
        Assert.Equal("medium", Assert.Single(updated.Models, m => m.Id == "gpt-5.5").DefaultThinkingLevel);
        Assert.Equal("medium", Assert.Single(updated.Models, m => m.Id == "gpt-5-codex").DefaultThinkingLevel);
        Assert.False(Assert.Single(updated.Models, m => m.Id == ModelIds.Gpt6Astra).Available);
    }

    [Fact]
    public void Registry_ContainsNonDefaultAstraMetadataWithoutInventedPricing()
    {
        var astra = Assert.Single(ModelMetadataRegistry.All, model => model.Id == ModelIds.Gpt6Astra);

        Assert.Equal("GPT-6 Astra", astra.Label);
        Assert.Equal("openai", astra.Vendor);
        Assert.Equal(272_000, astra.ContextWindow);
        Assert.False(astra.IsDefault);
        Assert.Null(astra.InputPricePerMillion);
        Assert.Null(astra.OutputPricePerMillion);
    }

    private static string ReadModelFixture(string fileName)
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "cli",
            "codex",
            fileName));
}
