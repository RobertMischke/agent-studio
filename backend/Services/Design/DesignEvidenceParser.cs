using OrchestratorApi.Services.Security;

namespace OrchestratorApi.Services.Design;

/// <summary>
/// Parses the structured block in a design-evidence Markdown file (slice 6
/// of the quality-system mockup, docs/mockups/quality-system/taxonomy.md
/// "Storage Shape"). Reuses the generic Markdown frontmatter / fenced JSON
/// reader from <see cref="SecurityReviewParser"/>; this wrapper exists so
/// design-evidence callers and unit tests have a domain-named entry point
/// that returns design-specific records.
///
/// Graceful degradation matches the "Report Contracts" rule from the
/// mockup README: when neither frontmatter nor JSON sidecar is present the
/// result has <c>ParseOk = false</c> and the UI renders the raw Markdown
/// with the "unstructured report" warning.
/// </summary>
public static class DesignEvidenceParser
{
    public static DesignEvidenceParseResult Parse(string? markdown)
    {
        var inner = SecurityReviewParser.Parse(markdown);
        return new DesignEvidenceParseResult(
            ParseOk: inner.ParseOk,
            ParseError: inner.ParseError,
            Fields: inner.Fields);
    }

    public static string? GetString(IReadOnlyDictionary<string, object?> fields, string key)
        => SecurityReviewParser.GetString(fields, key);

    public static int? GetInt(IReadOnlyDictionary<string, object?> fields, string key)
        => SecurityReviewParser.GetInt(fields, key);

    /// <summary>
    /// Normalises a kind token from the reference frontmatter. Unknown
    /// values fall through unchanged so producers can extend the taxonomy
    /// without the panel hiding their entry.
    /// </summary>
    public static string NormaliseKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "external";
        var k = raw.Trim().ToLowerInvariant();
        return k switch
        {
            "accepted" or "accept" or "approved" => "accepted",
            "rejected" or "declined" or "reject" => "rejected",
            "external" or "inspiration" or "reference" => "external",
            "brief" or "markdown-brief" => "brief",
            _ => k,
        };
    }
}
