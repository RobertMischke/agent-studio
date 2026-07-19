using System.Text.Json.Serialization;

namespace AgentStudio.Analysis;

/// <summary>
/// Scope kind for one analysis report. Mirrors the schema's
/// <c>scope.kind</c> enum.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisReportScopeKind
{
    Workspace,
    Project,
    Task,
    Run,
    TimeWindow
}

/// <summary>
/// Producer kind for one analysis report. Mirrors the schema's
/// <c>producer.kind</c> enum. Descriptive only; capability comes from code.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisReportProducerKind
{
    Manual,
    Scheduled,
    MetaCycle,
    SupportingAgent,
    ExternalMonitor
}

/// <summary>
/// What caused the analysis to run. Mirrors the schema's <c>trigger</c> enum.
/// Carried separately from <see cref="AnalysisReportProducerKind"/> so a single
/// producer can fire under multiple triggers.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisReportTrigger
{
    Manual,
    Scheduled,
    MetaCycle,
    SupportingAgent,
    ExternalMonitor
}

/// <summary>
/// Severity ladder used for the report itself and for individual findings.
/// Critical sits above the supervisor's three-step ladder because analysis
/// reports cover security and architecture risks that warrant a louder badge.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisReportSeverity
{
    Info,
    Warn,
    High,
    Critical
}

/// <summary>
/// How the UI should present this report. The Markdown sibling is the durable
/// human artifact; the JSON sidecar is the additive convenience. A failed JSON
/// parse never hides the Markdown.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisReportParseStatus
{
    /// <summary>Markdown sibling exists and the JSON sidecar parses cleanly.</summary>
    Structured,
    /// <summary>Markdown sibling exists; no JSON sidecar is present.</summary>
    Unstructured,
    /// <summary>Markdown sibling exists; the JSON sidecar failed to parse or validate.</summary>
    MalformedJson
}

/// <summary>
/// Reference kinds that an analysis report can point at. The report cites
/// these by stable id; it does not copy the underlying bytes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisReportReferenceKind
{
    Job,
    Run,
    Commit,
    Screenshot,
    BusMessage,
    RuntimeEvent,
    PreviousReport,
    LogSlice,
    Doc
}

/// <summary>
/// Priority for one follow-up task suggestion. Mirrors the schema enum.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisReportFollowUpPriority
{
    Low,
    Normal,
    High,
    Critical
}

/// <summary>
/// Optional grouping label for a follow-up suggestion so the UI can colour or
/// filter candidates. Mirrors the schema's <c>relatedTopic</c> enum.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisReportFollowUpRelatedTopic
{
    QueueHealth,
    DocsDrift,
    RoadmapAlignment,
    StaleJobs,
    Security,
    Architecture,
    Qa,
    TokenSpend,
    RuntimeObservability,
    UxUi,
    Other
}

/// <summary>
/// Initial state for a queued follow-up task. Default is
/// <c>1-preparation</c> so reports never bypass the user; <c>2-ready</c> is
/// reserved for templated topics with a fixed contract.
/// </summary>
public static class AnalysisReportFollowUpTargetStates
{
    public const string OnePreparation = TaskStates.Preparation;
    public const string TwoReady = TaskStates.Ready;
}

/// <summary>
/// Wall-clock window for <see cref="AnalysisReportScopeKind.TimeWindow"/>
/// reports.
/// </summary>
public sealed record AnalysisReportTimeWindow(
    DateTime From,
    DateTime To);

/// <summary>
/// Scope record for one analysis report. The kind decides which optional
/// pointers are populated; the <see cref="AnalysisReportValidator"/> enforces
/// the scope-specific required fields.
/// </summary>
public sealed record AnalysisReportScope(
    AnalysisReportScopeKind Kind,
    string? Project = null,
    string? JobId = null,
    int? RunIndex = null,
    AnalysisReportTimeWindow? TimeWindow = null);

/// <summary>
/// Who wrote the report. Descriptive only; the participant id, when set,
/// joins this report to the Agent Message Bus participant graph.
/// </summary>
public sealed record AnalysisReportProducer(
    AnalysisReportProducerKind Kind,
    string? ParticipantId = null,
    string? Agent = null);

/// <summary>
/// One typed pointer to evidence. Reports cite by stable id and do not copy
/// raw bytes. Reference shapes are documented in
/// <c>docs/system/reports/analysis-reports.md</c>.
/// </summary>
public sealed record AnalysisReportReference(
    AnalysisReportReferenceKind Kind,
    string Ref,
    string? Label = null);

/// <summary>
/// One typed finding. Optional in the report; helpful when the producer
/// wants the UI to surface a list rather than a single summary.
/// </summary>
public sealed record AnalysisReportFinding(
    string Topic,
    AnalysisReportSeverity Severity,
    string Message,
    IReadOnlyList<string>? EvidenceRefs = null);

/// <summary>
/// One follow-up task candidate. Suggestions are typed; creation is
/// explicit. Until <see cref="CreatedJobId"/> is set, the suggestion is a
/// candidate, not a commitment.
/// </summary>
public sealed record AnalysisReportFollowUpTaskSuggestion(
    string Title,
    string Summary,
    AnalysisReportFollowUpPriority Priority,
    AnalysisReportFollowUpRelatedTopic? RelatedTopic = null,
    string? TargetState = null,
    string? CreatedJobId = null);

/// <summary>
/// One inspection report. Markdown sibling at
/// <c>logs/analysis/&lt;project&gt;/&lt;reportId&gt;.md</c> is the durable
/// human artifact; this JSON record is the additive app contract.
/// </summary>
/// <remarks>
/// Schema: <c>docs/app/schemas/analysis-report.schema.json</c>. Storage rules,
/// producer model, and parse-failure semantics are documented in
/// <c>docs/system/reports/analysis-reports.md</c>.
/// </remarks>
public sealed record AnalysisReport(
    string ReportId,
    DateTime CreatedAt,
    AnalysisReportScope Scope,
    AnalysisReportProducer Producer,
    AnalysisReportTrigger Trigger,
    string Topic,
    string Summary,
    AnalysisReportSeverity Severity,
    AnalysisReportParseStatus ParseStatus,
    IReadOnlyList<AnalysisReportReference> References,
    IReadOnlyList<AnalysisReportFollowUpTaskSuggestion> FollowUpTaskSuggestions,
    string? ParseError = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<AnalysisReportFinding>? Findings = null,
    string? MarkdownPath = null,
    int SchemaVersion = 1);
