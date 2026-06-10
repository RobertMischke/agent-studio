
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the contract of <see cref="SecurityReviewParser"/>: both
/// frontmatter and JSON-fenced shapes resolve to the same field map; the
/// parser surfaces a typed <c>parseOk = false</c> + reason when neither
/// shape is present (the load-bearing path that drives the
/// "unstructured report" fallback in the project Security panel).
/// </summary>
public class SecurityReviewParserTests
{
    [Fact]
    public void ParsesFrontmatter()
    {
        var md = "---\n" +
                 "date: 2026-04-12\n" +
                 "verdict: ok\n" +
                 "severity: info\n" +
                 "openFindings: 2\n" +
                 "title: Quarterly check\n" +
                 "summary: Two low-severity issues in the dependency tree.\n" +
                 "---\n" +
                 "\n" +
                 "## Findings\n\nNothing critical.\n";

        var result = SecurityReviewParser.Parse(md);

        Assert.True(result.ParseOk);
        Assert.Null(result.ParseError);
        Assert.Equal("2026-04-12", SecurityReviewParser.GetString(result.Fields, "date"));
        Assert.Equal("ok", SecurityReviewParser.GetString(result.Fields, "verdict"));
        Assert.Equal("info", SecurityReviewParser.GetString(result.Fields, "severity"));
        Assert.Equal(2, SecurityReviewParser.GetInt(result.Fields, "openFindings"));
        Assert.Equal("Quarterly check", SecurityReviewParser.GetString(result.Fields, "title"));
    }

    [Fact]
    public void ParsesFrontmatterWithIndentedSeveritiesMap()
    {
        var md = "---\n" +
                 "verdict: stale\n" +
                 "openFindings: 7\n" +
                 "severities:\n" +
                 "  critical: 1\n" +
                 "  high: 2\n" +
                 "  medium: 3\n" +
                 "  low: 1\n" +
                 "---\n" +
                 "\nReport body.\n";

        var result = SecurityReviewParser.Parse(md);

        Assert.True(result.ParseOk);
        var sev = SecurityReviewParser.GetIntMap(result.Fields, "severities");
        Assert.NotNull(sev);
        Assert.Equal(1, sev!["critical"]);
        Assert.Equal(2, sev["high"]);
        Assert.Equal(3, sev["medium"]);
        Assert.Equal(1, sev["low"]);
    }

    [Fact]
    public void ParsesJsonFenceFooterAndPrefersItOverFrontmatter()
    {
        var md = "---\n" +
                 "verdict: stale\n" +
                 "openFindings: 99\n" +
                 "---\n\n" +
                 "## Body\n\nDetails here.\n\n" +
                 "```json\n" +
                 "{\n" +
                 "  \"verdict\": \"ok\",\n" +
                 "  \"openFindings\": 0,\n" +
                 "  \"severities\": { \"critical\": 0, \"high\": 0 }\n" +
                 "}\n" +
                 "```\n";

        var result = SecurityReviewParser.Parse(md);

        Assert.True(result.ParseOk);
        // JSON wins when both shapes are present (typed evidence > human-curated frontmatter).
        Assert.Equal("ok", SecurityReviewParser.GetString(result.Fields, "verdict"));
        Assert.Equal(0, SecurityReviewParser.GetInt(result.Fields, "openFindings"));
    }

    [Fact]
    public void EmptyFile_ParseOkFalse()
    {
        var result = SecurityReviewParser.Parse("");
        Assert.False(result.ParseOk);
        Assert.NotNull(result.ParseError);
    }

    [Fact]
    public void NoStructuredBlock_ParseOkFalse()
    {
        var md = "# Just prose\n\nThe agent forgot to emit a structured block.\n";
        var result = SecurityReviewParser.Parse(md);
        Assert.False(result.ParseOk);
        Assert.Contains("no structured", result.ParseError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedJsonFence_ParseOkFalseWithReason()
    {
        var md = "# Body\n\n```json\n{ this isn't json\n```\n";
        var result = SecurityReviewParser.Parse(md);
        Assert.False(result.ParseOk);
        Assert.Contains("JSON", result.ParseError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FrontmatterPresentButEmpty_ParseOkFalse()
    {
        var md = "---\n---\n\nBody.\n";
        var result = SecurityReviewParser.Parse(md);
        Assert.False(result.ParseOk);
        Assert.NotNull(result.ParseError);
    }

    [Fact]
    public void GetStringMap_ReadsBaselineSeverityThresholds()
    {
        var md = "---\n" +
                 "status: ok\n" +
                 "lastVerified: 2026-04-01\n" +
                 "severityThresholds:\n" +
                 "  critical: zero\n" +
                 "  high: review\n" +
                 "definitionRef: docs/quality/audits/SEC-OVERVIEW.md\n" +
                 "---\n";
        var result = SecurityReviewParser.Parse(md);
        Assert.True(result.ParseOk);
        var thresholds = SecurityReviewParser.GetStringMap(result.Fields, "severityThresholds");
        Assert.NotNull(thresholds);
        Assert.Equal("zero", thresholds!["critical"]);
        Assert.Equal("review", thresholds["high"]);
        Assert.Equal("docs/quality/audits/SEC-OVERVIEW.md",
            SecurityReviewParser.GetString(result.Fields, "definitionRef"));
    }

    [Fact]
    public void StripsCommentsAndSurroundingQuotes()
    {
        var md = "---\n" +
                 "title: \"Quarterly check\"  # human comment\n" +
                 "verdict: 'ok'\n" +
                 "openFindings: 4 # known low issues\n" +
                 "---\n";
        var result = SecurityReviewParser.Parse(md);
        Assert.True(result.ParseOk);
        Assert.Equal("Quarterly check", SecurityReviewParser.GetString(result.Fields, "title"));
        Assert.Equal("ok", SecurityReviewParser.GetString(result.Fields, "verdict"));
        Assert.Equal(4, SecurityReviewParser.GetInt(result.Fields, "openFindings"));
    }

    [Fact]
    public void IgnoresHashInsideQuotes()
    {
        // The unquoted-hash detector must not mistake a # inside quotes for
        // a comment. Without this, definition refs that include a fragment
        // (e.g. "docs/...#section") would be silently truncated.
        var md = "---\n" +
                 "definitionRef: \"docs/quality/audits/SEC.md#scoring\"\n" +
                 "---\n";
        var result = SecurityReviewParser.Parse(md);
        Assert.True(result.ParseOk);
        Assert.Equal("docs/quality/audits/SEC.md#scoring",
            SecurityReviewParser.GetString(result.Fields, "definitionRef"));
    }
}
