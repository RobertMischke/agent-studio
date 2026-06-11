using System.Text.Json.Serialization;

namespace AgentStudio.Drift;

/// <summary>
/// In-code projection of <c>docs/schemas/drift-report.schema.json</c> and the
/// prose contract in <c>docs/reports/drift-reports.md</c>. Drift reports are a
/// separate evidence pile from
/// <see cref="AgentStudio.Analysis.AnalysisReport"/>: drift is a
/// project dimension beside Architecture (ROADMAP "Drift Control"), so the
/// store, schema, and surfaces are intentionally distinct. The two contracts
/// share the producer model, the Markdown-plus-JSON convention, and the
/// parse-failure semantics.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftReportTrigger
{
    Manual,
    Scheduled,
    MetaCycle,
    SupportingAgent,
    ExternalMonitor,
}

/// <summary>Producer kind for one drift report. Mirrors the schema's
/// <c>producer.kind</c> enum and the analysis-report producer model.
/// Descriptive only; capability comes from code paths the user controls.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftReportProducerKind
{
    Manual,
    Scheduled,
    MetaCycle,
    SupportingAgent,
    ExternalMonitor,
}

/// <summary>Primary scope kind. Mirrors the schema's <c>scope.kind</c> enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftReportScopeKind
{
    Project,
    Workspace,
    Task,
    Run,
    TimeWindow,
}

/// <summary>
/// Triage band. <c>Unknown</c> is reserved for the evidence-only path where
/// no agent narrative was supplied or overall sourceCoverage was below the
/// reporting threshold (see docs/reports/drift-reports.md, Section 4).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftScoreBand
{
    Healthy,
    Watch,
    Warn,
    Critical,
    Unknown,
}

/// <summary>
/// Severity ladder reused from the analysis-report contract. Critical sits
/// above the supervisor's three-step ladder so architecture and security
/// drift can carry a louder badge.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftSeverity
{
    Info,
    Warn,
    High,
    Critical,
}

/// <summary>
/// Per-finding tracking state. Mirrors the schema's <c>status</c> enum and
/// the design-principles "Drift findings can be new, accepted, ignored,
/// tracked, or resolved" list.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftFindingStatus
{
    New,
    Accepted,
    Ignored,
    Tracked,
    Resolved,
}

/// <summary>
/// Drift dimension vocabulary. Producers must not invent new dimension names;
/// adding one requires a schema bump and a contract update in
/// docs/reports/drift-reports.md (Section 2).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftDimensionType
{
    Intent,
    Spec,
    TaskJob,
    Architecture,
    Documentation,
    Marketing,
    Design,
    Test,
    Runtime,
    Process,
    Schema,
    Token,
}

/// <summary>Priority for a follow-up task suggestion.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftFollowUpPriority
{
    Low,
    Normal,
    High,
    Critical,
}

/// <summary>
/// How the UI should present this report. The Markdown sibling is the durable
/// human artifact; the JSON sidecar is the additive convenience. A failed
/// JSON parse never hides the Markdown.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftReportParseStatus
{
    /// <summary>Markdown sibling exists and the JSON sidecar parses cleanly.</summary>
    Structured,
    /// <summary>Markdown sibling exists; no JSON sidecar is present.</summary>
    Unstructured,
    /// <summary>Markdown sibling exists; the JSON sidecar failed to parse or validate.</summary>
    MalformedJson,
}

/// <summary>
/// Who wrote the report. Descriptive only; the participant id, when set,
/// joins this report to the Agent Message Bus participant graph.
/// </summary>
public sealed record DriftReportProducer(
    DriftReportProducerKind Kind,
    string? ParticipantId = null,
    string? Agent = null);

/// <summary>
/// Scope record for one drift report. Project scope is the common case for
/// the manual project-level Drift action; the other kinds remain in the
/// contract so later producers (workspace audits, task-scoped or run-scoped
/// drift, time-windowed inspections) reuse the same record.
/// </summary>
public sealed record DriftReportScope(
    DriftReportScopeKind Kind,
    string? TaskId = null,
    string? RunId = null,
    string? TimeWindow = null,
    IReadOnlyList<string>? SourceRefs = null);

/// <summary>
/// Counts of findings by severity that contributed to a dimension's score.
/// Mirrors <c>scoreInputs.findingsBySeverity</c> in the schema.
/// </summary>
public sealed record DriftFindingSeverityCounts(
    int Info = 0,
    int Warn = 0,
    int High = 0,
    int Critical = 0);

