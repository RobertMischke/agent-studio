extern alias UpdSvc;
using UpdSvc::AgentTaskboard.UpdateService;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReleaseContractTests
{
    [Theory]
    [InlineData("v1.1.0", "upgrade", true)]
    [InlineData("v1.0.0", "same", true)]
    [InlineData("v0.9.0", "downgrade", false)]
    public void ComparesReleaseTagsOffline(string candidateTag, string relation, bool allowed)
    {
        var current = Manifest("v1.0.0");
        var result = ReleaseContract.Compare(current, current, Manifest(candidateTag), candidateTag);
        Assert.Equal(relation, result.Relation.ToString().ToLowerInvariant());
        Assert.Equal(allowed, result.Allowed);
    }

    [Fact] public void DirtyBuildIsRefused() => Assert.False(ReleaseContract.Compare(null, null, Manifest("v1.0.0") with { Dirty = true }, "v1.0.0").Allowed);
    [Fact] public void MissingTagIsRefused() => Assert.False(ReleaseContract.Compare(null, null, Manifest(""), null).Allowed);

    [Fact]
    public void SameTagWithMismatchedPackageIsDiverged()
    {
        var current = Manifest("v1.0.0");
        var candidate = current with { CodingAgentChat = current.CodingAgentChat with { Integrity = "sha512-other" } };
        var result = ReleaseContract.Compare(current, current, candidate, "v1.0.0");
        Assert.False(result.Allowed);
        Assert.Equal(ReleaseRelation.Diverged, result.Relation);
    }

    [Fact] public void LegacyUntaggedInstallMayMigrate() => Assert.True(ReleaseContract.Compare(null, null, Manifest("v1.0.0"), "v1.0.0").Allowed);

    [Fact]
    public void CandidateMustMatchLatestApprovedTag()
    {
        var current = Manifest("v1.0.0");
        var result = ReleaseContract.Compare(current, current, Manifest("v1.1.0"), "v1.2.0");
        Assert.False(result.Allowed);
        Assert.Equal(ReleaseRelation.Diverged, result.Relation);
    }

    [Fact]
    public void ExplicitRollbackMayAuthorizeDowngrade()
    {
        var current = Manifest("v2.0.0");
        var result = ReleaseContract.Compare(current, current, Manifest("v1.0.0"), "v2.0.0", allowDowngrade: true);
        Assert.True(result.Allowed);
        Assert.Equal(ReleaseRelation.Downgrade, result.Relation);
    }

    private static ReleaseManifest Manifest(string tag)
    {
        var version = tag.StartsWith('v') ? tag[1..] : tag;
        return new ReleaseManifest(1, tag, version, $"commit-{version}", false,
            new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc),
            new ReleaseArtifact("CodingAgentRunner", "0.5.0", "v0.5.0", null, "sha256-car", "nuget-lock"),
            new ReleaseArtifact("coding-agent-chat", "1.2.3", "v1.2.3", null, "sha512-chat", "npm-lock"));
    }
}
