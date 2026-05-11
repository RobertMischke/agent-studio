using OrchestratorApi.Services.Markdown;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the canonical YAML frontmatter helper contract: leading
/// <c>---</c> block becomes a flat dictionary; folded scalars work;
/// missing block produces an Ok=false result with the markdown passed
/// through as Body; quote stripping is consistent.
/// </summary>
public class FrontmatterParserTests
{
    [Fact]
    public void Parse_ExtractsFlatFields()
    {
        const string md = """
            ---
            kind: security-review
            severity: High
            title: SQL injection risk
            ---

            # Body starts here
            Some text.
            """;
        var r = FrontmatterParser.Parse(md);
        Assert.True(r.Ok);
        Assert.Null(r.Error);
        Assert.Equal("security-review", r.Fields["kind"]);
        Assert.Equal("High", r.Fields["severity"]);
        Assert.Equal("SQL injection risk", r.Fields["title"]);
        Assert.Contains("# Body starts here", r.Body);
    }

    [Fact]
    public void Parse_HandlesFoldedScalar()
    {
        const string md = """
            ---
            id: report
            description: >
              First line of the folded scalar.
              Second line.
            ---

            body
            """;
        var r = FrontmatterParser.Parse(md);
        Assert.True(r.Ok);
        Assert.Contains("First line", r.Fields["description"]);
        Assert.Contains("Second line", r.Fields["description"]);
    }

    [Fact]
    public void Parse_StripsQuotes()
    {
        const string md = """
            ---
            quoted: "hello: world"
            single: 'apostrophes work too'
            ---

            body
            """;
        var r = FrontmatterParser.Parse(md);
        Assert.True(r.Ok);
        Assert.Equal("hello: world", r.Fields["quoted"]);
        Assert.Equal("apostrophes work too", r.Fields["single"]);
    }

    [Fact]
    public void Parse_NoFrontmatter_ReturnsBodyVerbatim()
    {
        const string md = "# Just markdown\n\nNo frontmatter.";
        var r = FrontmatterParser.Parse(md);
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
        Assert.Equal(md, r.Body);
        Assert.Empty(r.Fields);
    }

    [Fact]
    public void Parse_EmptyFrontmatter_ReportsError()
    {
        const string md = "---\n\n---\n\nbody";
        var r = FrontmatterParser.Parse(md);
        Assert.False(r.Ok);
        Assert.Contains("recognised keys", r.Error);
    }

    [Fact]
    public void Parse_EmptyInput_Safe()
    {
        var r = FrontmatterParser.Parse("");
        Assert.False(r.Ok);
        Assert.Empty(r.Fields);
    }
}
