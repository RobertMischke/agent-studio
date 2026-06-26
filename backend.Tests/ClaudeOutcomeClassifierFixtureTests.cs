using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Drives real-shaped <c>claude</c> run transcripts (stored as NDJSON fixtures
/// under <c>Fixtures/cli/claude/outcome-classifier/</c>) through
/// <see cref="AgentOutcomeAnalyzer.Analyze"/> and locks the verdict.
///
/// <para>
/// This is the integration-of-fixtures gap behind the broken-commit-pipeline
/// incident (2026-06-08): the classifier matrix had hand-written unit coverage
/// but no coverage that fed a genuine claude transcript end to end. The headline
/// regression is <c>done-substantial-no-sentinel</c> / <c>substantial-neutral-no-verdict</c>:
/// a claude run that did the work but never emitted a parseable <c>[[TASK_DONE]]</c>
/// must classify as <see cref="AgentOutcomeKind.Done"/> (reviewable), never
/// <see cref="AgentOutcomeKind.Unknown"/> with <see cref="RunIssueKind.OrchestratorInconclusive"/>,
/// which is the loop that left work uncommitted in the worktree.
/// </para>
/// </summary>
public class ClaudeOutcomeClassifierFixtureTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "cli", "claude", "outcome-classifier");

    private static List<CliOutputLine> LoadFixture(string fileName)
    {
        var path = Path.Combine(FixtureDir, fileName);
        Assert.True(File.Exists(path), $"Missing claude outcome fixture: {path}");

        var ts = new DateTime(2026, 6, 8, 10, 0, 0, DateTimeKind.Utc);
        var lines = new List<CliOutputLine>();
        var i = 0;
        foreach (var raw in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var stream = root.TryGetProperty("stream", out var s) ? s.GetString() ?? "stdout" : "stdout";
            var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            lines.Add(new CliOutputLine
            {
                Timestamp = ts.AddSeconds(i++),
                Stream = stream,
                Text = text
            });
        }
        return lines;
    }

    [Theory]
    [InlineData("done-with-sentinel.ndjson", "completed", 95.0, AgentOutcomeKind.Done, true, RunIssueKind.None)]
    [InlineData("done-substantial-no-sentinel.ndjson", "completed", 140.0, AgentOutcomeKind.Done, false, RunIssueKind.MissingTerminalSentinel)]
    [InlineData("substantial-neutral-no-verdict.ndjson", "completed", 130.0, AgentOutcomeKind.Done, false, RunIssueKind.MissingTerminalSentinel)]
    [InlineData("blocked-with-sentinel.ndjson", "completed", 60.0, AgentOutcomeKind.Blocked, true, RunIssueKind.None)]
    [InlineData("needs-input-with-sentinel.ndjson", "completed", 30.0, AgentOutcomeKind.NeedsInput, true, RunIssueKind.None)]
    [InlineData("noop-with-sentinel.ndjson", "completed", 20.0, AgentOutcomeKind.NoOp, true, RunIssueKind.None)]
    [InlineData("empty-fast-exit-quota.ndjson", "completed", 1.5, AgentOutcomeKind.Unknown, false, RunIssueKind.EmptyFastExit)]
    [InlineData("context-overflow-failed.ndjson", "failed", 6.0, AgentOutcomeKind.Unknown, false, RunIssueKind.ContextOverflow)]
    public void ClaudeTranscriptFixture_ClassifiesToExpectedVerdict(
        string fixture,
        string status,
        double durationSeconds,
        AgentOutcomeKind expectedKind,
        bool expectedMatchedSentinel,
        RunIssueKind expectedIssue)
    {
        var lines = LoadFixture(fixture);

        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status, durationSeconds);

        Assert.Equal(expectedKind, outcome.Kind);
        Assert.Equal(expectedMatchedSentinel, outcome.MatchedSentinel);
        Assert.Equal(expectedIssue, outcome.IssueKind);
    }

    [Fact]
    public void ClaudeDoneSubstantialWithoutSentinel_IsNeverInconclusive()
    {
        // The exact 2026-06-08 broken-commit-pipeline shape: a substantial
        // claude completion reply with no parseable sentinel. It must flow to
        // review as Done, not spin an inconclusive reissue loop that
        // strands the work uncommitted.
        foreach (var fixture in new[] { "done-substantial-no-sentinel.ndjson", "substantial-neutral-no-verdict.ndjson" })
        {
            var outcome = AgentOutcomeAnalyzer.Analyze(LoadFixture(fixture), "completed", 130.0);
            Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
            Assert.NotEqual(AgentOutcomeKind.Unknown, outcome.Kind);
            Assert.NotEqual(RunIssueKind.OrchestratorInconclusive, outcome.IssueKind);
            Assert.NotEqual(RunIssueKind.InfraCrash, outcome.IssueKind);
        }
    }

    [Fact]
    public void EveryFixtureInFolder_IsCoveredByTheMatrix()
    {
        // Guard against a fixture being dropped in the folder without a matching
        // assertion: the README contract is "one file = one locked case".
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "done-with-sentinel.ndjson",
            "done-substantial-no-sentinel.ndjson",
            "substantial-neutral-no-verdict.ndjson",
            "blocked-with-sentinel.ndjson",
            "needs-input-with-sentinel.ndjson",
            "noop-with-sentinel.ndjson",
            "empty-fast-exit-quota.ndjson",
            "context-overflow-failed.ndjson",
        };

        var onDisk = Directory.EnumerateFiles(FixtureDir, "*.ndjson")
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .ToList();

        Assert.NotEmpty(onDisk);
        foreach (var file in onDisk)
            Assert.Contains(file, covered);
    }
}
