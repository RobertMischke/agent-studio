

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the pure mapping from a settled <see cref="AspectRunReport"/> plus the
/// run's evidence strings into the structured <see cref="WikiLearningsRun"/> the
/// runner distills (<see cref="ReviewDecisionOrchestrator.BuildWikiLearningsRun"/>).
/// Static + internal, so the verdict derivation, finding projection, and evidence
/// trimming are verified without standing up the orchestrator. The on-disk render
/// of the resulting run is covered by <c>WikiLearningsPostStepRunnerTests</c>.
/// </summary>
public sealed class WikiLearningsRunMappingTests
{
    [Fact]
    public void Build_BlockReport_DerivesReissue_AndProjectsFindingsWithEvidence()
    {
        var report = new AspectRunReport(
            Verdicts:
            [
                new AspectVerdict("code-quality", AspectStatus.Block, "missing null guard", "body", "quality:block"),
                new AspectVerdict("tests-and-evidence", AspectStatus.Concerns, "thin coverage", "body", "quality:concerns"),
                new AspectVerdict("requirement-fit", AspectStatus.Pass, "matches spec", "body", null),
            ],
            Overall: AspectStatus.Block,
            ConcernTagIds: ["quality:block"],
            FollowUpSummary: "Add the null guard and a regression test.");

        var task = Task() with
        {
            Commits = [Commit("abc1234deadbeef", "feat: add wiki-learnings step")],
            OutcomeIssue = new TaskOutcomeIssue { Label = "Locked bin", Summary = "MSBuild could not copy the DLL" },
        };

        var run = ReviewDecisionOrchestrator.BuildWikiLearningsRun(
            report, task, statusSummary: "Implemented the deterministic step.", diffSummary: "1 file changed");

        Assert.Equal("reissue", run.Verdict);
        Assert.Equal("Add the null guard and a regression test.", run.VerdictReason);
        Assert.Collection(run.Findings,
            f => { Assert.Equal("code-quality", f.Aspect); Assert.Equal("block", f.Verdict); Assert.Equal("missing null guard", f.Reason); },
            f => { Assert.Equal("tests-and-evidence", f.Aspect); Assert.Equal("concerns", f.Verdict); Assert.Equal("thin coverage", f.Reason); },
            f => { Assert.Equal("requirement-fit", f.Aspect); Assert.Equal("pass", f.Verdict); Assert.Equal("matches spec", f.Reason); });
        Assert.Equal("Locked bin: MSBuild could not copy the DLL", run.StumblingBlock);
        Assert.Equal("Implemented the deterministic step.", run.AgentNotes);
        Assert.Equal("1 commit; latest abc1234: feat: add wiki-learnings step", run.ChangedSummary);
    }

    [Fact]
    public void Build_ConcernsReport_DerivesAcceptWithConcerns()
    {
        var report = new AspectRunReport(
            Verdicts: [new AspectVerdict("code-quality", AspectStatus.Concerns, "naming nit", "body", "quality:concerns")],
            Overall: AspectStatus.Concerns,
            ConcernTagIds: ["quality:concerns"],
            FollowUpSummary: "");

        var run = ReviewDecisionOrchestrator.BuildWikiLearningsRun(report, Task(), "", "");

        Assert.Equal("accept-with-concerns", run.Verdict);
        // Empty follow-up + no outcome issue collapse to null rather than blank strings.
        Assert.Null(run.VerdictReason);
        Assert.Null(run.StumblingBlock);
        Assert.Null(run.AgentNotes);
    }

    [Fact]
    public void Build_PassReport_NoCommits_FallsBackToDiffSummaryHeadline()
    {
        var report = new AspectRunReport(
            Verdicts: [new AspectVerdict("requirement-fit", AspectStatus.Pass, "ok", "body", null)],
            Overall: AspectStatus.Pass,
            ConcernTagIds: [],
            FollowUpSummary: "");

        var run = ReviewDecisionOrchestrator.BuildWikiLearningsRun(
            report, Task(), statusSummary: "", diffSummary: "\n\n  3 files changed, 12 insertions(+)\nmore detail\n");

        Assert.Equal("accept", run.Verdict);
        Assert.Equal("3 files changed, 12 insertions(+)", run.ChangedSummary);
    }

    [Fact]
    public void Build_DistillsStatusNotes_DroppingHeadingsAndComments_AndCaps()
    {
        var report = new AspectRunReport(
            Verdicts: [new AspectVerdict("code-quality", AspectStatus.Pass, "ok", "body", null)],
            Overall: AspectStatus.Pass,
            ConcernTagIds: [],
            FollowUpSummary: "");

        var status = "# Heading\n<!-- regenerated -->\n---\nFirst real line.\nSecond line.\nThird line.\nFourth line should be dropped.";
        var run = ReviewDecisionOrchestrator.BuildWikiLearningsRun(report, Task(), status, "");

        Assert.Equal("First real line. Second line. Third line.", run.AgentNotes);
    }

    private static TaskInfo Task() => new()
    {
        Id = "task-1",
        Key = "ASS-1694",
        Title = "Wiki-Post-Processing-Step",
        ProjectName = "agent-taskboard",
        FolderPath = "unused",
        Agent = "claude",
        CliType = "claude",
        TaskType = TaskTypes.Feature,
        Tags = ["pipeline", "wiki"],
    };

    private static TaskCommitInfo Commit(string sha, string message) => new()
    {
        Sha = sha,
        ShortSha = sha.Length >= 7 ? sha[..7] : sha,
        Message = message,
    };
}
