namespace OrchestratorApi.Models;

/// <summary>
/// Body for <c>POST /api/roadmap/intake</c>. The user dumps a long, often
/// multi-language message; the splitter turns it into reviewable
/// candidates without side effects. <see cref="WatchPath"/> is required
/// because the preview UI is project-scoped: the user is sending the
/// dump from a specific project's chat surface.
/// </summary>
public record RoadmapIntakeRequest
{
    public string Text { get; init; } = "";
    public string WatchPath { get; init; } = "";
}

/// <summary>
/// Splitter response. <see cref="Candidates"/> mirrors the JSON shape
/// produced by the Haiku splitter; the endpoint passes them through
/// unchanged so the user can edit each one in place before confirming.
/// </summary>
public record RoadmapIntakeResponse
{
    public List<RoadmapIntakeCandidate> Candidates { get; init; } = [];
    public string Notes { get; init; } = "";
}

public record RoadmapIntakeCandidate
{
    /// <summary>Short imperative title. English.</summary>
    public string Title { get; init; } = "";
    /// <summary>Self-contained task body in English Markdown.</summary>
    public string PromptBody { get; init; } = "";
    /// <summary><c>feature|bug|adr|chore|research</c> - hint, not enforced.</summary>
    public string Kind { get; init; } = "feature";
    /// <summary>Multiple of 10 starting at 10; preserves implied sequence.</summary>
    public int SuggestedOrder { get; init; } = 10;
    /// <summary>One of <c>claude|codex|copilot|gemini</c>; defaults to <c>claude</c>.</summary>
    public string SuggestedCliType { get; init; } = "claude";
    /// <summary>One-sentence reason shown to the user in the preview list.</summary>
    public string Rationale { get; init; } = "";
}

/// <summary>
/// Body for <c>POST /api/roadmap/intake/confirm</c>. The user has reviewed
/// (and possibly edited) the splitter's candidates and now wants them
/// materialised as job folders in <c>1-preparation</c>. The endpoint
/// never lands jobs in <c>2-ready</c> - even confirmed intake gets one
/// last human pass on the board before queueing.
/// </summary>
public record RoadmapIntakeConfirmRequest
{
    public string WatchPath { get; init; } = "";
    public List<RoadmapIntakeCandidate> Candidates { get; init; } = [];
}

public record RoadmapIntakeConfirmResponse
{
    public List<RoadmapIntakeCreatedJob> Created { get; init; } = [];
    public List<string> Skipped { get; init; } = [];
}

public record RoadmapIntakeCreatedJob
{
    public string JobId { get; init; } = "";
    public string Title { get; init; } = "";
    public string State { get; init; } = JobStates.Preparation;
}
