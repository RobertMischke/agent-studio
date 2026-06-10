
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the contract of <see cref="DesignEvidenceParser"/>: design
/// references and council notes use the same Markdown-plus-frontmatter
/// shape as security reviews; this test fixture confirms the wrapper
/// returns design-named records and that <see cref="DesignEvidenceParser.NormaliseKind"/>
/// keeps the four card-kinds stable. Slice 6 of the quality-system mockup
/// (docs/mockups/quality-system/) requires the parseOk = false path so the
/// UI can fall back to "unstructured report" + raw Markdown.
/// </summary>
public class DesignEvidenceParserTests
{
    [Fact]
    public void ParsesReferenceFrontmatter()
    {
        var md =
            "---\n" +
            "kind: accepted\n" +
            "title: Project shell rail\n" +
            "summary: Final layout that landed in slice 2.\n" +
            "screenshot: project-shell-rail.png\n" +
            "---\n\n" +
            "## Notes\n\nLocked direction.\n";

        var result = DesignEvidenceParser.Parse(md);

        Assert.True(result.ParseOk);
        Assert.Null(result.ParseError);
        Assert.Equal("accepted", DesignEvidenceParser.GetString(result.Fields, "kind"));
        Assert.Equal("Project shell rail", DesignEvidenceParser.GetString(result.Fields, "title"));
        Assert.Equal("project-shell-rail.png", DesignEvidenceParser.GetString(result.Fields, "screenshot"));
    }

    [Fact]
    public void ParsesCouncilNoteFrontmatter()
    {
        var md =
            "---\n" +
            "date: 2026-04-12\n" +
            "category: a11y\n" +
            "title: Accessibility\n" +
            "summary: Heatmap cells need keyboard drill-down.\n" +
            "---\n\n" +
            "## Body\n\nNote details.\n";

        var result = DesignEvidenceParser.Parse(md);

        Assert.True(result.ParseOk);
        Assert.Equal("2026-04-12", DesignEvidenceParser.GetString(result.Fields, "date"));
        Assert.Equal("a11y", DesignEvidenceParser.GetString(result.Fields, "category"));
        Assert.Equal("Accessibility", DesignEvidenceParser.GetString(result.Fields, "title"));
    }

    [Fact]
    public void ReturnsParseOkFalseForUnstructuredMarkdown()
    {
        var md = "# Free-form note\n\nThe agent forgot to emit frontmatter. The panel must fall back to raw Markdown.\n";

        var result = DesignEvidenceParser.Parse(md);

        Assert.False(result.ParseOk);
        Assert.NotNull(result.ParseError);
    }

    [Fact]
    public void PrefersFencedJsonSidecarOverFrontmatter()
    {
        var md =
            "---\n" +
            "kind: rejected\n" +
            "title: Old draft\n" +
            "---\n\n" +
            "## Body\n\nDetails.\n\n" +
            "```json\n" +
            "{ \"kind\": \"accepted\", \"title\": \"Final draft\" }\n" +
            "```\n";

        var result = DesignEvidenceParser.Parse(md);

        Assert.True(result.ParseOk);
        // JSON sidecar wins over frontmatter.
        Assert.Equal("accepted", DesignEvidenceParser.GetString(result.Fields, "kind"));
        Assert.Equal("Final draft", DesignEvidenceParser.GetString(result.Fields, "title"));
    }

    [Theory]
    [InlineData("accepted", "accepted")]
    [InlineData("Accepted", "accepted")]
    [InlineData("approved", "accepted")]
    [InlineData("rejected", "rejected")]
    [InlineData("declined", "rejected")]
    [InlineData("external", "external")]
    [InlineData("inspiration", "external")]
    [InlineData("brief", "brief")]
    [InlineData("markdown-brief", "brief")]
    [InlineData(null, "external")]
    [InlineData("", "external")]
    public void NormaliseKindMapsKnownAliases(string? raw, string expected)
    {
        Assert.Equal(expected, DesignEvidenceParser.NormaliseKind(raw));
    }

    [Fact]
    public void NormaliseKindKeepsUnknownValuesIntact()
    {
        // Producer extension: an unknown token survives so the panel can
        // surface it in a future card without the parser hiding the row.
        Assert.Equal("future-kind", DesignEvidenceParser.NormaliseKind("future-kind"));
    }

    [Fact]
    public void StampAcceptedAtCreatesFrontmatterWhenAbsent()
    {
        var original = "# Council note\n\nBody only.\n";
        var stamped = DesignEvidenceService.StampAcceptedAt(original, "2026-05-08T12:00:00Z");

        Assert.StartsWith("---\nacceptedAt: 2026-05-08T12:00:00Z\n---", stamped);
        Assert.Contains("Body only.", stamped);
    }

    [Fact]
    public void StampAcceptedAtInsertsIntoExistingFrontmatter()
    {
        var original =
            "---\n" +
            "category: workflow\n" +
            "title: Product\n" +
            "---\n\n" +
            "Body.\n";
        var stamped = DesignEvidenceService.StampAcceptedAt(original, "2026-05-08T12:00:00Z");

        Assert.Contains("acceptedAt: 2026-05-08T12:00:00Z", stamped);
        Assert.Contains("category: workflow", stamped);
        Assert.Contains("Body.", stamped);
    }

    [Fact]
    public void StampAcceptedAtIsIdempotent()
    {
        var original =
            "---\n" +
            "category: workflow\n" +
            "acceptedAt: 2026-04-01T00:00:00Z\n" +
            "---\n\nBody.\n";
        var stamped = DesignEvidenceService.StampAcceptedAt(original, "2026-05-08T12:00:00Z");

        Assert.Contains("acceptedAt: 2026-05-08T12:00:00Z", stamped);
        Assert.DoesNotContain("acceptedAt: 2026-04-01", stamped);
    }
}
