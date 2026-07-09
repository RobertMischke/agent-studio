using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the per-attempt-chain reissue budget (AGT-1935 sticky-budget belege):
/// <see cref="ReviewDecisionOrchestrator.CountReissuesInCurrentChain"/> counts the
/// reissues recorded SINCE the most recent chain-ending verdict
/// (Escalate / AcceptAsDone), not the job's whole lifetime total. A closed chain
/// must not spend a reopened card's fresh budget, while in-chain counting stays
/// identical to the old lifetime total when no chain-ender sits in between.
/// </summary>
public class ReviewDecisionChainBudgetTests
{
    private const string Job = "job-1";
    private const string Other = "job-2";

    private static ReviewDecisionRecord Rec(ReviewDecisionKind kind, string jobId = Job)
        => new(
            CreatedAt: DateTime.UnixEpoch,
            JobId: jobId,
            Project: "demo",
            Kind: kind,
            Reason: kind.ToString(),
            Prompt: string.Empty,
            Response: string.Empty,
            FollowUp: string.Empty);

    [Fact]
    public void NoRecords_IsZero()
        => Assert.Equal(0, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(
            Array.Empty<ReviewDecisionRecord>(), Job));

    [Fact]
    public void OnlyReissues_CountsAll_LikeLifetimeTotal()
    {
        // No chain-ender in between: per-chain count == the old lifetime total, so
        // in-chain budget behaviour is unchanged.
        var records = new[]
        {
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Reissue),
        };
        Assert.Equal(3, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, Job));
    }

    [Fact]
    public void ReissuesBeforeEscalate_DoNotCount_AfterReset()
    {
        // Two reissues then an Escalate closed the FIRST chain. A human reopened
        // the card and it reissued once more: only that last reissue is the
        // current chain's budget - the pre-Escalate ones must not stick.
        var records = new[]
        {
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Escalate),
            Rec(ReviewDecisionKind.Reissue),
        };
        Assert.Equal(1, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, Job));
    }

    [Fact]
    public void AcceptAsDone_AlsoResetsTheChain()
    {
        var records = new[]
        {
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.AcceptAsDone),
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Reissue),
        };
        Assert.Equal(2, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, Job));
    }

    [Fact]
    public void ChainEnderAfterLastReissue_ResetsToZero()
    {
        // The most recent verdict is a chain-ender: the current chain is empty, so
        // a card reopened from here starts with a full budget again.
        var records = new[]
        {
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Escalate),
        };
        Assert.Equal(0, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, Job));
    }

    [Fact]
    public void Skipped_IsNotAChainBoundary()
    {
        // Skipped leaves the card for the normal sentinel path: it neither counts
        // nor resets, so the two reissues around it are one continuous chain.
        var records = new[]
        {
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Skipped),
            Rec(ReviewDecisionKind.Reissue),
        };
        Assert.Equal(2, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, Job));
    }

    [Fact]
    public void CountsOnlyTheRequestedJob()
    {
        // Another job's records - including its chain-enders - must not touch this
        // job's count.
        var records = new[]
        {
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Escalate, Other),
            Rec(ReviewDecisionKind.Reissue, Other),
            Rec(ReviewDecisionKind.Reissue),
        };
        Assert.Equal(2, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, Job));
    }
}
