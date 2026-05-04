using OrchestratorApi.Models;
using OrchestratorApi.Services.Supervisor;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the parsing rules used by <see cref="ProjectObservationService"/>.
/// The supervisor's hard health checks and soft reasoner both consume these
/// outputs each tick, so the rules are load-bearing for any per-project
/// observation: which lines count as orchestrator decisions, which are agent
/// samples, and which contribute to error counts.
/// </summary>
public class ObservationParsingTests
{
    private static List<CliOutputLine> Lines(params (string stream, string text)[] entries)
    {
        var ts = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        return entries.Select((e, i) => new CliOutputLine
        {
            Timestamp = ts.AddSeconds(i),
            Stream = e.stream,
            Text = e.text,
        }).ToList();
    }

    [Fact]
    public void ExtractRecentDecisions_FindsOnlyOrchestratorLines_AndPreservesOrder()
    {
        var lines = Lines(
            ("stdout", "Reading file foo.cs"),
            ("orchestrator", "[reissue] Re-issued follow-up after fast no-op."),
            ("stdout", "Done reading"),
            ("orchestrator", "[heuristic] Verdict reached without sentinel."));

        var decisions = ObservationParsing.ExtractRecentDecisions(lines);

        Assert.Equal(2, decisions.Count);
        Assert.Equal("reissue", decisions[0].Kind);
        Assert.Equal("Re-issued follow-up after fast no-op.", decisions[0].Summary);
        Assert.Equal("heuristic", decisions[1].Kind);
        Assert.True(decisions[0].At < decisions[1].At);
    }

    [Fact]
    public void ExtractRecentDecisions_RespectsMax()
    {
        var entries = new List<(string, string)>();
        for (int i = 0; i < 30; i++)
            entries.Add(("orchestrator", $"[reissue] msg {i}"));
        var lines = Lines(entries.ToArray());

        var decisions = ObservationParsing.ExtractRecentDecisions(lines, max: 5);

        Assert.Equal(5, decisions.Count);
        Assert.Equal("msg 25", decisions[0].Summary);
        Assert.Equal("msg 29", decisions[4].Summary);
    }

    [Fact]
    public void ExtractRecentDecisions_HandlesUntaggedText()
    {
        var lines = Lines(("orchestrator", "no tag here"));
        var decisions = ObservationParsing.ExtractRecentDecisions(lines);
        Assert.Single(decisions);
        Assert.Equal("decision", decisions[0].Kind);
        Assert.Equal("no tag here", decisions[0].Summary);
    }

    [Fact]
    public void ExtractRecentAgentSamples_SkipsOrchestratorAndEmptyLines()
    {
        var lines = Lines(
            ("stdout", "Reading file foo.cs"),
            ("orchestrator", "[reissue] not a sample"),
            ("stdout", ""),
            ("stdout", "Writing file bar.cs"));

        var samples = ObservationParsing.ExtractRecentAgentSamples(lines);

        Assert.Equal(2, samples.Count);
        Assert.Equal("Reading file foo.cs", samples[0]);
        Assert.Equal("Writing file bar.cs", samples[1]);
    }

    [Fact]
    public void ExtractRecentAgentSamples_RespectsMax_AndKeepsLatest()
    {
        var entries = Enumerable.Range(0, 50).Select(i => ("stdout", $"line {i}")).ToArray();
        var lines = Lines(entries);
        var samples = ObservationParsing.ExtractRecentAgentSamples(lines, max: 10);
        Assert.Equal(10, samples.Count);
        Assert.Equal("line 40", samples[0]);
        Assert.Equal("line 49", samples[9]);
    }

    [Fact]
    public void CountErrors_ScansWithinWindow_AndCategorisesSource()
    {
        var lines = Lines(
            ("stdout", "all good"),
            ("stderr", "TypeError: undefined is not a function"),
            ("orchestrator", "[heuristic] failure verdict"),
            ("stdout", "task failed: ran out of context"));
        var now = lines[^1].Timestamp.AddSeconds(1);
        var counts = ObservationParsing.CountErrors(lines, now, TimeSpan.FromMinutes(5));

        Assert.Equal(2, counts.CliErrorsLastHour);
        Assert.Equal(1, counts.OrchestratorErrorsLastHour);
        Assert.Equal(1, counts.RunFailuresLastHour);
    }

    [Fact]
    public void CountErrors_IgnoresLinesOutsideWindow()
    {
        var lines = Lines(
            ("stderr", "old error"),
            ("stdout", "still ok"));
        // Move the cutoff forward so the first line is outside the window.
        var now = lines[^1].Timestamp.AddMinutes(10);
        var counts = ObservationParsing.CountErrors(lines, now, TimeSpan.FromMinutes(2));

        Assert.Equal(0, counts.CliErrorsLastHour);
        Assert.Equal(0, counts.OrchestratorErrorsLastHour);
        Assert.Equal(0, counts.RunFailuresLastHour);
    }

    [Fact]
    public void LatestTimestamp_PicksTheNewestEntry()
    {
        var lines = Lines(("stdout", "a"), ("stdout", "b"), ("stdout", "c"));
        var ts = ObservationParsing.LatestTimestamp(lines);
        Assert.Equal(lines[^1].Timestamp, ts);
    }

    [Fact]
    public void LatestTimestamp_OnEmpty_ReturnsNull()
    {
        Assert.Null(ObservationParsing.LatestTimestamp(Array.Empty<CliOutputLine>()));
    }
}
