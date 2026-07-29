namespace AgentStudio.Tasks;

/// <summary>
/// Normalizes the integration line captured by the runner that prepared a task
/// worktree. The persisted value is a full local branch ref so it cannot be
/// confused with a remote-tracking ref or with a later project default.
/// </summary>
public static class TaskIntegrationBranch
{
    public static string? NormalizeRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var branch = Name(value);
        return string.IsNullOrWhiteSpace(branch) ? null : $"refs/heads/{branch}";
    }

    public static string Name(string? value, string fallback = "develop")
    {
        var branch = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            branch = branch["refs/heads/".Length..];
        else if (branch.StartsWith("refs/remotes/origin/", StringComparison.OrdinalIgnoreCase))
            branch = branch["refs/remotes/origin/".Length..];
        else if (branch.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            branch = branch["origin/".Length..];
        return branch;
    }

    public static string Resolve(TaskInfo task, string? configured)
        => Name(task.IntegrationBranch, Name(configured));
}
