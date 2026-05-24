namespace OrchestratorApi.Services.Registry;

/// <summary>
/// F45a — single source of truth for the on-disk locations of
/// <c>workspaces.json</c> and <c>projects.json</c>. Both files live under
/// <c>&lt;TaskRepository&gt;/.metadata/</c>; the directory is created lazily
/// on first write.
/// </summary>
public static class RegistryPaths
{
    public const string MetadataFolderName = ".metadata";
    public const string WorkspacesFileName = "workspaces.json";
    public const string ProjectsFileName = "projects.json";

    public static string MetadataDir(string taskRepositoryRoot) =>
        Path.Combine(taskRepositoryRoot, MetadataFolderName);

    public static string WorkspacesFilePath(string taskRepositoryRoot) =>
        Path.Combine(MetadataDir(taskRepositoryRoot), WorkspacesFileName);

    public static string ProjectsFilePath(string taskRepositoryRoot) =>
        Path.Combine(MetadataDir(taskRepositoryRoot), ProjectsFileName);
}
