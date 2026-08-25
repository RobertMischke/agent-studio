namespace AgentStudio.Runner;

/// <summary>Resolves validation onto project sources, never the task-board folder.</summary>
public static class BuildProfileValidationWorkspace
{
    public static string? Resolve(WatchPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (var candidate in new[] { entry.RootPath, entry.RepositoryPath })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        return null;
    }
}
