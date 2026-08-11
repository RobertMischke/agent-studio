using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Git;
using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2494 - a divergent salvage must not report a ref that cannot carry its
/// own result. On 28.07. AGT-2220 completed with
/// <c>resultSha=f538f896</c> on <c>resultRef=runner/agent-runner-01/AGT-2220</c>
/// while that ref held <c>744deb89</c>; the result lived on the
/// <c>...-collision-&lt;sha&gt;-&lt;sha&gt;</c> branch the reconciliation had just
/// published. The review subject was unresolvable by construction
/// (<c>immutable-result-mismatch</c>, empty <c>commits[]</c>).
/// </summary>
public sealed class RemoteDeliveryRefPolicyTests
{
    // ---------------------------------------------------------------
    // Ranking - pure, no repository involved.
    // ---------------------------------------------------------------

    [Fact]
    public void Candidates_ImmutableResultRef_OutranksEverySalvageRef()
    {
        var candidates = RemoteDeliveryRefPolicy.Candidates(
            ImmutableRef, "divergent", CanonicalBranch, RecoveryBranch, LocalSha, LocalSha);

        Assert.Equal(
            [ImmutableRef, RecoveryBranch, CanonicalBranch],
            candidates.Select(candidate => candidate.Ref));
        Assert.Equal(RemoteDeliveryRefOrigin.ImmutableResult, candidates[0].Origin);
    }

    /// <summary>
    /// The core AGT-2494 ranking: for a divergent resolution the reconciliation
    /// itself recorded that the canonical branch kept the remote tip, so the
    /// collision branch is the only reported ref that carries the result.
    /// </summary>
    [Fact]
    public void Candidates_DivergentSalvage_RanksRecoveryBranchAheadOfTheCanonicalBranch()
    {
        var candidates = RemoteDeliveryRefPolicy.Candidates(
            immutableResultRef: null,
            "divergent",
            CanonicalBranch,
            RecoveryBranch,
            LocalSha,
            LocalSha);

        Assert.Equal([RecoveryBranch, CanonicalBranch], candidates.Select(candidate => candidate.Ref));
        Assert.Equal(RemoteDeliveryRefOrigin.SalvageRecovery, candidates[0].Origin);
        Assert.Equal(RemoteDeliveryRefOrigin.SalvageBranch, candidates[1].Origin);
    }

    /// <summary>
    /// Equal, local-ahead, remote-ahead and generation-scoped resolutions leave
    /// the result on the canonical branch. Promoting a recovery ref there would
    /// invent a claim the reconciliation never made.
    /// </summary>
    [Theory]
    [InlineData("equal")]
    [InlineData("local-ahead")]
    [InlineData("remote-ahead")]
    [InlineData("generation-scoped")]
    [InlineData("quarantined")]
    [InlineData(null)]
    public void Candidates_NonDivergentResolution_KeepsTheCanonicalBranchFirst(string? resolution)
    {
        var candidates = RemoteDeliveryRefPolicy.Candidates(
            immutableResultRef: null, resolution, CanonicalBranch, RecoveryBranch, LocalSha, LocalSha);

        Assert.Equal([CanonicalBranch], candidates.Select(candidate => candidate.Ref));
    }

    /// <summary>
    /// A recovery branch is only promoted when the runner reported that it holds
    /// exactly the fenced result SHA. Anything else is recovery evidence, not a
    /// delivery claim.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(CanonicalSha)]
    public void Candidates_RecoveryBranchThatDoesNotCarryTheResult_IsNotPromoted(
        string? recoveryCommitSha)
    {
        var candidates = RemoteDeliveryRefPolicy.Candidates(
            immutableResultRef: null,
            "divergent",
            CanonicalBranch,
            RecoveryBranch,
            recoveryCommitSha,
            LocalSha);

        Assert.Equal([CanonicalBranch], candidates.Select(candidate => candidate.Ref));
    }

    [Fact]
    public void Candidates_RepeatedRef_IsRankedOnce()
    {
        var candidates = RemoteDeliveryRefPolicy.Candidates(
            CanonicalBranch, "divergent", CanonicalBranch, CanonicalBranch, LocalSha, LocalSha);

        Assert.Equal([CanonicalBranch], candidates.Select(candidate => candidate.Ref));
        Assert.Equal(RemoteDeliveryRefOrigin.ImmutableResult, candidates[0].Origin);
    }

