using System.Text.Json.Serialization;

namespace AgentStudio.Runner;

/// <summary>
/// Workspace-level executive summary returned by
/// <c>GET /api/workspace/summary</c>. Mirrors
/// <c>docs/schemas/executive-summary.schema.json</c>.
/// </summary>
public sealed record ExecutiveSummary(
    [property: JsonPropertyName("windowStart")] DateTime WindowStart,
    [property: JsonPropertyName("windowEnd")] DateTime WindowEnd,
    [property: JsonPropertyName("headline")] string Headline,
    [property: JsonPropertyName("byProject")] IReadOnlyList<ExecutiveSummaryProject> ByProject,
    [property: JsonPropertyName("crashes")] IReadOnlyList<ExecutiveSummaryCrash> Crashes,
    [property: JsonPropertyName("topDecisions")] IReadOnlyList<ExecutiveSummaryDecision> TopDecisions,
    [property: JsonPropertyName("openHumanDecisions")] IReadOnlyList<ExecutiveSummaryOpenDecision> OpenHumanDecisions,
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion = "1");

public sealed record ExecutiveSummaryProject(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("jobsMoved")] IReadOnlyList<ExecutiveSummaryJobMove> JobsMoved,
    [property: JsonPropertyName("decisionsMade")] int DecisionsMade,
    [property: JsonPropertyName("advisoriesRaised")] int AdvisoriesRaised,
    [property: JsonPropertyName("commits")] IReadOnlyList<ExecutiveSummaryCommit> Commits);

public sealed record ExecutiveSummaryJobMove(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("fromState")] string FromState,
    [property: JsonPropertyName("toState")] string ToState,
    [property: JsonPropertyName("at")] DateTime At);

public sealed record ExecutiveSummaryCommit(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("shortSha")] string ShortSha,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("at")] DateTime At);

public sealed record ExecutiveSummaryCrash(
    [property: JsonPropertyName("at")] DateTime At,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("summary")] string? Summary);

public sealed record ExecutiveSummaryDecision(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("decisionId")] string DecisionId,
    [property: JsonPropertyName("at")] DateTime At,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("jobId")] string? JobId);

public sealed record ExecutiveSummaryOpenDecision(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt);
