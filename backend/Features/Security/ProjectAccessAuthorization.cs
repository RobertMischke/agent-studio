using AgentStudio.Registry;
using AgentStudio.Shared;

namespace AgentStudio.Security;

/// <summary>
/// Shared project-membership checks for routes that return workspace-wide
/// collections. Route middleware protects project-addressed URLs; collection
/// handlers must also filter their payloads so a scoped account cannot learn
/// about projects it was not assigned.
/// </summary>
public static class ProjectAccessAuthorization
{
    public static bool Allows(StudioUser user, string? projectHandle, ProjectRegistry? registry = null)
    {
        if (user.Role == StudioRoles.Owner || user.Projects.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(projectHandle)) return false;
        if (user.Projects.Contains(projectHandle, StringComparer.OrdinalIgnoreCase)) return true;

        var project = registry?.FindByIdOrDisplayName(projectHandle)
                      ?? registry?.FindByShortCode(projectHandle)
                      ?? registry?.FindByStorageLocation(projectHandle);
        return project is not null && user.Projects.Any(allowed => Matches(project, allowed));
    }

    public static IEnumerable<TaskInfo> FilterTasks(
        HttpContext context,
        IEnumerable<TaskInfo> tasks,
        ProjectRegistry registry)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human)
            return tasks;
        return tasks.Where(task => Allows(human.User, task.ProjectName, registry));
    }

    /// <summary>
    /// Membership guard for body-addressed task-set mutations (reorder,
    /// batch-move). The networked middleware cannot infer a project from routes
    /// whose target set travels in the request body, so those handlers resolve
    /// each affected task's project and call this to enforce that a scoped
    /// non-owner human is a member of every one of them. An owner or an unscoped
    /// account (empty membership) is always allowed. A null/blank resolved
    /// project for any item fails closed for a scoped account, so an unresolvable
    /// id can never smuggle a cross-project mutation through. Returns true when no
    /// human principal is present (local profile / Runner routes handled upstream).
    /// </summary>
    public static bool AllowsTasks(
        HttpContext context,
        IEnumerable<string?> taskProjects,
        ProjectRegistry? registry = null)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human)
            return true;
        var user = human.User;
        if (user.Role == StudioRoles.Owner || user.Projects.Count == 0) return true;
        foreach (var project in taskProjects)
        {
            if (string.IsNullOrWhiteSpace(project)) return false;
            if (!Allows(user, project, registry)) return false;
        }
        return true;
    }

    private static bool Matches(ProjectRecord project, string allowed)
        => string.Equals(project.Id, allowed, StringComparison.OrdinalIgnoreCase)
           || string.Equals(project.DisplayName, allowed, StringComparison.OrdinalIgnoreCase)
           || string.Equals(project.ShortCode, allowed, StringComparison.OrdinalIgnoreCase)
           || WatchPathComparison.PathsEqual(project.StorageLocation, allowed);
}
