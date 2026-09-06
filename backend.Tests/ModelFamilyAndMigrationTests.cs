using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelFamilyAndMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-studio-model-migrations", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        ModelFamilyResolver.ClearLiveCatalogues();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Resolver_UsesNewestAvailableMemberFromCapturedClaudeCatalog()
    {
        var catalog = Catalog(
            Model("claude-opus-4-8", available: true),
            Model("claude-opus-5", available: true),
            Model("claude-opus-6", available: false),
            Model("claude-sonnet-5", available: true),
            Model("claude-haiku-4-5", available: true));

        Assert.Equal("claude-opus-5", ModelFamilyResolver.Resolve(ModelFamilies.ClaudeOpus, catalog));
        Assert.Equal("claude-sonnet-5", ModelFamilyResolver.Resolve(ModelFamilies.ClaudeSonnet, catalog));
        Assert.Equal("claude-haiku-4-5", ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku, catalog));
    }

    [Fact]
    public void Resolver_UsesNewestAvailableMemberFromCapturedCodexCatalog()
    {
        var catalog = Catalog(
            Model("gpt-5.6-sol", available: true),
            Model("gpt-5.5", available: true),
            Model("gpt-5.4-mini", available: true));

        Assert.Equal("gpt-5.6-sol", ModelFamilyResolver.Resolve(ModelFamilies.GptFlagship, catalog));
        Assert.Equal("gpt-5.4-mini", ModelFamilyResolver.Resolve(ModelFamilies.GptMini, catalog));
    }

    [Fact]
    public void Resolver_FallsBackToRegistryWhenDiscoveryIsUnavailable()
    {
        Assert.Equal(ModelIds.ClaudeOpus5, ModelFamilyResolver.Resolve(ModelFamilies.ClaudeOpus));
        Assert.Equal(ModelIds.ClaudeHaiku45, ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku));
    }

    [Fact]
    public void Resolver_FallsBackToRegistryWhenDiscoveryIsStale()
    {
        var stale = Catalog(Model("claude-opus-99", available: true)) with
        {
            FetchedAt = DateTime.UtcNow - ModelFamilyResolver.LiveCatalogMaxAge - TimeSpan.FromMinutes(1),
        };

        Assert.Equal(ModelIds.ClaudeOpus5, ModelFamilyResolver.Resolve(ModelFamilies.ClaudeOpus, stale));
    }

    [Fact]
    public void Catalog_ProposesPinnedHaikuMigration_WithCostAndLadderDiff()
    {
        var service = Service();

        var proposal = Assert.IsType<ModelMigrationProposal>(service.Propose(ModelIds.ClaudeHaiku45));

        Assert.Equal(ModelIds.ClaudeSonnet5, proposal.To);
        Assert.False(proposal.SafeAuto);
        Assert.Equal("economy", proposal.FromCostClass);
        Assert.Equal("standard", proposal.ToCostClass);
        Assert.NotEmpty(proposal.ToReasoningLadder);
    }

    [Fact]
    public void SafeNonExplicitMigration_CreatesCompleteTimelineAuditEvent()
    {
        var service = Service();
        var qualification = new ModelQualificationService(
            new ModelRoutingPolicyRegistry(),
            new FixedRoutingMode(),
            new JsonlAppender(),
            NullLogger<ModelQualificationService>.Instance,
            migrations: service);
        var task = new TaskInfo
        {
            Id = "migration-task", Title = "migration-task", ProjectName = "test-project",
            FolderPath = _root, CliType = CliTypes.Claude, Model = ModelIds.ClaudeOpus48,
            ModelExplicit = false, ThinkingLevelExplicit = false, TaskType = TaskTypes.Chore,
        };
        var decision = qualification.Qualify(task, "Apply a bounded local edit.", Catalog(
            Model("gpt-5.6-sol", available: true),
            Model("gpt-5.6-terra", available: true),
            Model("gpt-5.6-luna", available: true)), []);
        var migration = Assert.IsType<ModelMigrationProposal>(decision.AppliedMigration);

        var audit = ModelMigrationAudit.TimelineEvent(migration, new DateTime(2026, 9, 6, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(ModelIds.ClaudeOpus5, decision.SelectedModel);
        Assert.Equal("policy-migration", decision.SelectionSource);
        Assert.Equal(TimelineEventKinds.ModelMigrated, audit.Kind);
        Assert.Equal(ModelIds.ClaudeOpus48, audit.Details!["from"]);
        Assert.Equal(ModelIds.ClaudeOpus5, audit.Details["to"]);
        Assert.Equal("latest-in-family", audit.Details["rule"]);
        Assert.Equal("test-2026-09-06", audit.Details["catalogVersion"]);
    }

    [Fact]
    public void ExplicitPin_IsNeverAutomaticallyMigrated()
    {
        Assert.Null(Service().SafeAutomaticMigration(
            ModelIds.ClaudeOpus48, isExplicit: true, autoApplyEnabled: true));
    }

    private ModelMigrationCatalogService Service()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "model-migrations.json");
        File.WriteAllText(path, """
        {
          "version": "test-2026-09-06",
          "migrations": [
            {
              "from": "claude-opus-4-8", "to": "claude-opus-5", "family": "claude-opus",
              "rule": "latest-in-family", "safeAuto": true,
              "fromCostClass": "premium", "toCostClass": "premium",
              "fromReasoningLadder": ["low", "medium"], "toReasoningLadder": ["low", "medium", "high"]
            },
            {
              "from": "claude-haiku-4-5", "to": "claude-sonnet-5", "family": "claude-haiku",
              "rule": "te-economy-haiku-to-sonnet-5", "safeAuto": false,
              "fromCostClass": "economy", "toCostClass": "standard",
              "fromReasoningLadder": ["low"], "toReasoningLadder": ["low", "medium", "high"]
            }
          ]
        }
        """);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TokenEconomy:ModelMigrationCatalogPath"] = path,
        }).Build();
        return new ModelMigrationCatalogService(configuration, NullLogger<ModelMigrationCatalogService>.Instance);
    }

    private static CliModelCatalog Catalog(params CliModelInfo[] models) => new()
    {
        Models = models.ToList(), Source = "captured-2026-09-06", FetchedAt = DateTime.UtcNow,
    };

    private static CliModelInfo Model(string id, bool available) => new()
    {
        Id = id, Label = id, Vendor = id.StartsWith("claude-") ? "anthropic" : "openai",
        Available = available, Deprecated = false,
    };

    private sealed class FixedRoutingMode : IModelRoutingModeProvider
    {
        public bool EconomyMode => false;
    }
}
