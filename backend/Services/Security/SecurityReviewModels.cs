namespace OrchestratorApi.Services.Security;

/// <summary>
/// One row in the security review history. Slice 1 of the quality-system
/// mockup (docs/mockups/quality-system/) defines this shape: a Markdown
/// file with optional structured frontmatter; the app surfaces the parsed
/// fields in cards and the raw file in the history list.
/// </summary>
/// <param name="FileName">Bare file name on disk (e.g. <c>2026-04-12-baseline-pass.md</c>).</param>
/// <param name="RelPath">Path relative to the project's <c>security/</c> folder.</param>
/// <param name="UpdatedAt">Last-write time of the file, UTC.</param>
/// <param name="ReviewDate">
/// Date harvested from the structured frontmatter (or, as a fallback, the
/// leading <c>YYYY-MM-DD</c> in the file name). Null when neither yields one.
/// </param>
/// <param name="Verdict">
/// Free-form verdict string lifted from the structured block (e.g. "ok",
/// "stale", "failing"). Null when not present or when parsing failed.
/// </param>
/// <param name="Severity">Optional severity label (e.g. "info", "warn", "critical").</param>
/// <param name="OpenFindings">Open-finding count, when the structured block reports it.</param>
/// <param name="Severities">
/// Optional split of open-finding counts by severity, lifted from the
/// structured block. Null when not present.
/// </param>
/// <param name="Title">Optional title from the structured block.</param>
/// <param name="Summary">Optional one-line summary from the structured block.</param>
/// <param name="ParseOk">
/// True when the structured block was parsed successfully; false when the
/// file lacked structured fields or they failed to parse. Drives the
/// "unstructured report" warning in the UI per the README's "Report
/// Contracts" section.
/// </param>
/// <param name="ParseError">Diagnostic when <see cref="ParseOk"/> is false.</param>
public sealed record SecurityReviewSummary(
    string FileName,
    string RelPath,
    DateTime UpdatedAt,
    string? ReviewDate,
    string? Verdict,
    string? Severity,
    int? OpenFindings,
    IReadOnlyDictionary<string, int>? Severities,
    string? Title,
    string? Summary,
    bool ParseOk,
    string? ParseError);

/// <summary>List response wrapper. Keeps the JSON additive when more aggregates appear.</summary>
public sealed record SecurityReviewListResponse(
    string ProjectName,
    string ReviewsDir,
    bool Exists,
    IReadOnlyList<SecurityReviewSummary> Reviews);

/// <summary>
/// Parsed baseline state for the project's security panel. The baseline is
/// stored as <c>security/baseline.md</c> with a small structured block
/// (severity thresholds, last-verified date, link to the active review
/// definition record). When the file is missing we still return a record so
/// the UI can render the empty state without a 404.
/// </summary>
public sealed record SecurityBaselineResponse(
    string ProjectName,
    string FilePath,
    bool Exists,
    string? Status,
    string? LastVerified,
    string? DefinitionRef,
    IReadOnlyDictionary<string, string>? SeverityThresholds,
    string? Summary,
    bool ParseOk,
    string? ParseError,
    string? Markdown);

/// <summary>Result of <see cref="SecurityReviewParser"/> on a single Markdown body.</summary>
public sealed record SecurityReviewParseResult(
    bool ParseOk,
    string? ParseError,
    IReadOnlyDictionary<string, object?> Fields);
