using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelFamilyAndMigrationTests
{
    [Fact]
    public void ClaudeCapturedCatalog_ResolvesLatestInEachFamily()
    {
        var catalog = Catalog(
            "claude-opus-5",
            "claude-fable-5-1",
            "claude-sonnet-5",
            "claude-haiku-4-5",
            "claude-opus-4-8");

        Assert.Equal("claude-opus-5", ModelFamilyResolver.Resolve(ModelFamilies.ClaudeOpus, catalog));
        Assert.Equal("claude-sonnet-5", ModelFamilyResolver.Resolve(ModelFamilies.ClaudeSonnet, catalog));
        Assert.Equal("claude-haiku-4-5", ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku, catalog));
    }

    [Fact]
    public void CodexCapturedCatalog_ResolvesLatestMiniAndFlagship()
    {
        var catalog = Catalog("gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.4-mini", "gpt-5.5");

        Assert.Equal("gpt-5.4-mini", ModelFamilyResolver.Resolve(ModelFamilies.GptMini, catalog));
        Assert.Equal("gpt-5.6-sol", ModelFamilyResolver.Resolve(ModelFamilies.GptFlagship, catalog));
    }

    [Fact]
    public void StaleDiscovery_FallsBackToCurrentRegistryGeneration()
    {
        var stale = Catalog("claude-opus-99");
        stale = stale with { FetchedAt = DateTime.UtcNow.AddDays(-2) };

        Assert.Equal(ModelIds.ClaudeOpus5, ModelFamilyResolver.Resolve(ModelFamilies.ClaudeOpus, stale));
    }

    [Fact]
    public void PinnedHaiku_HasVisibleTokenEconomyProposal_ButNeverAutoApplies()
    {
        var catalog = MigrationCatalog();
        var proposal = catalog.Propose("claude-haiku-4-5");

        Assert.NotNull(proposal);
        Assert.Equal("claude-sonnet-5", proposal!.To);
        Assert.False(proposal.SafeAuto);
        Assert.False(ModelMigrationAdmissionPolicy.ShouldApply(true, true, proposal));
    }

    [Fact]
    public void SafeNonExplicitMigration_CreatesAuditableTimelineEvent()
    {
        var proposal = MigrationCatalog().Propose("claude-opus-4-8");

        Assert.True(ModelMigrationAdmissionPolicy.ShouldApply(false, true, proposal));
        var timeline = ModelMigrationAdmissionPolicy.CreateTimelineEvent(proposal!);
        Assert.Equal(TimelineEventKinds.ModelMigrated, timeline.Kind);
        Assert.Equal("claude-opus-4-8", timeline.Details!["from"]);
        Assert.Equal("claude-opus-5", timeline.Details["to"]);
        Assert.Equal("test-v1", timeline.Details["catalogVersion"]);
    }

    [Fact]
    public void ExplicitPin_AndWorkspaceSwitch_BlockSafeMigration()
    {
        var proposal = MigrationCatalog().Propose("claude-opus-4-8");

        Assert.False(ModelMigrationAdmissionPolicy.ShouldApply(true, true, proposal));
        Assert.False(ModelMigrationAdmissionPolicy.ShouldApply(false, false, proposal));
    }

    private static CliModelCatalog Catalog(params string[] ids) => new()
    {
        Source = "captured-cli-2026-09-06",
        FetchedAt = DateTime.UtcNow,
        Models = ids.Select((id, index) => new CliModelInfo
        {
            Id = id,
            Label = id,
            Available = true,
            Deprecated = false,
            IsDefault = index == 0,
        }).ToList(),
    };

    private static ModelMigrationCatalog MigrationCatalog() => new(new ModelMigrationDocument
    {
        Version = "test-v1",
        Rules =
        [
            new ModelMigrationRule
            {
                From = "claude-opus-4-8", To = "claude-opus-5", Family = ModelFamilies.ClaudeOpus,
                SafeAuto = true, Rule = "latest-generation-same-family",
                CostClassFrom = "premium", CostClassTo = "premium",
                ReasoningLadderFrom = "standard", ReasoningLadderTo = "standard",
            },
            new ModelMigrationRule
            {
                From = "claude-haiku-4-5", To = "claude-sonnet-5", Family = ModelFamilies.ClaudeHaiku,
                SafeAuto = false, Rule = "token-economy-capability-upgrade",
                CostClassFrom = "economy", CostClassTo = "standard",
                ReasoningLadderFrom = "fast", ReasoningLadderTo = "standard",
            },
        ],
    });
}
