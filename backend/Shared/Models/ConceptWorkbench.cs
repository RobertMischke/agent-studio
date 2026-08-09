namespace AgentStudio.Shared;

/// <summary>One implementation card proposed by a concept Workbench.</summary>
public sealed record ConceptImplementationTask
{
    public string Title { get; init; } = "";
    public string PromptMarkdown { get; init; } = "";
}

/// <summary>
/// The machine-readable descriptor beside a concept Workbench's
/// <c>index.html</c>. It extends the existing Workbench descriptor additively:
/// older Workbenches without <see cref="ImplementationTasks"/> remain readable.
/// </summary>
public sealed record ConceptWorkbenchDescriptor
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Entrypoint { get; init; } = "index.html";
    public string Status { get; init; } = "decision-pending";
    public string Phase { get; init; } = "sight-review";
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
    public List<string> SourceTaskKeys { get; init; } = [];
    public List<ConceptImplementationTask> ImplementationTasks { get; init; } = [];
}

/// <summary>Durable source-document reference returned by concept promotion.</summary>
public sealed record ConceptSourceDocument
{
    public string RepoRelativePath { get; init; } = "";
    public string Title { get; init; } = "";
}

public sealed record PromoteConceptResponse
{
    public ConceptSourceDocument Source { get; init; } = new();
    public List<ConceptImplementationTask> Items { get; init; } = [];
    public string Mode { get; init; } = TaskModes.Coding;
    public string TargetState { get; init; } = TaskStates.Preparation;
    public string WatchPath { get; init; } = "";
    public string ProjectName { get; init; } = "";
}

public sealed record PromoteConceptRequest
{
    /// <summary>
    /// Zero-based descriptor items to create. Null or empty creates every
    /// proposed implementation item.
    /// </summary>
    public List<int>? ItemIndexes { get; init; }
}

public sealed record PromotedConceptTask
{
    public string JobId { get; init; } = "";
    public string? TaskKey { get; init; }
    public string Title { get; init; } = "";
}

public sealed record PromoteConceptTasksResponse
{
    public ConceptSourceDocument Source { get; init; } = new();
    public List<PromotedConceptTask> Created { get; init; } = [];
}
