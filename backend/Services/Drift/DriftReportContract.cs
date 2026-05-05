using System.Text.Json.Serialization;

namespace OrchestratorApi.Services.Drift;

/// <summary>
/// First in-code projection of <c>docs/schemas/drift-report.schema.json</c>.
/// The companion task <c>drift-report-schema-and-scoring</c> will own the
/// finalised contract (full dimension vocabulary, weighted scoring, validator
/// round-trip tests). This contract is the minimum the ADR / Code Drift
/// producer needs to emit a schema-valid record without blocking on that
/// queued work.
/// </summary>
/// <remarks>
/// Drift reports are a separate evidence pile from
/// <see cref="OrchestratorApi.Services.Analysis.AnalysisReport"/>: drift is a
/// project dimension beside Architecture (ROADMAP "Drift Control"), so the
/// store, schema, and surfaces are intentionally distinct. The two
/// contracts will converge through normal schema work, not through
/// inheritance.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftReportTrigger
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
/// no agent narrative was supplied: the report exists, but the score has not
/// been computed yet.
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
/// Drift dimension vocabulary. Mirrors the full schema enum so future
/// producers can extend without re-defining the type. The ADR / Code Drift
/// producer in this slice emits Architecture, Documentation, Process, and
/// Schema entries.
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
/// Scope record for one drift report. Project scope is the common case for
/// the ADR / Code Drift action; the other kinds remain in the contract so
/// later producers (task-scoped or run-scoped) reuse the same record.
/// </summary>
public sealed record DriftReportScope(
    DriftReportScopeKind Kind,
    string? TaskId = null,
    string? RunId = null,
    string? TimeWindow = null,
    IReadOnlyList<string>? SourceRefs = null);

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
    IReadOnlyList<string> RecommendedActions);

/// <summary>One follow-up task candidate. Suggestion until the user creates
/// the actual job.</summary>
public sealed record DriftFollowUpSuggestion(
    string Title,
    string Summary,
    DriftFollowUpPriority Priority,
    DriftDimensionType? RelatedDimension = null);

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
/// One drift report. JSON sidecar contract plus the Markdown sibling under
/// <c>logs/drift/&lt;project&gt;/&lt;reportId&gt;.md</c>. Reports are
/// append-only and immutable; corrections land as a new report, not as an
/// edit.
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
    DriftArchitectureModel? ArchitectureModel = null);