/// <summary>
/// Transparent breakdown of the inputs that produced a dimension's
/// <see cref="DriftDimension.Score"/>. Consumers may render this as a
/// tooltip or drill-down; the weighting is documented in
/// docs/reports/drift-reports.md (Section 5) so the score is reproducible from
/// these inputs.
/// </summary>
public sealed record DriftScoreInputs(
    DriftFindingSeverityCounts? FindingsBySeverity = null,
    IReadOnlyList<string>? AffectedSurfaces = null,
    int RecurrenceCount = 0,
    double? OldestFindingAgeDays = null,
    int TrackedFindings = 0,
    int TotalFindings = 0);

/// <summary>
/// One itemised finding inside a dimension. Each finding carries its own
/// status so a reviewer can mark one item Tracked while the dimension as a
/// whole stays New.
/// </summary>
public sealed record DriftFinding(
    string FindingId,
    DriftSeverity Severity,
    string Summary,
    DriftFindingStatus Status,
    DateTime? FirstSeenAt = null,
    DateTime? LastSeenAt = null,
    string? TrackedTaskId = null,
    IReadOnlyList<string>? EvidenceRefs = null);

/// <summary>One per-dimension entry in a drift report.</summary>
public sealed record DriftDimension(
    DriftDimensionType Type,
    int Score,
    DriftSeverity Severity,
    double Confidence,
    double SourceCoverage,
    DriftFindingStatus Status,
    string Summary,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string> RecommendedActions,
    DriftScoreInputs? ScoreInputs = null,
    IReadOnlyList<DriftFinding>? Findings = null);

/// <summary>One follow-up task candidate. Suggestion until the user creates
/// the actual job.</summary>
public sealed record DriftFollowUpSuggestion(
    string Title,
    string Summary,
    DriftFollowUpPriority Priority,
    DriftDimensionType? RelatedDimension = null,
    string? TargetState = null,
    string? CreatedJobId = null);

/// <summary>
/// One element in a high-level architecture map. Drives the marble surface on
/// the project Drift view (ROADMAP "Architecture marble drift surface"). The
/// schema caps the element count at ten; the in-code validator enforces the
/// same ceiling so a mis-emitted report is rejected at append time rather than
/// silently overflowing the surface.
/// </summary>
public sealed record DriftArchitectureElement(
    string ElementId,
    string Label,
    string ExpectedRole,
    int Score,
    DriftSeverity Severity,
    double SourceCoverage,
    DriftFindingStatus Status,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string>? Guidelines = null,
    IReadOnlyList<string>? AllowedDependencies = null,
    IReadOnlyList<string>? SourceRefs = null,
    string? Summary = null,
    IReadOnlyList<string>? FollowUpTaskSuggestions = null);

/// <summary>
/// Optional high-level architecture model carried by a drift report. When
/// present, the Drift project surface renders it as a marble map: up to ten
/// scan-friendly cards, each linking back to evidence. Mirrors the
/// <c>architectureModel</c> branch in <c>docs/schemas/drift-report.schema.json</c>.
/// </summary>
public sealed record DriftArchitectureModel(
    string ModelId,
    string Title,
    IReadOnlyList<DriftArchitectureElement> Elements,
    string? SourceRef = null);

/// <summary>
/// Sensible default values used by tests and the rare caller that does not
/// have a specific producer or parse status. Production producers should set
/// these explicitly so the recorded report reflects the real producer.
/// </summary>
public static class DriftReportDefaults
{
    public static readonly DriftReportProducer ManualProducer =
        new(DriftReportProducerKind.Manual);
}

/// <summary>
/// One drift report. JSON sidecar contract plus the Markdown sibling under
/// <c>logs/drift/&lt;project&gt;/&lt;reportId&gt;.md</c>. Reports are
/// append-only and immutable; corrections land as a new report, not as an
/// edit. Status transitions on findings (e.g. New -> Tracked) happen by
/// emitting a new report that supersedes the prior one.
/// </summary>
public sealed record DriftReport(
    string ReportId,
    string Project,
    DateTime CreatedAt,
    DriftReportTrigger Trigger,
    DriftReportScope Scope,
    int OverallScore,
    DriftScoreBand ScoreBand,
    IReadOnlyList<DriftDimension> Dimensions,
    string Summary,
    IReadOnlyList<DriftFollowUpSuggestion> FollowUpTaskSuggestions,
    int SchemaVersion = 1,
    DriftArchitectureModel? ArchitectureModel = null,
    DriftReportProducer? Producer = null,
    DriftReportParseStatus ParseStatus = DriftReportParseStatus.Structured,
    string? ParseError = null,
    IReadOnlyList<string>? Tags = null,
    string? MarkdownPath = null);
