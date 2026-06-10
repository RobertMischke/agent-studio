namespace AgentStudio.Design;

/// <summary>
/// Top-row counts + status for the project UX/UI panel (slice 6 of the
/// quality-system mockup, docs/mockups/quality-system/). Values are the
/// inputs to the four metric cards: design status, references count,
/// screenshots count (accepted vs rejected variants), council notes.
/// </summary>
public sealed record DesignOverview(
    string ProjectName,
    string DesignDir,
    bool Exists,
    string Status,
    string? StatusDetail,
    string? LastReviewDate,
    int ReferencesCount,
    int ScreenshotsAcceptedCount,
    int ScreenshotsRejectedCount,
    int ExternalCount,
    int CouncilOpenCount,
    int CouncilAcceptedCount,
    bool BriefExists,
    string? BriefSummary);

/// <summary>One row in the Design References grid.</summary>
/// <param name="FileName">Bare file name on disk (e.g. <c>workbench-shell.md</c>).</param>
/// <param name="RelPath">Path relative to the project's <c>design/</c> folder.</param>
/// <param name="Kind">accepted|rejected|external|brief — drives which card the row docks into.</param>
/// <param name="Title">Optional title from frontmatter.</param>
/// <param name="Summary">One-line summary for the card.</param>
/// <param name="ScreenshotRelPath">
/// Optional path to the sibling image file; when present the UI renders a thumbnail.
/// Path is relative to the project's <c>design/</c> folder so the API stays simple.
/// </param>
/// <param name="UpdatedAt">Last-write time of the Markdown file.</param>
/// <param name="ParseOk">True when frontmatter parsed cleanly.</param>
/// <param name="ParseError">Diagnostic when <see cref="ParseOk"/> is false.</param>
public sealed record DesignReferenceItem(
    string FileName,
    string RelPath,
    string Kind,
    string? Title,
    string? Summary,
    string? ScreenshotRelPath,
    DateTime UpdatedAt,
    bool ParseOk,
    string? ParseError);

/// <summary>List response wrapper.</summary>
public sealed record DesignReferencesResponse(
    string ProjectName,
    string ReferencesDir,
    bool Exists,
    IReadOnlyList<DesignReferenceItem> References);

/// <summary>One council-critique note.</summary>
/// <param name="FileName">Bare file name on disk (e.g. <c>2026-04-12-product-workflow.md</c>).</param>
/// <param name="RelPath">Path relative to the project's <c>design/</c> folder.</param>
/// <param name="Category">workflow|polish|a11y|product|visual|interaction|... — drives the chip color.</param>
/// <param name="Title">Council role or note title (e.g. "Product", "Visual Design").</param>
/// <param name="Summary">Body excerpt for the row.</param>
/// <param name="NoteDate">Date harvested from frontmatter or the leading <c>YYYY-MM-DD</c> in the file name.</param>
/// <param name="AcceptedAt">Set after the user clicks "Accept"; absence means the note is open.</param>
/// <param name="UpdatedAt">Last-write time of the Markdown file.</param>
/// <param name="ParseOk">True when frontmatter parsed cleanly.</param>
/// <param name="ParseError">Diagnostic when <see cref="ParseOk"/> is false.</param>
public sealed record DesignCouncilNote(
    string FileName,
    string RelPath,
    string? Category,
    string? Title,
    string? Summary,
    string? NoteDate,
    string? AcceptedAt,
    DateTime UpdatedAt,
    bool ParseOk,
    string? ParseError);

public sealed record DesignCouncilResponse(
    string ProjectName,
    string CouncilDir,
    bool Exists,
    IReadOnlyList<DesignCouncilNote> Notes);

/// <summary>Result of <see cref="DesignEvidenceParser"/>.Parse on one Markdown body.</summary>
public sealed record DesignEvidenceParseResult(
    bool ParseOk,
    string? ParseError,
    IReadOnlyDictionary<string, object?> Fields);

/// <summary>Action-button response: a queued CLI job id + state.</summary>
public sealed record DesignActionQueueResponse(
    string JobId,
    string State,
    string Title);

/// <summary>Body for the Accept-council-note POST.</summary>
public sealed record AcceptCouncilNoteResponse(
    string FileName,
    string AcceptedAt,
    bool ParseOk);
