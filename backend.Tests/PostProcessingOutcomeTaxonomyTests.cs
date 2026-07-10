using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the post-processing outcome taxonomy (AGT-1944): the pure classifier +
/// environmental-retry decider that turns a finished run into one of the five
/// buckets (success / code-defect / environmental / inconclusive-with-results /
/// inconclusive-empty) and decides retry-with-backoff vs escalate for
/// environmental faults. One case per outcome, per the neighbour-test pattern.
/// </summary>
public class PostProcessingOutcomeTaxonomyTests
{
    // ---- Classify: one case per outcome bucket ------------------------------

    [Fact]
    public void Classify_CleanTerminal_IsSuccess()
    {
        var outcome = PostProcessingOutcomeTaxonomy.Classify(
            RunIssueKind.None, TerminalRunOutcomeKinds.Success, hasResults: false);
        Assert.Equal(PostProcessingOutcome.Success, outcome);
    }

    [Fact]
    public void Classify_SelfReportedBuildFailure_IsCodeDefect()
    {
        var outcome = PostProcessingOutcomeTaxonomy.Classify(
            RunIssueKind.None, TerminalRunOutcomeKinds.Failed, hasResults: false, hasCodeDefectEvidence: true);
        Assert.Equal(PostProcessingOutcome.CodeDefect, outcome);
    }

    [Fact]
    public void Classify_AgentGitViolation_IsCodeDefect()
    {
        var outcome = PostProcessingOutcomeTaxonomy.Classify(
            RunIssueKind.AgentGitViolation, TerminalRunOutcomeKinds.Failed, hasResults: false);
        Assert.Equal(PostProcessingOutcome.CodeDefect, outcome);
    }

    [Fact]
    public void Classify_TransientEnvironmentalFault_IsEnvironmental()
    {
        var outcome = PostProcessingOutcomeTaxonomy.Classify(
            RunIssueKind.EnvironmentalTransient, TerminalRunOutcomeKinds.Failed, hasResults: false);
        Assert.Equal(PostProcessingOutcome.Environmental, outcome);
    }

    [Fact]
    public void Classify_EnvironmentalWinsEvenWithResults()
    {
        // An infra fault must never be mislabelled inconclusive just because the
        // dying run happened to leave files behind.
        var outcome = PostProcessingOutcomeTaxonomy.Classify(
            RunIssueKind.QuotaExhausted, TerminalRunOutcomeKinds.Failed, hasResults: true);
        Assert.Equal(PostProcessingOutcome.Environmental, outcome);
    }

    [Fact]
    public void Classify_InconclusiveWithResults_RoutesToResultsBucket()
    {
        var outcome = PostProcessingOutcomeTaxonomy.Classify(
            RunIssueKind.OrchestratorInconclusive, TerminalRunOutcomeKinds.Failed, hasResults: true);
        Assert.Equal(PostProcessingOutcome.InconclusiveWithResults, outcome);
    }

    [Fact]
    public void Classify_InconclusiveEmpty_RoutesToEmptyBucket()
    {
        var outcome = PostProcessingOutcomeTaxonomy.Classify(
            RunIssueKind.OrchestratorInconclusive, TerminalRunOutcomeKinds.Failed, hasResults: false);
        Assert.Equal(PostProcessingOutcome.InconclusiveEmpty, outcome);
    }

    [Fact]
    public void Classify_InfraCrashWithResults_RoutesToResultsBucket()
    {
        var outcome = PostProcessingOutcomeTaxonomy.Classify(
            RunIssueKind.InfraCrash, TerminalRunOutcomeKinds.Failed, hasResults: true);
        Assert.Equal(PostProcessingOutcome.InconclusiveWithResults, outcome);
    }

    // ---- Membership predicates ----------------------------------------------

    [Theory]
    [InlineData(RunIssueKind.EnvironmentalTransient)]
    [InlineData(RunIssueKind.CliLaunchFailed)]
    public void RetryableEnvironmental_TransientAndLaunch(RunIssueKind kind)
        => Assert.True(PostProcessingOutcomeTaxonomy.IsRetryableEnvironmental(kind));

    [Theory]
    [InlineData(RunIssueKind.QuotaExhausted)]
    [InlineData(RunIssueKind.ModelInvalid)]
    [InlineData(RunIssueKind.ContextOverflow)]
    [InlineData(RunIssueKind.EnvironmentBlocker)]
    [InlineData(RunIssueKind.OrchestratorInconclusive)]
    [InlineData(RunIssueKind.None)]
    public void RetryableEnvironmental_ExcludesNonTransient(RunIssueKind kind)
        => Assert.False(PostProcessingOutcomeTaxonomy.IsRetryableEnvironmental(kind));

    [Theory]
    [InlineData(RunIssueKind.EnvironmentalTransient)]
    [InlineData(RunIssueKind.CliLaunchFailed)]
    [InlineData(RunIssueKind.EnvironmentBlocker)]
    [InlineData(RunIssueKind.QuotaExhausted)]
    [InlineData(RunIssueKind.ModelInvalid)]
    [InlineData(RunIssueKind.ContextOverflow)]
    [InlineData(RunIssueKind.EmptyFastExit)]
    public void IsEnvironmental_IncludesInfraProviderCliKinds(RunIssueKind kind)
        => Assert.True(PostProcessingOutcomeTaxonomy.IsEnvironmental(kind));