    [Fact]
    public void Candidates_NothingReported_IsEmpty()
    {
        var candidates = RemoteDeliveryRefPolicy.Candidates(
            immutableResultRef: null,
            salvageResolution: "divergent",
            salvageBranch: "   ",
            salvageRecoveryBranch: null,
            salvageRecoveryCommitSha: LocalSha,
            resultSha: LocalSha);

        Assert.Empty(candidates);
    }

    // ---------------------------------------------------------------
    // Selection - the repository decides, with injected verdicts.
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(DeliveryVerificationStatus.Verified)]
    [InlineData(DeliveryVerificationStatus.VerifiedContained)]
    public void Select_TopCandidateCarriesTheResult_IsTakenWithoutLookingFurther(
        DeliveryVerificationStatus status)
    {
        var inspected = new List<string>();

        var selection = RemoteDeliveryRefPolicy.Select(
            Ranked((ImmutableRef, RemoteDeliveryRefOrigin.ImmutableResult),
                   (RecoveryBranch, RemoteDeliveryRefOrigin.SalvageRecovery)),
            gitRef =>
            {
                inspected.Add(gitRef);
                return Verdict(gitRef, status);
            });

        Assert.Equal(ImmutableRef, selection.Ref);
        Assert.True(selection.CarriesResult);
        Assert.Equal([ImmutableRef], inspected);
    }

    /// <summary>
    /// The repair AGT-2494 asks for: the best-ranked claim is contradicted, so
    /// the walk continues to the ref that provably holds the SHA instead of
    /// reporting an unresolvable subject.
    /// </summary>
    [Theory]
    [InlineData(DeliveryVerificationStatus.RefMissing)]
    [InlineData(DeliveryVerificationStatus.ShaMismatch)]
    [InlineData(DeliveryVerificationStatus.CommitMissing)]
    public void Select_DisprovedCandidate_YieldsToTheRefThatHoldsTheSha(
        DeliveryVerificationStatus disproof)
    {
        var selection = RemoteDeliveryRefPolicy.Select(
            Ranked((ImmutableRef, RemoteDeliveryRefOrigin.ImmutableResult),
                   (RecoveryBranch, RemoteDeliveryRefOrigin.SalvageRecovery),
                   (CanonicalBranch, RemoteDeliveryRefOrigin.SalvageBranch)),
            gitRef => Verdict(
                gitRef,
                gitRef == RecoveryBranch ? DeliveryVerificationStatus.Verified : disproof));

        Assert.Equal(RecoveryBranch, selection.Ref);
        Assert.Equal(RemoteDeliveryRefOrigin.SalvageRecovery, selection.Origin);
        Assert.True(selection.CarriesResult);
    }

    /// <summary>
    /// "Could not look" is never disproof, so it must not push the claim down to
    /// a lower-ranked ref either. The walk stops and the rank stands.
    /// </summary>
    [Fact]
    public void Select_UnverifiableCandidate_KeepsItsRankAndStopsTheWalk()
    {
        var inspected = new List<string>();

        var selection = RemoteDeliveryRefPolicy.Select(
            Ranked((ImmutableRef, RemoteDeliveryRefOrigin.ImmutableResult),
                   (RecoveryBranch, RemoteDeliveryRefOrigin.SalvageRecovery)),
            gitRef =>
            {
                inspected.Add(gitRef);
                return DeliveryVerificationResult.NotVerifiable("no origin", LocalSha, gitRef);
            });

        Assert.Equal(ImmutableRef, selection.Ref);
        Assert.False(selection.CarriesResult);
        Assert.Equal([ImmutableRef], inspected);
    }

    /// <summary>
    /// Every reported ref is contradicted: the best-ranked claim is returned
    /// unchanged so the AGT-2220 disproof gate escalates the card honestly
    /// instead of a lower-ranked ref laundering the delivery into 4-auto-review.
    /// </summary>
    [Fact]
    public void Select_AllCandidatesDisproved_KeepsTheBestRankedClaim()
    {
        var selection = RemoteDeliveryRefPolicy.Select(
            Ranked((ImmutableRef, RemoteDeliveryRefOrigin.ImmutableResult),
                   (CanonicalBranch, RemoteDeliveryRefOrigin.SalvageBranch)),
            gitRef => Verdict(gitRef, DeliveryVerificationStatus.RefMissing));

        Assert.Equal(ImmutableRef, selection.Ref);
        Assert.Equal(DeliveryVerificationStatus.RefMissing, selection.Verification);
        Assert.False(selection.CarriesResult);
    }

    [Fact]
    public void Select_NoCandidate_ClaimsNoRefAtAll()
    {
        var selection = RemoteDeliveryRefPolicy.Select(
            [],
            _ => throw new InvalidOperationException("An empty candidate list must not be inspected."));

        Assert.Null(selection.Ref);
        Assert.Equal(RemoteDeliveryRefOrigin.None, selection.Origin);
        Assert.False(selection.CarriesResult);
    }

    // ---------------------------------------------------------------
    // The AGT-2220 shape against real git.
    // ---------------------------------------------------------------

    /// <summary>
    /// Abnahme 1: a divergent salvage reports a ref on which the result SHA
    /// provably lies. The immutable result ref is gone from origin (retention
    /// GC), the canonical branch holds the remote tip it collided with - the
    /// collision branch is the one that carries the result.
    /// </summary>
    [Fact]
    public void DivergentSalvage_ReportsARefThatProvablyCarriesTheResultSha()
    {
        WithDivergentSalvageFixture((git, repo, fixture) =>
        {
            var selection = RemoteDeliveryRefPolicy.Select(
                RemoteDeliveryRefPolicy.Candidates(
                    fixture.CollectedImmutableRef,
                    "divergent",
                    CanonicalBranch,
                    fixture.RecoveryBranch,
                    fixture.ResultSha,
                    fixture.ResultSha),
                candidate => git.VerifyDeliveredCommit(repo, candidate, fixture.ResultSha));

            Assert.Equal(fixture.RecoveryBranch, selection.Ref);
            Assert.Equal(RemoteDeliveryRefOrigin.SalvageRecovery, selection.Origin);
            Assert.True(selection.CarriesResult);

            // The claim is not merely ranked - the repository confirms it.
            var proof = git.VerifyDeliveredCommit(repo, selection.Ref, fixture.ResultSha);
            Assert.True(proof.IsVerified);
            Assert.Equal(fixture.ResultSha, proof.ResolvedRefSha);
        });
    }

    /// <summary>
    /// Abnahme 2: the review subject of a divergent delivery resolves. Inspecting
    /// the selected ref yields a fenced commit range instead of the ShaMismatch
    /// that emptied AGT-2220's <c>commits[]</c> - which the canonical branch,
    /// asserted alongside, still produces.
    /// </summary>
    [Fact]
    public void DivergentSalvage_ReviewSubjectResolves_WhereTheCanonicalBranchWouldShaMismatch()
    {
        WithDivergentSalvageFixture((git, repo, fixture) =>
        {
            var selection = RemoteDeliveryRefPolicy.Select(
                RemoteDeliveryRefPolicy.Candidates(
                    fixture.CollectedImmutableRef,
                    "divergent",
                    CanonicalBranch,
                    fixture.RecoveryBranch,
                    fixture.ResultSha,
                    fixture.ResultSha),
                candidate => git.VerifyDeliveredCommit(repo, candidate, fixture.ResultSha));

            var resolved = git.InspectRemoteDeliveryCommitRange(
                repo, selection.Ref!, fixture.ResultSha, "refs/heads/main");

            Assert.True(resolved.Success, resolved.Warning);
            Assert.Equal(DeliveryVerificationStatus.Verified, resolved.Verification);
            Assert.False(resolved.IsDisproved);
            Assert.Equal(fixture.ResultSha, resolved.TipSha);
            Assert.NotEmpty(resolved.Commits);

            // The ref the completion used to report is exactly the AGT-2220
            // failure: it exists, it just never held this result.
            var canonical = git.InspectRemoteDeliveryCommitRange(
                repo, CanonicalBranch, fixture.ResultSha, "refs/heads/main");
            Assert.Equal(DeliveryVerificationStatus.ShaMismatch, canonical.Verification);
            Assert.Empty(canonical.Commits);
        });
    }

    // ---------------------------------------------------------------
    // Fixture helpers
    // ---------------------------------------------------------------

    private const string CanonicalBranch = "runner/agent-runner-01/AGT-2220";
    private const string ImmutableRef =
        "refs/heads/agent-studio/results/run-2220/fence-3/"
        + "f538f896f538f896f538f896f538f896f538f896";
    private const string LocalSha = "f538f896f538f896f538f896f538f896f538f896";
    private const string CanonicalSha = "744deb892744deb892744deb892744deb892744d";
    private const string RecoveryBranch =
        CanonicalBranch + "-collision-" + LocalSha + "-" + CanonicalSha;

    private sealed record DivergentFixture(
        string ResultSha,
        string RecoveryBranch,
        string? CollectedImmutableRef);

    /// <summary>
    /// Reproduces the reconciliation on origin: <c>main</c>, a canonical delivery
    /// branch that moved on to a foreign tip, and the collision branch the
    /// divergent salvage published this run's result to. The immutable result ref
    /// is deliberately absent from origin - the completion still reported it, but
    /// retention GC has since removed it.
    /// </summary>
    private static void WithDivergentSalvageFixture(
        Action<GitService, string, DivergentFixture> assert)
    {
        var root = NewTempRoot("divergent-salvage-");
        var remote = Path.Combine(root, "origin.git");
        var repo = Path.Combine(root, "repo");
        try
        {
            Directory.CreateDirectory(root);
            RunGit(root, "init", "-q", "--bare", "--initial-branch=main", remote);
            RunGit(root, "clone", "-q", remote, repo);
            RunGit(repo, "config", "user.email", "test@example.com");
            RunGit(repo, "config", "user.name", "Test");
            RunGit(repo, "checkout", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(repo, "base.txt"), "base");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-q", "-m", "chore: base");
            var baseSha = RunGit(repo, "rev-parse", "HEAD");
            RunGit(repo, "push", "-q", "origin", "main");

            // The canonical branch as origin holds it: a foreign tip the salvage
            // collided with and refused to overwrite.
            RunGit(repo, "checkout", "-q", "-b", CanonicalBranch, baseSha);
            File.WriteAllText(Path.Combine(repo, "foreign.txt"), "foreign");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-q", "-m", "feat: foreign tip that won the canonical ref");
            var canonicalSha = RunGit(repo, "rev-parse", "HEAD");
            RunGit(repo, "push", "-q", "origin", CanonicalBranch);

            // This run's own result, parked on the collision branch the
            // reconciliation names: <card-branch>-collision-<local>-<remote>.
            RunGit(repo, "checkout", "-q", "-b", "salvage-work", baseSha);
            File.WriteAllText(Path.Combine(repo, "result.txt"), "result");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-q", "-m", "feat(AGT-2220): the delivered result");
            var resultSha = RunGit(repo, "rev-parse", "HEAD");
            var recoveryBranch = $"{CanonicalBranch}-collision-{resultSha}-{canonicalSha}";
            RunGit(repo, "push", "-q", "origin", $"HEAD:refs/heads/{recoveryBranch}");
            RunGit(repo, "checkout", "-q", "main");

            assert(
                NewGitService(repo),
                repo,
                new DivergentFixture(
                    resultSha,
                    recoveryBranch,
                    CollectedImmutableRef: ImmutableRef));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static IReadOnlyList<RemoteDeliveryRefCandidate> Ranked(
        params (string Ref, RemoteDeliveryRefOrigin Origin)[] candidates)
        => candidates
            .Select(candidate => new RemoteDeliveryRefCandidate(candidate.Ref, candidate.Origin))
            .ToArray();

    private static DeliveryVerificationResult Verdict(
        string gitRef, DeliveryVerificationStatus status)
        => new(status, null, LocalSha, gitRef, LocalSha);

    private static GitService NewGitService(string repo)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "Fixture",
                ["WatchPaths:0:RootPath"] = repo,
                ["WatchPaths:0:RepositoryPath"] = repo,
                ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
            }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static string NewTempRoot(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "RemoteDeliveryRefPolicyTests: fixture cleanup is best-effort");
        }
    }

    private static string RunGit(string cwd, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }
}
