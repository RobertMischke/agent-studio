namespace AgentStudio.Shared;

/// <summary>
/// F45a — top-level grouping for projects on the kanban board. Workspaces
/// are pure metadata: no folder, no on-disk identity beyond an entry in
/// <c>&lt;TaskRepository&gt;/.metadata/workspaces.json</c>. A project belongs
/// to exactly one workspace via its <see cref="ProjectRecord.WorkspaceId"/>.
///
/// <para>This record is the F45 successor to the old "workspace == folder"
/// model embodied by <see cref="AgentStudio.Configuration.WorkspaceManagementService"/>;
/// the legacy service stays in place and continues to manage per-project
/// folders until F45c migrates the consumers over.</para>
/// </summary>
public record WorkspaceRecord
{
    /// <summary>Stable identifier, e.g. <c>ws-default</c>. Never re-used, never renamed.</summary>
    public string Id { get; init; } = "";
    /// <summary>Human-readable label. Free to edit, lives only here.</summary>
    public string DisplayName { get; init; } = "";
    /// <summary>Render order within the sidebar. Smaller comes first.</summary>
    public int SortOrder { get; init; }
    /// <summary>True for the single workspace that owns auto-discovered legacy projects.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Optional accent colour (CSS hex string).</summary>
    public string? Color { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// F45a — per-project registry entry. The project id is the stable identity
/// for jobKey (<c>PROJ-001::job-slug</c>), folder lookups, and cross-system
/// references. Display name and short code are mutable metadata.
///
/// <para>For legacy projects discovered from <c>WatchPaths</c> on first boot,
/// <see cref="StorageLocation"/> points at the existing task folder; F45c
/// will physically relocate those folders to
/// <c>&lt;TaskRepository&gt;/projects/&lt;Id&gt;/</c> and rewrite the field.</para>
/// </summary>
public record ProjectRecord
{
    /// <summary>Stable identifier in the form <c>PROJ-001</c>. Immutable.</summary>
    public string Id { get; init; } = "";
    /// <summary>Free-text display label. Renamable without touching the filesystem.</summary>
    public string DisplayName { get; init; } = "";
    /// <summary>Short prefix for task display keys (e.g. <c>ATP</c> in <c>ATP-130</c>). 2-6 chars, A-Z and 0-9.</summary>
    public string ShortCode { get; init; } = "";
    /// <summary>Owning workspace; references <see cref="WorkspaceRecord.Id"/>.</summary>
    public string WorkspaceId { get; init; } = "";
    public string? Color { get; init; }
    public string? CliDefault { get; init; }
    public string? ModelDefault { get; init; }
    public int SortOrder { get; init; }
    /// <summary>Next task key counter (monotonic, per project). Never re-used.</summary>
    public int NextTaskKeySeq { get; init; } = 1;
    /// <summary>
    /// Absolute path to the project's task folder on disk. For legacy
    /// auto-discovered projects this is the resolved <c>WatchPathEntry.Path</c>;
    /// for projects created via the new API it points at
    /// <c>&lt;TaskRepository&gt;/projects/&lt;Id&gt;/</c>.
    /// </summary>
    public string StorageLocation { get; init; } = "";
    /// <summary>
    /// Absolute path of the project's repository checkout, when the project
    /// has one. Durable registry data (API-mutable), NOT an appsettings
    /// override: the docs/wiki surface derives its root from
    /// <c>&lt;RepositoryPath&gt;/docs</c> by convention. Null for task-only
    /// projects and for repos where the path is derivable from the storage
    /// layout (<c>&lt;repo&gt;/.orchestrator/jobs</c>).
    /// </summary>
    public string? RepositoryPath { get; init; }
    public bool Archived { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// On-disk shape of <c>&lt;TaskRepository&gt;/.metadata/workspaces.json</c>.
/// </summary>
public record WorkspacesFile
{
    public int Version { get; init; } = 1;
    public List<WorkspaceRecord> Workspaces { get; init; } = [];
}

/// <summary>
/// On-disk shape of <c>&lt;TaskRepository&gt;/.metadata/projects.json</c>.
/// <see cref="NextProjectIdSeq"/> is the global counter the registry hands
/// out next; never wound backwards.
/// </summary>
public record ProjectsFile
{
    public int Version { get; init; } = 1;
    public int NextProjectIdSeq { get; init; } = 1;
    public List<ProjectRecord> Projects { get; init; } = [];
}

/// <summary>
/// Well-known constants for the F45 default workspace.
/// </summary>
public static class DefaultWorkspace
{
    public const string Id = "ws-default";
    public const string DisplayName = "Workspace";
    public const string Color = "#a78bfa";
}

/// <summary>
/// F66 — return shape for <c>DELETE /api/workspaces/{id}</c>. The delete is
/// blocked (409) while any project is still assigned, so a successful response
/// only echoes the deleted id.
/// </summary>
public sealed record WorkspaceDeleteResult
{
    public string DeletedId { get; init; } = "";
}
