using AgentStudio.Review;

using Xunit;

namespace AgentStudio.Tests;

public sealed class CouncilReviewPolicyTests
{
    [Fact]
    public void GradeBWithNamedDeficiencies_ReissuesWithOneAssessmentPerFinding()
    {
        var findings = new[]
        {
            "Dark-theme colors are incorrect; fix them and provide both-theme screenshots.",
            "Upload rejection lacks focused test evidence; add the missing regression test.",
            "The Playwright inventory is ambiguous; make the executed spec list explicit.",
        };

        var reaction = CouncilReviewPolicy.Derive(
            "code-review-grade.md", CodeReviewGrade.B, findings,
            priorReissues: 0, maxReissues: 2, jobId: "AGT-2108",
            now: new DateTime(2026, 7, 23, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(CouncilReactionDisposition.Reissue, reaction.Disposition);
        Assert.True(reaction.StartsNewRound);
        Assert.Equal("AGT-2108", reaction.TargetJobId);
        Assert.Equal(2, reaction.TargetRunAttempt);
        Assert.Equal(3, reaction.Assessments.Count);
        Assert.All(reaction.Assessments, item => Assert.Equal(CouncilFindingAction.FixNextRound, item.Action));

        var followUp = CouncilReviewPolicy.BuildTargetedFollowUp(reaction);
        Assert.All(findings, finding => Assert.Contains(finding, followUp));
        Assert.DoesNotContain("full review", followUp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReissueTargetsTheActualNextPipelineAttempt_NotTheChainReissueCount()
    {
        var reaction = CouncilReviewPolicy.Derive(
            "code-review-grade.md", CodeReviewGrade.B, new[] { "Dark theme is wrong." },
            priorReissues: 0, maxReissues: 2, jobId: "AGT-2108", targetRunAttempt: 7);

        Assert.Equal(7, reaction.TargetRunAttempt);
    }

    [Fact]
    public void GradeAWithoutFindings_AcceptsNothingOpenWithoutStartingRound()
    {
        var reaction = CouncilReviewPolicy.Derive(
            "code-review-grade.md", CodeReviewGrade.A, Array.Empty<string>(),
            priorReissues: 0, maxReissues: 2, jobId: "AGT-2108");

        Assert.Equal(CouncilReactionDisposition.Accept, reaction.Disposition);
        Assert.Equal("Accept, nothing open.", reaction.Summary);
        Assert.False(reaction.StartsNewRound);
        Assert.Empty(reaction.Assessments);
    }

    [Fact]
    public void GradeAWithNamedDeficiency_ReissuesInsteadOfLettingThePassHideIt()
    {
        var reaction = CouncilReviewPolicy.Derive(
            "code-review-grade.md",
            CodeReviewGrade.A,
            new[] { "The dark theme still uses incorrect colors; provide both-theme screenshots." },
            priorReissues: 0,
            maxReissues: 2,
            jobId: "AGT-2108");

        Assert.Equal(CouncilReactionDisposition.Reissue, reaction.Disposition);
        Assert.True(reaction.StartsNewRound);
        Assert.Equal(CouncilFindingAction.FixNextRound, Assert.Single(reaction.Assessments).Action);
    }

    [Theory]
    [InlineData(CodeReviewGrade.B)]
    [InlineData(CodeReviewGrade.C)]
    [InlineData(CodeReviewGrade.D)]
    public void NonCleanGradeWithoutConcreteFindings_EscalatesInsteadOfOptimisticallyAccepting(
        CodeReviewGrade grade)
    {
        var reaction = CouncilReviewPolicy.Derive(
            "code-review-grade.md", grade, Array.Empty<string>(),
            priorReissues: 0, maxReissues: 2, jobId: "AGT-2108");

        Assert.Equal(CouncilReactionDisposition.Escalate, reaction.Disposition);
        Assert.False(reaction.StartsNewRound);
        var assessment = Assert.Single(reaction.Assessments);
        Assert.Equal(CouncilFindingAction.Escalate, assessment.Action);
        Assert.Contains("no concrete finding sentence", assessment.Finding, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReviewerInfrastructureFailure_IsExplicitButDoesNotInventAWorkDeficiency()
    {
        var reaction = CouncilReviewPolicy.Derive(
            "code-review-grade.md", CodeReviewGrade.C, Array.Empty<string>(),
            priorReissues: 0, maxReissues: 2, jobId: "AGT-2108", executionError: "CLI unavailable");

        Assert.Equal(CouncilReactionDisposition.Accept, reaction.Disposition);
        Assert.Contains("grade as unavailable", reaction.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(reaction.Assessments);
        Assert.False(reaction.StartsNewRound);
    }

    [Fact]
    public void NamedDeficienciesWithSpentBudget_EscalateEachFinding()
    {
        var reaction = CouncilReviewPolicy.Derive(
            "code-review-grade.md", CodeReviewGrade.C, new[] { "Missing regression coverage." },
            priorReissues: 2, maxReissues: 2, jobId: "AGT-2108");

        Assert.Equal(CouncilReactionDisposition.Escalate, reaction.Disposition);
        Assert.False(reaction.StartsNewRound);
        Assert.Equal(CouncilFindingAction.Escalate, Assert.Single(reaction.Assessments).Action);
    }

    [Fact]
    public void InfrastructureOverride_EscalatesEveryFindingWithoutClaimingANewRound()
    {
        var initial = CouncilReviewPolicy.Derive(
            "code-review-grade.md", CodeReviewGrade.B, new[] { "Dark theme is wrong." },
            priorReissues: 0, maxReissues: 2, jobId: "AGT-2108");

        var reaction = CouncilReviewPolicy.EscalateBecause(initial, "aspect review infrastructure failed");

        Assert.Equal(CouncilReactionDisposition.Escalate, reaction.Disposition);
        Assert.Equal(CouncilFindingAction.Escalate, Assert.Single(reaction.Assessments).Action);
        Assert.False(reaction.StartsNewRound);
        Assert.Null(reaction.TargetJobId);
        Assert.Null(reaction.TargetRunAttempt);
    }

    [Fact]
    public void FindingParserReturnsOnlyConcreteFindingSentinels()
    {
        var parsed = CodeReviewFindingParsing.Parse("""
            Review prose.
            [[CODE_REVIEW_FINDING: text=Dark theme is wrong; provide both-theme screenshots.]]
            [[CODE_REVIEW_FINDING: text=Upload rejection needs a focused test.]]
            [[CODE_REVIEW_GRADE: grade=B; summary=Two gaps remain.]]
            """);

        Assert.Equal(2, parsed.Count);
        Assert.Contains("Dark theme is wrong; provide both-theme screenshots.", parsed);
        Assert.Contains("Upload rejection needs a focused test.", parsed);
    }

    [Fact]
    public void ReactionSidecar_RoundTripsPerFindingAssessmentsAndTargetRoundLink()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "council-reaction-sidecar-" + Guid.NewGuid().ToString("N"));
        try
        {
            var reaction = CouncilReviewPolicy.Derive(
                "code-review-grade-2026-07-23.md",
                CodeReviewGrade.B,
                new[]
                {
                    "Dark-theme colors are incorrect; provide both-theme screenshots.",
                    "Upload rejection lacks focused test evidence; add the missing regression test.",
                },
                priorReissues: 0,
                maxReissues: 2,
                jobId: "AGT-2108",
                targetRunAttempt: 4,
                now: new DateTime(2026, 7, 23, 20, 0, 0, DateTimeKind.Utc));

            CouncilReviewReactionStore.Write(folder, reaction);
            var persisted = CouncilReviewReactionStore.Read(folder, reaction.ReviewFileName);

            Assert.NotNull(persisted);
            Assert.Equal(CouncilReactionDisposition.Reissue, persisted!.Disposition);
            Assert.True(persisted.StartsNewRound);
            Assert.Equal("AGT-2108", persisted.TargetJobId);
            Assert.Equal(4, persisted.TargetRunAttempt);
            Assert.Equal(2, persisted.Assessments.Count);
            Assert.All(
                persisted.Assessments,
                assessment => Assert.Equal(CouncilFindingAction.FixNextRound, assessment.Action));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
