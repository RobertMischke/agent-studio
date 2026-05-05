using OrchestratorApi.Services.Protocol;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Tolerance contract for the protocol-header parser. The parser is the
/// single point that turns a free-form <c>status.md</c> document into the
/// structured payload that drives the in-app header card. Three rules are
/// load-bearing:
/// <list type="bullet">
///   <item>Missing or malformed structured blocks never fail rendering;
///   the lead-paragraph fallback is always populated.</item>
///   <item>Both surface forms (HTML comment, fenced JSON block) round-trip
///   so prompt authors can pick whichever fits the document better.</item>
///   <item>Optional fields default to safe values when absent.</item>
/// </list>
/// </summary>
public class ProtocolHeaderParserTests
{
    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmptyResult()
    {
        var result = ProtocolHeaderParser.Parse(null);
        Assert.Null(result.Header);
        Assert.Equal(string.Empty, result.LeadParagraph);

        result = ProtocolHeaderParser.Parse("   ");
        Assert.Null(result.Header);
        Assert.Equal(string.Empty, result.LeadParagraph);
    }

    [Fact]
    public void Parse_HtmlCommentHeader_ProducesStructuredPayload()
    {
        var md = """
        Implementation in progress, schema and parser landed.

        <!-- header-json: { "phase": "implementing", "summary": "Schema and parser landed; endpoint wired.", "decisionsOpen": 1 } -->

        ## What Was Done
        - schemas committed
        """;

        var result = ProtocolHeaderParser.Parse(md);
        Assert.NotNull(result.Header);
        Assert.Equal(ProtocolPhase.Implementing, result.Header!.Phase);
        Assert.Equal("Schema and parser landed; endpoint wired.", result.Header.Summary);
        Assert.Equal(1, result.Header.DecisionsOpen);
        Assert.Null(result.ParseWarning);
        Assert.Contains("Implementation in progress", result.LeadParagraph);
    }

    [Fact]
    public void Parse_FencedJsonHeaderBlock_ProducesStructuredPayload()
    {
        var md = """
        Header carried inside a fenced block instead of an HTML comment.

        ```json header
        { "phase": "review", "summary": "Ready for review.", "agent": "claude-code", "runs": 2 }
        ```
        """;

        var result = ProtocolHeaderParser.Parse(md);
        Assert.NotNull(result.Header);
        Assert.Equal(ProtocolPhase.Review, result.Header!.Phase);
        Assert.Equal("claude-code", result.Header.Agent);
        Assert.Equal(2, result.Header.Runs);
    }

    [Fact]
    public void Parse_MissingStructuredBlock_FallsBackToLeadParagraph()
    {
        var md = """
        # Status

        First prose line that the UI can use as a fallback.
        Second prose line is also folded into the lead paragraph.

        ## What Was Done
        - work
        """;

        var result = ProtocolHeaderParser.Parse(md);
        Assert.Null(result.Header);
        Assert.Null(result.ParseWarning);
        Assert.Contains("First prose line", result.LeadParagraph);
        Assert.Contains("Second prose line", result.LeadParagraph);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsWarningAndNoHeader()
    {
        var md = "<!-- header-json: { phase: 'oops' } -->";
        var result = ProtocolHeaderParser.Parse(md);
        Assert.Null(result.Header);
        Assert.NotNull(result.ParseWarning);
    }

    [Fact]
    public void Parse_UnknownPhase_ReturnsWarning()
    {
        var md = "<!-- header-json: { \"phase\": \"deploying\", \"summary\": \"x\" } -->";
        var result = ProtocolHeaderParser.Parse(md);
        Assert.Null(result.Header);
        Assert.Contains("unknown phase", result.ParseWarning);
    }

    [Fact]
    public void Parse_OptionalFieldsMissing_DefaultsApplied()
    {
        var md = "<!-- header-json: { \"phase\": \"analysis\", \"summary\": \"only required fields\" } -->";
        var result = ProtocolHeaderParser.Parse(md);
        Assert.NotNull(result.Header);
        Assert.Equal(0, result.Header!.DecisionsOpen);
        Assert.Null(result.Header.NextAction);
        Assert.Null(result.Header.CorrelationId);
        Assert.Equal("1", result.Header.SchemaVersion);
    }

    [Fact]
    public void Parse_SummaryOver240Chars_TruncatesNotFails()
    {
        var longSummary = new string('x', 600);
        var md = $"<!-- header-json: {{ \"phase\": \"plan\", \"summary\": \"{longSummary}\" }} -->";
        var result = ProtocolHeaderParser.Parse(md);
        Assert.NotNull(result.Header);
        Assert.Equal(240, result.Header!.Summary.Length);
    }

    [Fact]
    public void Parse_LeadParagraphSkipsHeadingsAndFencesAndComments()
    {
        var md = """
        # Status

        <!-- some-other-comment -->

        ```text
        not part of the lead
        ```

        Real lead line.
        """;
        var result = ProtocolHeaderParser.Parse(md);
        Assert.Equal("Real lead line.", result.LeadParagraph);
    }
}
