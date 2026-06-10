using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// ASS-734 traceability half: every steering step the orchestrator drives must
/// be reconstructable - the exact prompt the agent received plus the context it
/// was given (resume vs fresh, prior attempt count, reason, prior commits).
/// These lock the rendered steering-history surface that
/// <see cref="ReviewDecisionOrchestrator"/> persists per step under
/// <c>orchestrator-follow-up-history/</c> (never overwritten).
/// </summary>
public class OrchestratorSteeringTraceabilityTests
{
    [Fact]
    public void RenderSteeringHistory_IncludesContextAndVerbatimPrompt()
    {
        var context = new ReviewDecisionOrchestrator.SteeringContext(
            Cause: "no-completion-signal",
            Verdict: "reissue",
            PriorReissues: 1,
            Reason: "run finished without a terminal sentinel",
            ResumeSessionId: "sess-123",
            PriorCommits: new[] { "a1b2c3 feat: lane move" });

        var rendered = ReviewDecisionOrchestrator.RenderSteeringHistory(
            context, "STEER THE DIFF...\n\nDo only the open work.");

        Assert.Contains("## Context", rendered);
        Assert.Contains("cause: no-completion-signal", rendered);
        Assert.Contains("verdict: reissue", rendered);
        Assert.Contains("priorReissues: 1", rendered);
        Assert.Contains("mode: resume", rendered);
        Assert.Contains("resumeSessionId: sess-123", rendered);
        Assert.Contains("reason: run finished without a terminal sentinel", rendered);
        Assert.Contains("a1b2c3 feat: lane move", rendered);
        // The verbatim prompt is preserved so the operator sees exactly what the
        // agent was told.
        Assert.Contains("## Steering prompt (verbatim)", rendered);
        Assert.Contains("Do only the open work.", rendered);
    }

    [Fact]
    public void RenderSteeringHistory_MarksFreshRun_WhenNoResumeSession()
    {
        var context = new ReviewDecisionOrchestrator.SteeringContext(
            Cause: "noop-recovery",
            Verdict: "reissue",
            PriorReissues: 0);

        var rendered = ReviewDecisionOrchestrator.RenderSteeringHistory(context, "prompt body");

        Assert.Contains("mode: fresh-run", rendered);
        Assert.DoesNotContain("resumeSessionId", rendered);
        Assert.Contains("prompt body", rendered);
    }

    /// <summary>
    /// Audit persistence: a steering step writes the canonical
    /// <c>orchestrator-follow-up.md</c> (read by the next pickup) AND a versioned
    /// copy under <c>orchestrator-follow-up-history/</c> carrying the verbatim
    /// prompt + context header. This is the on-disk half of the traceability fix
    /// (ASS-734): the canonical file alone was clobbered every reissue.
    /// </summary>
    [Fact]
    public async Task WriteFollowUpFiles_PersistsCanonicalAndVersionedHistoryCopy()
    {
        var folder = NewTempFolder();
        try
        {
            var context = new ReviewDecisionOrchestrator.SteeringContext(
                Cause: "no-completion-signal",
                Verdict: "reissue",
                PriorReissues: 2,
                Reason: "no terminal sentinel",
                ResumeSessionId: "sess-xyz");

            var historyPath = await ReviewDecisionOrchestrator.WriteFollowUpFilesAsync(
                folder, "STEER THE DIFF...\n\nclose out the open items.", context,
                jobId: "fix-thing", logger: NullLogger.Instance, ct: default);

            // Canonical file the pickup pre-check reads.
            var canonicalPath = Path.Combine(folder, "orchestrator-follow-up.md");
            Assert.True(File.Exists(canonicalPath));
            Assert.Contains("close out the open items.", await File.ReadAllTextAsync(canonicalPath));

            // Versioned copy lives under the history dir and carries prompt + context.
            Assert.NotNull(historyPath);
            Assert.StartsWith(
                Path.Combine(folder, "orchestrator-follow-up-history"),
                historyPath!,
                StringComparison.OrdinalIgnoreCase);
            var historyText = await File.ReadAllTextAsync(historyPath!);
            Assert.Contains("cause: no-completion-signal", historyText);
            Assert.Contains("mode: resume", historyText);
            Assert.Contains("close out the open items.", historyText);
        }
        finally
        {
            Cleanup(folder);
        }
    }

    /// <summary>
    /// Append-only: a second steering step must add a NEW history file rather
    /// than overwrite the first, so the full steering trail survives across
    /// reissues. Two back-to-back writes (worst case: same-millisecond stamp)
    /// must yield two distinct, both-present history files.
    /// </summary>
    [Fact]
    public async Task WriteFollowUpFiles_IsAppendOnly_AcrossConsecutiveSteps()
    {
        var folder = NewTempFolder();
        try
        {
            var first = await ReviewDecisionOrchestrator.WriteFollowUpFilesAsync(
                folder, "first steer", context: null,
                jobId: "fix-thing", logger: NullLogger.Instance, ct: default);
            var second = await ReviewDecisionOrchestrator.WriteFollowUpFilesAsync(
                folder, "second steer", context: null,
                jobId: "fix-thing", logger: NullLogger.Instance, ct: default);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first, second);

            var historyDir = Path.Combine(folder, "orchestrator-follow-up-history");
            var files = Directory.GetFiles(historyDir, "*.md");
            Assert.Equal(2, files.Length);
            Assert.Contains("first steer", await File.ReadAllTextAsync(first!));
            Assert.Contains("second steer", await File.ReadAllTextAsync(second!));

            // The canonical file holds only the latest steer (it is meant to be
            // the current instruction); the history retains both.
            var canonical = await File.ReadAllTextAsync(
                Path.Combine(folder, "orchestrator-follow-up.md"));
            Assert.Contains("second steer", canonical);
            Assert.DoesNotContain("first steer", canonical);
        }
        finally
        {
            Cleanup(folder);
        }
    }

    private static string NewTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "atp-steer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Cleanup(string folder)
    {
        try { Directory.Delete(folder, recursive: true); } catch { /* best-effort */ }
    }
}
