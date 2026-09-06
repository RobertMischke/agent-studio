using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class RetentionExcerptWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retention-excerpt-" + Guid.NewGuid().ToString("N"));

    public RetentionExcerptWriterTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task CliExcerptKeepsHeadTailErrorsCommandsAndCounters()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        var lines = Enumerable.Range(1, 800).Select(index => $"line {index}").ToArray();
        lines[5] = "2026-09-01T10:00:00Z duration 42s";
        lines[250] = "$ dotnet test";
        lines[300] = "tool call started";
        lines[350] = "FAILED with exception and exit code 2";
        lines[400] = "git commit abcdef1";
        lines[450] = "123 tokens";
        await File.WriteAllLinesAsync(Path.Combine(_root, "logs", "cli-output.log"), lines);

        var excerpt = Assert.Single(await new RetentionExcerptWriter().CreateAsync(_root, ["logs/cli-output.log"]));

        Assert.Contains("## Timestamps and duration", excerpt.Markdown);
        Assert.Contains("## Commands", excerpt.Markdown);
        Assert.Contains("$ dotnet test", excerpt.Markdown);
        Assert.Contains("FAILED with exception", excerpt.Markdown);
        Assert.Contains("- Tool calls: 1", excerpt.Markdown);
        Assert.Contains("- Token lines: 1", excerpt.Markdown);
        Assert.Contains("line 1", excerpt.Markdown);
        Assert.Contains("line 800", excerpt.Markdown);
    }

    [Fact]
    public async Task ReviewExcerptKeepsVerdictAndFindings()
    {
        await File.WriteAllLinesAsync(Path.Combine(_root, "review-stdout.log"),
            ["noise", "Verdict: warn", "Finding: unsafe path", "more noise"]);
        var excerpt = Assert.Single(await new RetentionExcerptWriter().CreateAsync(_root, ["review-stdout.log"]));
        Assert.Contains("Verdict: warn", excerpt.Markdown);
        Assert.Contains("Finding: unsafe path", excerpt.Markdown);
        Assert.DoesNotContain("more noise", excerpt.Markdown);
    }

    [Fact]
    public async Task ResultsExcerptInventoriesBinaryAndKeepsMarkdownInFull()
    {
        Directory.CreateDirectory(Path.Combine(_root, "results"));
        await File.WriteAllTextAsync(Path.Combine(_root, "results", "report.md"), "# Complete report\nFinding");
        await File.WriteAllBytesAsync(Path.Combine(_root, "results", "trace.zip"), [1, 2, 3]);
        var excerpt = Assert.Single(await new RetentionExcerptWriter().CreateAsync(
            _root, ["results/report.md", "results/trace.zip"]));
        Assert.Contains("results/trace.zip", excerpt.Markdown);
        Assert.Contains("# Complete report", excerpt.Markdown);
    }
}
