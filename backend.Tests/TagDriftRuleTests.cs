
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Unit tests for the deterministic <see cref="TagDriftRule"/>: a concern tag
/// that survives on an accepted card with nothing open is drift; a concern tag
/// kept alive by an outcome issue, an open verdict, or a current aspect concern
/// is not. See the "concern tags bleiben kleben" bug.
/// </summary>
public class TagDriftRuleTests
{
    [Fact]
    public void AcceptedCleanCard_WithConcernTag_FlagsDrift()
    {
        var tags = new[] { "requirement:concerns", "quality:concerns", "area-backend" };
        var drift = TagDriftRule.FindDriftingConcernTags(
            tags, justifiedConcernTags: Array.Empty<string>(), verdict: "accept", hasOutcomeIssue: false);

        Assert.Equal(new[] { "requirement:concerns", "quality:concerns" }, drift);
    }

    [Fact]
    public void AcceptedWithConcerns_KeepsJustified_FlagsOnlyStale()
    {
        var tags = new[] { "requirement:concerns", "quality:concerns" };
        var drift = TagDriftRule.FindDriftingConcernTags(
            tags, justifiedConcernTags: new[] { "quality:concerns" }, verdict: "accept", hasOutcomeIssue: false);

        Assert.Equal(new[] { "requirement:concerns" }, drift);
    }

    [Fact]
    public void OpenVerdict_KeepsAllConcerns()
    {
        var tags = new[] { "requirement:concerns", "quality:concerns" };

        Assert.Empty(TagDriftRule.FindDriftingConcernTags(
            tags, Array.Empty<string>(), verdict: "reissue", hasOutcomeIssue: false));
        Assert.Empty(TagDriftRule.FindDriftingConcernTags(
            tags, Array.Empty<string>(), verdict: "escalate", hasOutcomeIssue: false));
    }

    [Fact]
    public void ActiveOutcomeIssue_KeepsAllConcerns()
    {
        var tags = new[] { "quality:concerns" };
        Assert.Empty(TagDriftRule.FindDriftingConcernTags(
            tags, Array.Empty<string>(), verdict: "accept", hasOutcomeIssue: true));
    }

    [Fact]
    public void NonConcernTags_AreNeverDrift()
    {
        var tags = new[] { "area-backend", "orchestrator-moved", "reissue:autoreview" };
        Assert.Empty(TagDriftRule.FindDriftingConcernTags(
            tags, Array.Empty<string>(), verdict: "accept", hasOutcomeIssue: false));
    }

    [Fact]
    public void UnparseableMarker_IsTreatedAsConcernTag()
    {
        Assert.True(TagDriftRule.IsAspectConcernTag("review:unparseable"));
        var drift = TagDriftRule.FindDriftingConcernTags(
            new[] { "review:unparseable" }, Array.Empty<string>(), verdict: "accept", hasOutcomeIssue: false);
        Assert.Equal(new[] { "review:unparseable" }, drift);
    }

    [Fact]
    public void ExtractConcernTagIds_RecoversConcernSetFromDecisionReason()
    {
        var ids = TagDriftRule.ExtractConcernTagIds(
            "Multi-aspect: accept with concerns (quality:concerns, requirement:concerns)");
        Assert.Equal(new[] { "quality:concerns", "requirement:concerns" }, ids);

        Assert.Empty(TagDriftRule.ExtractConcernTagIds("Multi-aspect: all aspects pass"));
        Assert.Empty(TagDriftRule.ExtractConcernTagIds(null));
    }
}
