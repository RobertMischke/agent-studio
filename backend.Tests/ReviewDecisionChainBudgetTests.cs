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

/// <summary>
/// End-to-end coverage of the per-attempt-chain reissue budget (AGT-1935) through
/// the REAL on-disk decision journal: records are appended with
/// <see cref="ReviewDecisionLog.Append"/> and counted back with
/// <see cref="ReviewDecisionOrchestrator.CountReissuesInCurrentChain"/> over
/// <see cref="ReviewDecisionLog.ReadAll"/> - the exact composition the private
/// production <c>CountPriorReissues(workspace, project, jobId)</c> performs. This
/// proves the chain reset survives the JSONL serialize/deserialize round-trip
/// (including the <see cref="ReviewDecisionKind"/> string-enum converter), which
/// the in-memory unit cases above do not exercise. It runs fully isolated in a
/// temp workspace - no live backend or integration host is required - which is the
/// integration coverage the AGT-1935 belege said was missing.
/// </summary>
public sealed class ReviewDecisionChainBudgetJournalTests : IDisposable
{
    private const string Project = "demo";
    private const string Job = "job-1";
    private readonly string _workspace;

    public ReviewDecisionChainBudgetJournalTests()
        => _workspace = Path.Combine(Path.GetTempPath(), "rdo-chain-budget-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    private void Append(ReviewDecisionKind kind, int minute, string jobId = Job)
        => ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc),
            JobId: jobId,
            Project: Project,
            Kind: kind,
            Reason: kind.ToString(),
            Prompt: "p",
            Response: "r",
            FollowUp: string.Empty));

    private int CurrentChainReissues(string jobId = Job)
        => ReviewDecisionOrchestrator.CountReissuesInCurrentChain(
            ReviewDecisionLog.ReadAll(_workspace, Project), jobId);

    [Fact]
    public void MissingJournal_CountsZero()
        => Assert.Equal(0, CurrentChainReissues());

    [Fact]
    public void PersistedReissuesBeforeEscalate_DoNotStick_AfterReopen()
    {
        // Old, already-resolved chain: two reissues then an Escalate parked the
        // card. A human reopened it and it reissued once more - only that last
        // reissue is the current chain's budget, even after a journal round-trip.
        Append(ReviewDecisionKind.Reissue, 1);
        Append(ReviewDecisionKind.Reissue, 2);
        Append(ReviewDecisionKind.Escalate, 3);
        Append(ReviewDecisionKind.Reissue, 4);

        Assert.Equal(1, CurrentChainReissues());
    }

    [Fact]
    public void PersistedChainWithoutEnder_MatchesLifetimeTotal()
    {
        // No chain-ender persisted: the per-chain count equals the old sticky
        // lifetime total, so in-chain budget behaviour is unchanged.
        Append(ReviewDecisionKind.Reissue, 1);
        Append(ReviewDecisionKind.Reissue, 2);

        Assert.Equal(2, CurrentChainReissues());
    }

    [Fact]
    public void PersistedOtherJobRecords_AreIgnored()
    {
        // A different job's persisted chain-ender must not reset this job's count.
        Append(ReviewDecisionKind.Reissue, 1);
        Append(ReviewDecisionKind.Escalate, 2, jobId: "job-2");
        Append(ReviewDecisionKind.Reissue, 3, jobId: "job-2");
        Append(ReviewDecisionKind.Reissue, 4);

        Assert.Equal(2, CurrentChainReissues());
    }
}
