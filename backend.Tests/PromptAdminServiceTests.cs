using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Drift;
using AgentStudio.Pipeline;

using Xunit;

namespace AgentStudio.Tests;

public sealed class PromptAdminServiceTests
{
    [Fact]
    public void EveryShippedPrompt_HasAReviewCompanion()
    {
        var promptDirectory = Path.Combine(
            DriftRepoRootLocator.Resolve(),
            "prompts",
            "runtime");
        var missing = Directory.EnumerateFiles(promptDirectory, "*.md")
            .Where(path => !File.Exists(path + ".meta.json"))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Empty(missing);
    }

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
    public void CatalogAndDetail_ExposeProjectOverrideOriginAndStaleDefault()
    {
        using var home = new PromptTestHome();
        const string name = "review-aspect-code-quality.md";
        home.WriteDefault(name, "Shipped v1");
        var settings = home.CreateProjectSettings();
        settings.SetPipelineStep("Agent Studio", "aspect-code-quality", new PipelineStepSetting
        {
            Prompt = "Project-specific review",
            PromptBaseDefaultSha = RuntimePromptService.ContentSha("Shipped v1"),
            PromptBaseDefaultContent = "Shipped v1",
        });
        home.WriteDefault(name, "Shipped v2");

        var admin = home.CreateAdminService(home.CreatePromptService(), settings);
        var item = Assert.Single(
            admin.GetCatalog().Items,
            candidate => candidate.Name == name);
        var origin = Assert.Single(item.ProjectOverrides);

        Assert.True(item.HasOverride);
        Assert.False(item.HasGlobalOverride);
        Assert.True(item.DefaultChangedSinceOverride);
        Assert.Equal("Agent Studio", origin.ProjectName);
        Assert.True(origin.DefaultChangedSinceOverride);
        Assert.Equal(
            RuntimePromptService.ContentSha("Shipped v1"),
            origin.BaseDefaultSha);

        var detail = Assert.IsType<PromptDetail>(admin.GetDetail(name));
        Assert.Equal("Agent Studio", Assert.Single(detail.ProjectOverrides).ProjectName);
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

    [Fact]
    public void Review_WritesAdjacentSidecarAndFlagsAuditedDeadPrompt()
    {
        using var home = new PromptTestHome();
        const string name = "recurring-output-pattern-review.md";
        home.WriteDefault(name, "Review recurring output.");
        var prompts = home.CreatePromptService();
        var admin = home.CreateAdminService(prompts);

        var result = admin.Review(name, "Robert");

        Assert.NotNull(result);
        Assert.Equal("stale", result.Metadata.Status);
        Assert.Equal("Robert", result.Metadata.ReviewedBy);
        Assert.Contains(result.Metadata.Findings, finding => finding.Code == "dead-prompt");
        Assert.True(File.Exists(home.ReviewSidecarPath(name)));

        var catalog = admin.GetCatalog();
        var item = Assert.Single(catalog.Items);
        Assert.NotNull(item.LastReviewedAt);
        Assert.Equal("stale", item.ReviewStatus);
        Assert.True(item.ReviewFindingCount > 0);
    }

    [Fact]
    public void CatalogAndDetail_ProjectOverridesIncludeOriginDiffAndOrphans()
    {
        using var home = new PromptTestHome();
        const string name = "review-aspect-code-quality.md";
        home.WriteDefault(name, "Default prompt");
        var prompts = home.CreatePromptService();
        var settings = home.CreateProjectSettings();
        settings.SetPipelineStep(
            "Alpha",
            "aspect-code-quality",
            new PipelineStepSetting { Prompt = "Project-specific prompt" });
        settings.SetPipelineStep(
            "Alpha",
            "retired-review-step",
            new PipelineStepSetting { Prompt = "Unused override" });
        var admin = home.CreateAdminService(prompts, settings);

        var catalog = admin.GetCatalog();
        var detail = admin.GetDetail(name);

        Assert.Equal(1, Assert.Single(catalog.Items).ProjectOverrideCount);
        var orphan = Assert.Single(catalog.OrphanedOverrides);
        Assert.Equal("retired-review-step", orphan.StepId);
        Assert.True(orphan.Orphaned);

        Assert.NotNull(detail);
        var projectOverride = Assert.Single(detail.ProjectOverrides);
        Assert.Equal("Alpha", projectOverride.ProjectName);
        Assert.Equal("aspect-code-quality", projectOverride.StepId);
        Assert.False(projectOverride.MatchesDefault);
        Assert.True(projectOverride.AddedLines > 0);
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

        public string ReviewSidecarPath(string name) =>
            Path.Combine(DefaultDir, name + ".meta.json");

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

        public ProjectSettingsService CreateProjectSettings() =>
            new(
                NullLogger<ProjectSettingsService>.Instance,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _root,
                    })
                    .Build());

        public PromptAdminService CreateAdminService(
            RuntimePromptService prompts,
            ProjectSettingsService? projectSettings = null) =>
            new(
                prompts,
                new PromptReviewService(
                    prompts,
                    projectSettings ?? CreateProjectSettings(),
                    NullLogger<PromptReviewService>.Instance),
                NullLogger<PromptAdminService>.Instance);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
