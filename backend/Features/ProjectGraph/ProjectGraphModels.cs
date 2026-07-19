namespace AgentStudio.ProjectGraph;

/// <summary>
/// Read-only, repository-fact projection used by the Project Hub and by the
/// prompt-readable project-map generator. The contract deliberately stops at
/// manifests, package/project references, and rough sizes. It is not a code
/// call graph.
/// </summary>
public sealed record ProjectGraphSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public string GeneratorVersion { get; init; } = "project-graph-v1";
    public string SnapshotId { get; init; } = "";
    public string? PreviousSnapshotId { get; init; }
    public string CaptureMode { get; init; } = "explicit";
    public DateTime CapturedAtUtc { get; init; }
    public string FocusProjectId { get; init; } = "";
    public string FocusProjectKey { get; init; } = "";
    public IReadOnlyList<ProjectGraphProject> Projects { get; init; } = [];
    public IReadOnlyList<ProjectGraphComponent> Components { get; init; } = [];
    public IReadOnlyList<ProjectGraphDependency> Dependencies { get; init; } = [];
}

public sealed record ProjectGraphProject
{
    /// <summary>Canonical immutable registry identity (PROJ-NNN).</summary>
    public string Id { get; init; } = "";
    /// <summary>Mutable display alias retained for Project Hub compatibility.</summary>
    public string Key { get; init; } = "";
    /// <summary>Mutable short-code alias; never used to derive component IDs.</summary>
    public string ShortCode { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Status { get; init; } = "unavailable";
    public string? RepositoryLabel { get; init; }
    public string? SourceRevision { get; init; }
    public string SourceState { get; init; } = "unavailable";
    public IReadOnlyList<string> Solutions { get; init; } = [];
    public IReadOnlyList<string> Workflows { get; init; } = [];
    public IReadOnlyList<ProjectGraphTechnology> Technologies { get; init; } = [];
    public IReadOnlyList<string> ComponentIds { get; init; } = [];
    public ProjectGraphSize Size { get; init; } = new();
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record ProjectGraphComponent
{
    /// <summary>Stable ID derived from ProjectId, kind, and manifest path.</summary>
    public string Id { get; init; } = "";
    /// <summary>Canonical immutable project identity.</summary>
    public string ProjectId { get; init; } = "";
    /// <summary>Mutable short-code alias for display only.</summary>
    public string ProjectKey { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public IReadOnlyList<ProjectGraphTechnology> Technologies { get; init; } = [];
    public ProjectGraphSize Size { get; init; } = new();
}

public sealed record ProjectGraphDependency
{
    public string FromComponentId { get; init; } = "";
    public string? ToComponentId { get; init; }
    public string Kind { get; init; } = "";
    public string Resolution { get; init; } = "resolved";
    public string? TargetHint { get; init; }
    public string Evidence { get; init; } = "";
}

public sealed record ProjectGraphTechnology
{
    public string Slug { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed record ProjectGraphSize
{
    public int Files { get; init; }
    public long Lines { get; init; }
}

internal sealed record ProjectGraphTarget(ProjectRecord Record, string? RepositoryRoot);
