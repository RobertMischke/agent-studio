using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

public sealed class DeliveryRefResolverTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "delivery-ref-resolution-" + Guid.NewGuid().ToString("N"));

    public DeliveryRefResolverTests()
    {
        Directory.CreateDirectory(_folder);
        WriteCard(branch: "runner/attributed/AGT-42");
        WriteSubject(immutableResultRef: "agent-studio/results/run-42/result");
    }

    [Fact]
    public void Resolve_UsesImmutableResultRefBeforeAttributedAndConventionalBranches()
    {
        var resolved = DeliveryRefResolver.Resolve("slug-that-is-not-a-ref", _folder);

        Assert.Equal("agent-studio/results/run-42/result", resolved.Ref);
        Assert.Equal(DeliveryRefSource.ImmutableResultEnvelope, resolved.Source);
        Assert.True(resolved.IsRemote);
        Assert.Equal(new string('a', 40), resolved.ExpectedResultSha);
    }

    [Fact]
    public void Resolve_UsesAttributedCommitBranchBeforeRunnerConvention()
    {
        WriteSubject(immutableResultRef: null);

        var resolved = DeliveryRefResolver.Resolve("slug-that-is-not-a-ref", _folder);

        Assert.Equal("runner/attributed/AGT-42", resolved.Ref);
        Assert.Equal(DeliveryRefSource.AttributedCommit, resolved.Source);
        Assert.True(resolved.IsRemote);
    }

    [Fact]
    public void Resolve_UsesRunnerConventionBeforeLocalSlugFallback()
    {
        WriteCard(branch: null);
        WriteSubject(immutableResultRef: null);

        var resolved = DeliveryRefResolver.Resolve("slug-that-is-not-a-ref", _folder);

        Assert.Equal("runner/agent-runner-01/AGT-42", resolved.Ref);
        Assert.Equal(DeliveryRefSource.RunnerConvention, resolved.Source);
        Assert.True(resolved.IsRemote);
    }

    [Fact]
    public void Resolve_UsesTaskSlugOnlyAsFinalLocalFallback()
    {
        WriteCard(branch: null);
        File.Delete(ReviewSubjectStore.PathFor(_folder));

        var resolved = DeliveryRefResolver.Resolve("slug-that-is-not-a-ref", _folder);

        Assert.Equal(
            WorktreeTaskLifecycle.BranchFor("slug-that-is-not-a-ref"),
            resolved.Ref);
        Assert.Equal(DeliveryRefSource.LocalTaskFallback, resolved.Source);
        Assert.False(resolved.IsRemote);
    }

    private void WriteCard(string? branch)
    {
        var commit = new
        {
            sha = new string('b', 40),
            shortSha = new string('b', 8),
            message = "attributed delivery",
            branch,
            filesChanged = 1,
            files = Array.Empty<string>(),
            at = DateTime.UtcNow,
            attribution = CommitAttributionKinds.Automatic,
            confidence = 1,
        };
        File.WriteAllText(
            Path.Combine(_folder, "task.json"),
            JsonSerializer.Serialize(
                new
                {
                    id = "slug-that-is-not-a-ref",
                    key = "AGT-42",
                    commits = new[] { commit },
                    commit,
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private void WriteSubject(string? immutableResultRef)
    {
        ReviewSubjectStore.Write(_folder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-42",
            Project = "Fixture",
            Repository = "fixture",
            ResultSha = new string('a', 40),
            AttemptChainId = "attempt-42",
            Executor = "agent-runner-01",
            LeaseId = "lease-42",
            FencingToken = 1,
            ImmutableResultRef = immutableResultRef,
            ResultRef = "legacy/salvage/ref",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }
}
