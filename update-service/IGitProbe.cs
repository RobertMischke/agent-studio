namespace AgentTaskboard.UpdateService;

/// <summary>
/// Read-only view onto the stable checkout's git state.
///
/// ADR-0031 follow-up: extracted so the integration suite under
/// <c>update-service-full-integration-test--live-phase-label-probe</c> can
/// substitute a scripted probe in WebApplicationFactory-hosted tests
/// without spinning up a real git remote for the readonly parts of the
/// pipeline. The bash-invoked pull/reset still hits a real fake checkout
/// in the test harness; only the HEAD short/fetch-compare/pending-commits
/// reads run through this seam.
/// </summary>
public interface IGitProbe
{
    string HeadShort();
    (string Origin, int BehindBy) FetchAndCompare();
    IReadOnlyList<CommitInfo> PendingCommits(int max = 50);
    VersionTopology ReadVersionTopology(string runningCommit);
}

public sealed record VersionTopology(BranchVersion Main, BranchVersion Develop);