    [Theory]
    [InlineData(RunIssueKind.None)]
    [InlineData(RunIssueKind.AgentGitViolation)]
    [InlineData(RunIssueKind.OrchestratorInconclusive)]
    [InlineData(RunIssueKind.InfraCrash)]
    [InlineData(RunIssueKind.MissingTerminalSentinel)]
    public void IsEnvironmental_ExcludesCodeAndInconclusiveKinds(RunIssueKind kind)
        => Assert.False(PostProcessingOutcomeTaxonomy.IsEnvironmental(kind));

    // ---- DecideEnvironmentalRetry -------------------------------------------

    [Fact]
    public void EnvironmentalTransient_FirstFailure_RetriesWithBackoff()
    {
        var decision = PostProcessingOutcomeTaxonomy.DecideEnvironmentalRetry(
            RunIssueKind.EnvironmentalTransient, priorRetryAttempt: 0);
        Assert.Equal(EnvironmentalRetryAction.RetryWithBackoff, decision.Action);
        Assert.Equal(1, decision.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(30), decision.Backoff);
    }

    [Fact]
    public void EnvironmentalTransient_SecondFailure_RetriesWithLongerBackoff()
    {
        var decision = PostProcessingOutcomeTaxonomy.DecideEnvironmentalRetry(
            RunIssueKind.EnvironmentalTransient, priorRetryAttempt: 1);
        Assert.Equal(EnvironmentalRetryAction.RetryWithBackoff, decision.Action);
        Assert.Equal(2, decision.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(120), decision.Backoff);
    }

    [Fact]
    public void EnvironmentalTransient_BudgetSpent_Escalates()
    {
        var decision = PostProcessingOutcomeTaxonomy.DecideEnvironmentalRetry(
            RunIssueKind.EnvironmentalTransient,
            priorRetryAttempt: PostProcessingOutcomeTaxonomy.DefaultMaxEnvironmentalRetries);
        Assert.Equal(EnvironmentalRetryAction.Escalate, decision.Action);
    }

    [Fact]
    public void CliLaunchFailed_FirstFailure_RetriesOnce()
    {
        var decision = PostProcessingOutcomeTaxonomy.DecideEnvironmentalRetry(
            RunIssueKind.CliLaunchFailed, priorRetryAttempt: 0);
        Assert.Equal(EnvironmentalRetryAction.RetryWithBackoff, decision.Action);
        Assert.Equal(1, decision.Attempt);
    }

    [Fact]
    public void CliLaunchFailed_AfterOneRetry_Escalates()
    {
        var decision = PostProcessingOutcomeTaxonomy.DecideEnvironmentalRetry(
            RunIssueKind.CliLaunchFailed, priorRetryAttempt: PostProcessingOutcomeTaxonomy.MaxCliLaunchRetries);
        Assert.Equal(EnvironmentalRetryAction.Escalate, decision.Action);
    }

    [Theory]
    [InlineData(RunIssueKind.QuotaExhausted)]
    [InlineData(RunIssueKind.ModelInvalid)]
    [InlineData(RunIssueKind.ContextOverflow)]
    [InlineData(RunIssueKind.EnvironmentBlocker)]
    public void NonRetryableEnvironmental_EscalatesOnFirstDetection(RunIssueKind kind)
    {
        var decision = PostProcessingOutcomeTaxonomy.DecideEnvironmentalRetry(kind, priorRetryAttempt: 0);
        Assert.Equal(EnvironmentalRetryAction.Escalate, decision.Action);
        Assert.Equal(TimeSpan.Zero, decision.Backoff);
    }

    // ---- DecidePostStepVerdictRetry (AGT-2021) ------------------------------

    [Fact]
    public void PostStepVerdict_FirstMiss_RetriesOnceWithBackoff()
    {
        // A missing / unparseable post-step verdict (dead reviewer) reruns exactly
        // once with the environmental backoff before it may escalate.
        var decision = PostProcessingOutcomeTaxonomy.DecidePostStepVerdictRetry(priorRetryAttempt: 0);
        Assert.Equal(EnvironmentalRetryAction.RetryWithBackoff, decision.Action);
        Assert.Equal(1, decision.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(30), decision.Backoff);
    }

    [Fact]
    public void PostStepVerdict_AfterOneRetry_EscalatesAsInfraCrash()
    {
        // The retry budget is exactly one; a second miss escalates (the caller
        // records an InfraCrash flagged environmental).
        var decision = PostProcessingOutcomeTaxonomy.DecidePostStepVerdictRetry(
            priorRetryAttempt: PostProcessingOutcomeTaxonomy.MaxPostStepVerdictRetries);
        Assert.Equal(EnvironmentalRetryAction.Escalate, decision.Action);
        Assert.Equal(TimeSpan.Zero, decision.Backoff);
    }

    [Fact]
    public void PostStepVerdict_BudgetIsExactlyOne()
        => Assert.Equal(1, PostProcessingOutcomeTaxonomy.MaxPostStepVerdictRetries);

    // ---- RetryBackoff curve --------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 300)]   // 30*4^2 = 480, capped at 300
    [InlineData(9, 300)]   // stays capped
    public void RetryBackoff_IsExponentialAndCapped(int attempt, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), PostProcessingOutcomeTaxonomy.RetryBackoff(attempt));
}
