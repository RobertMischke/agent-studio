using System.Text;
using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class RetentionExcerptWriterTests
{
    [Fact]
    public void Cli_excerpt_keeps_head_tail_error_signals_counters_and_commands()
    {
        var lines = Enumerable.Range(1, 800).Select(index => $"line {index}").ToArray();
        lines[300] = "2026-09-06T10:00:00 ERROR command failed with exit code 2";
        lines[301] = "$ dotnet test";
        lines[302] = "tool_call tokens=123 commit completed duration=12s";
        var result = RetentionExcerptWriter.Write("logs/cli-output.log", Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        Assert.Contains("line 1", result);
        Assert.Contains("line 800", result);
        Assert.Contains("line 301", result);
        Assert.Contains("## Error windows", result);
        Assert.Contains("- Tool calls: 1", result);
        Assert.Contains("$ dotnet test", result);
    }

    [Fact]
    public void Review_excerpt_keeps_only_verdict_and_findings()
    {
        var result = RetentionExcerptWriter.Write("review/review-stdout.log",
            Encoding.UTF8.GetBytes("noise\nVerdict: reissue\nFinding: missing test\nmore noise"));
        Assert.Contains("Verdict: reissue", result);
        Assert.Contains("Finding: missing test", result);
        Assert.DoesNotContain("more noise", result);
    }

    [Theory]
    [InlineData("results/report.md", "full report", true)]
    [InlineData("results/trace.zip", "binary", false)]
    [InlineData("results/screenshot.png", "pixels", false)]
    public void Result_excerpt_keeps_reports_and_lists_binary_entries(string path, string text, bool full)
    {
        var result = RetentionExcerptWriter.Write(path, Encoding.UTF8.GetBytes(text));
        Assert.Contains("## Inventory", result);
        Assert.Equal(full, result.Contains(text, StringComparison.Ordinal));
    }
}
