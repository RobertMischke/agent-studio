
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the scope-resolution rules behind the user-triggered code review.
/// The historical default reviewed only HEAD, so a task whose feature
/// landed in an earlier commit and whose HEAD was a later test/doc-only
/// commit was wrongly judged "not implemented" (ASS-794). The new default
/// reviews the aggregate of every commit the task owns; an explicit commit
/// override still pins a single commit; an unattributed task falls back to
/// HEAD and finally the working tree.
/// </summary>
public class CodeReviewScopeResolverTests
{
    [Fact]
    public void MultipleTaskCommits_AggregatesAll_NotJustHead()
    {
        var shas = new[] { "710976abcdef", "f9a747abc123" };
        var scope = CodeReviewScopeResolver.Resolve(overrideCommit: null, taskShas: shas, headSha: "710976abcdef");

        Assert.Equal(CodeReviewScopeMode.AggregateCommits, scope.Mode);
        Assert.Equal(2, scope.Shas.Count);
        Assert.Contains("f9a747abc123", scope.Shas);
        Assert.Contains("710976abcdef", scope.Shas);
        // Label names the reviewed range, not "(HEAD)".
        Assert.Contains("2 task commits", scope.Label);
        Assert.Contains("f9a747ab", scope.Label);
        Assert.DoesNotContain("HEAD", scope.Label);
    }

    [Fact]
    public void ExplicitCommitOverride_PinsSingleCommit_IgnoringTaskCommits()
    {
        var shas = new[] { "710976abcdef", "f9a747abc123" };
        var scope = CodeReviewScopeResolver.Resolve(
            overrideCommit: "deadbeef0000", taskShas: shas, headSha: "710976abcdef");

        Assert.Equal(CodeReviewScopeMode.SingleCommit, scope.Mode);
        Assert.Single(scope.Shas);
        Assert.Equal("deadbeef0000", scope.Shas[0]);
        Assert.Equal("deadbeef", scope.Label);
    }

    [Fact]
    public void SingleTaskCommit_ReviewsThatCommit()
    {
        var scope = CodeReviewScopeResolver.Resolve(
            overrideCommit: null, taskShas: new[] { "f9a747abc123" }, headSha: "f9a747abc123");

        Assert.Equal(CodeReviewScopeMode.SingleCommit, scope.Mode);
        Assert.Single(scope.Shas);
        Assert.Equal("f9a747abc123", scope.Shas[0]);
        Assert.Equal("f9a747ab", scope.Label);
    }

    [Fact]
    public void NoTaskCommits_FallsBackToHead()
    {
        var scope = CodeReviewScopeResolver.Resolve(
            overrideCommit: null, taskShas: Array.Empty<string>(), headSha: "abc1234567");

        Assert.Equal(CodeReviewScopeMode.SingleCommit, scope.Mode);
        Assert.Single(scope.Shas);
        Assert.Equal("abc1234567", scope.Shas[0]);
        Assert.Contains("HEAD", scope.Label);
    }

    [Fact]
    public void NoTaskCommitsAndNoHead_FallsBackToWorkingTree()
    {
        var scope = CodeReviewScopeResolver.Resolve(
            overrideCommit: null, taskShas: null, headSha: null);

        Assert.Equal(CodeReviewScopeMode.WorkingTree, scope.Mode);
        Assert.Empty(scope.Shas);
        Assert.Equal("working tree", scope.Label);
    }

    [Fact]
    public void DuplicateTaskCommits_AreDeduped()
    {
        var shas = new[] { "f9a747abc123", "f9a747abc123", "710976abcdef" };
        var scope = CodeReviewScopeResolver.Resolve(overrideCommit: null, taskShas: shas, headSha: null);

        Assert.Equal(CodeReviewScopeMode.AggregateCommits, scope.Mode);
        Assert.Equal(2, scope.Shas.Count);
    }

    [Fact]
    public void WhitespaceOverride_IsIgnored_AndTaskScopeApplies()
    {
        var scope = CodeReviewScopeResolver.Resolve(
            overrideCommit: "   ", taskShas: new[] { "f9a747abc123" }, headSha: null);

        Assert.Equal(CodeReviewScopeMode.SingleCommit, scope.Mode);
        Assert.Equal("f9a747abc123", scope.Shas[0]);
    }
}
