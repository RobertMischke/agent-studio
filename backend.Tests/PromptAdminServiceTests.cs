using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class PromptAdminServiceTests
{
    [Fact]
    public void Render_UsesOverrideAndResetReturnsToDefault()
    {
        using var home = new PromptTestHome();
        const string name = "runner-fresh-start.md";
        home.WriteDefault(name, "Default {{name}}");

        var prompts = home.CreatePromptService();
        var admin = home.CreateAdminService(prompts);

        Assert.Equal("Default Ada", prompts.Render(name, new Dictionary<string, string?> { ["name"] = "Ada" }));

        var saved = admin.SaveOverride(name, "Override {{name}}");

        Assert.NotNull(saved);
        Assert.True(saved.HasOverride);
        Assert.Equal("Default {{name}}", saved.BaseDefaultContent);
        Assert.Equal("Override Ada", prompts.Render(name, new Dictionary<string, string?> { ["name"] = "Ada" }));

        var reset = admin.ResetToDefault(name);

        Assert.NotNull(reset);
        Assert.False(reset.HasOverride);
        Assert.Equal("Default Ada", prompts.Render(name, new Dictionary<string, string?> { ["name"] = "Ada" }));
    }

    [Fact]
    public void DetailAndCatalog_FlagOverrideWhenDefaultChangedSinceBase()
    {
        using var home = new PromptTestHome();
        const string name = "review-aspect-code-quality.md";
        home.WriteDefault(name, "Default v1");

        var prompts = home.CreatePromptService();
        var admin = home.CreateAdminService(prompts);
        admin.SaveOverride(name, "Custom override");

        home.WriteDefault(name, "Default v2");

        var detail = admin.GetDetail(name);
        var catalog = admin.GetCatalog();

        Assert.NotNull(detail);
        Assert.True(detail.DefaultChangedSinceOverride);
        Assert.Equal("Default v1", detail.BaseDefaultContent);
        Assert.Equal("Default v2", detail.DefaultContent);
        Assert.Equal("Custom override", detail.EffectiveContent);
        Assert.Contains(catalog.Items, item => item.Name == name && item.DefaultChangedSinceOverride);

        var rebaselined = admin.RebaselineOverride(name);

        Assert.NotNull(rebaselined);
        Assert.False(rebaselined.DefaultChangedSinceOverride);
        Assert.Equal("Default v2", rebaselined.BaseDefaultContent);
    }

    [Fact]
    public void Catalog_IncludesOverrideOnlyTemplates()
    {
        using var home = new PromptTestHome();
        const string defaultName = "summary-protocol.md";
        const string overrideOnlyName = "custom-runtime.md";
        home.WriteDefault(defaultName, "Default");
        home.WriteOverrideFile(overrideOnlyName, "Override only");

        var admin = home.CreateAdminService(home.CreatePromptService());

        var catalog = admin.GetCatalog();
        var detail = admin.GetDetail(overrideOnlyName);

        Assert.Contains(catalog.Items, item => item.Name == defaultName && item.HasDefault);
        Assert.Contains(catalog.Items, item => item.Name == overrideOnlyName && item.HasOverride && !item.HasDefault);
        Assert.NotNull(detail);
        Assert.Equal("Override only", detail.EffectiveContent);
    }

    [Fact]
    public void Detail_ExposesSlotsAndRegisteredUsages()
    {
        using var home = new PromptTestHome();
        const string name = "runner-fresh-start.md";
        home.WriteDefault(name, "Hello {{name}}, task {{taskId}}.");

        var admin = home.CreateAdminService(home.CreatePromptService());

        var detail = admin.GetDetail(name);

        Assert.NotNull(detail);
        Assert.Equal(new[] { "name", "taskId" }, detail.Slots);
        // runner-fresh-start.md is registered in PromptUsageCatalog with usages.
        Assert.NotEmpty(detail.Usages);
        Assert.Contains(detail.Usages, u => u.Component == "ProjectRunner");
    }

    [Fact]
    public void Preview_RendersDraftAndReportsFilledAndMissingSlots()
    {
        using var home = new PromptTestHome();
        const string name = "runner-fresh-start.md";
        home.WriteDefault(name, "saved {{a}} {{b}}");

        var admin = home.CreateAdminService(home.CreatePromptService());

        var result = admin.Preview(
            name,
            new Dictionary<string, string?> { ["a"] = "X" },
            content: "draft {{a}} {{b}}");

        Assert.NotNull(result);
        Assert.Equal("draft X {{b}}", result.Rendered);
        Assert.Equal(new[] { "a", "b" }, result.Slots);
        Assert.Equal(new[] { "a" }, result.FilledSlots);
        Assert.Equal(new[] { "b" }, result.MissingSlots);
    }

    [Fact]
    public void Coverage_ReportsTheFourMigratedSitesAsCovered()
    {
        using var home = new PromptTestHome();
        var admin = home.CreateAdminService(home.CreatePromptService());

        var coverage = admin.GetCoverage();

        Assert.True(coverage.TotalSites >= 4);
        Assert.Equal(coverage.Items.Count, coverage.TotalSites);
        Assert.Equal(
            coverage.CoveredSites + coverage.PendingSites,
            coverage.TotalSites);
        Assert.Contains(coverage.Items, i =>
            i.Component.EndsWith("ReviewDecisionOrchestrator.cs") && i.Status == "covered");
        Assert.Contains(coverage.Items, i =>
            i.Component.EndsWith("CodePatternDriftAnalysisService.cs") && i.Status == "covered");
    }

    private sealed class PromptTestHome : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"prompt-admin-tests-{Guid.NewGuid():N}");

        public PromptTestHome()
        {
            Directory.CreateDirectory(DefaultDir);
            Directory.CreateDirectory(OverrideDir);
        }

        private string DefaultDir => Path.Combine(_root, "defaults");
        private string OverrideDir => Path.Combine(_root, "overrides");

        public void WriteDefault(string name, string content) =>
            File.WriteAllText(Path.Combine(DefaultDir, name), content);

        public void WriteOverrideFile(string name, string content) =>
            File.WriteAllText(Path.Combine(OverrideDir, name), content);

        public RuntimePromptService CreatePromptService()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PromptTemplates:RuntimePath"] = DefaultDir,
                    ["PromptTemplates:OverridePath"] = OverrideDir,
                })
                .Build();
            return new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        }

        public PromptAdminService CreateAdminService(RuntimePromptService prompts) =>
            new(prompts, NullLogger<PromptAdminService>.Instance);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
