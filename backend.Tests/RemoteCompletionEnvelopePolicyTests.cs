using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteCompletionEnvelopePolicyTests
{
    [Theory]
    [InlineData("done")]
    [InlineData("noop")]
    public void Successful_coding_completion_with_the_full_envelope_is_persisted(string outcome)
    {
        var decision = RemoteCompletionEnvelopePolicy.Decide(
            requiresEnvelope: true,
            outcome,
            runAttemptKnown: true,
            hasBaseSha: true,
            hasImmutableResultRef: true,
            hasArtifactManifestDigest: true);

        Assert.Equal(RemoteCompletionEnvelopeDisposition.Persist, decision.Disposition);
        Assert.Equal(outcome, decision.AuthorityOutcome);
        Assert.Null(decision.Reason);
    }

    [Theory]
    [InlineData(false, true, true, true, "RunAttemptAuthority")]
    [InlineData(true, false, true, true, "BaseSha")]
    [InlineData(true, true, false, true, "ImmutableResultRef")]
    [InlineData(true, true, true, false, "ArtifactManifestDigest")]
    public void Successful_coding_completion_with_any_missing_envelope_fact_is_unverified(
        bool runAttemptKnown,
        bool hasBaseSha,
        bool hasImmutableResultRef,
        bool hasArtifactManifestDigest,
        string missingFact)
    {
        var decision = RemoteCompletionEnvelopePolicy.Decide(
            requiresEnvelope: true,
            "done",
            runAttemptKnown,
            hasBaseSha,
            hasImmutableResultRef,
            hasArtifactManifestDigest);

        Assert.Equal(RemoteCompletionEnvelopeDisposition.EscalateUnverified, decision.Disposition);
        Assert.Equal("unverified", decision.AuthorityOutcome);
        Assert.Contains(missingFact, decision.Reason);
        Assert.Contains("no ReviewSubject was created", decision.Reason);
    }

    [Theory]
    [InlineData(false, "done")]
    [InlineData(true, "blocked")]
    [InlineData(true, "unknown")]
    public void Non_coding_or_non_success_completion_does_not_require_an_envelope(
        bool requiresEnvelope,
        string outcome)
    {
        var decision = RemoteCompletionEnvelopePolicy.Decide(
            requiresEnvelope,
            outcome,
            runAttemptKnown: false,
            hasBaseSha: false,
            hasImmutableResultRef: false,
            hasArtifactManifestDigest: false);

        Assert.Equal(RemoteCompletionEnvelopeDisposition.NotRequired, decision.Disposition);
        Assert.Equal(outcome, decision.AuthorityOutcome);
        Assert.Null(decision.Reason);
    }
}
