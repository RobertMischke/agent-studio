using OrchestratorApi.Services;
using Xunit;

namespace OrchestratorApi.Tests;

public class CliOutputLogParserTests
{
    [Fact]
    public void ParseLines_RehydratesPersistedCliOutput()
    {
        var lines = new[]
        {
            "[12:18:54.201] [stdout] * Read prompt.md",
            "[12:18:56.826] [stderr] Build failed"
        };

        var parsed = CliOutputLogParser.ParseLines(lines, new DateTime(2026, 4, 26, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, parsed.Count);
        Assert.Equal("stdout", parsed[0].Stream);
        Assert.Equal("* Read prompt.md", parsed[0].Text);
        Assert.Equal(new DateTime(2026, 4, 26, 12, 18, 54, 201, DateTimeKind.Utc), parsed[0].Timestamp);
        Assert.Equal("stderr", parsed[1].Stream);
        Assert.Equal("Build failed", parsed[1].Text);
    }

    [Fact]
    public void ParseLines_KeepsUnknownRowsVisible()
    {
        var parsed = CliOutputLogParser.ParseLines(
            ["raw row without persisted metadata"],
            new DateTime(2026, 4, 26, 0, 0, 0, DateTimeKind.Utc));

        Assert.Single(parsed);
        Assert.Equal("stdout", parsed[0].Stream);
        Assert.Equal("raw row without persisted metadata", parsed[0].Text);
    }
}
