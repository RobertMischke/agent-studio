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

    private static bool Matches(ProjectRecord project, string allowed)
        => string.Equals(project.Id, allowed, StringComparison.OrdinalIgnoreCase)
           || string.Equals(project.DisplayName, allowed, StringComparison.OrdinalIgnoreCase)
           || string.Equals(project.ShortCode, allowed, StringComparison.OrdinalIgnoreCase)
           || WatchPathComparison.PathsEqual(project.StorageLocation, allowed);
}
