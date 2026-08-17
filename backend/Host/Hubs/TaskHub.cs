using Microsoft.AspNetCore.SignalR;

namespace AgentStudio.Host;

public class TaskHub : Hub
{
    internal const string UnscopedSecurityGroup = "security:unscoped";

    private readonly IConfiguration _configuration;
    private readonly AccessSecurityStore _security;
    private readonly TaskScannerService _scanner;
    private readonly AgentStudio.Registry.ProjectRegistry _projects;

    public TaskHub(
        IConfiguration configuration,
        AccessSecurityStore security,
        TaskScannerService scanner,
        AgentStudio.Registry.ProjectRegistry projects)
    {
        _configuration = configuration;
        _security = security;
        _scanner = scanner;
        _projects = projects;
    }

    public override async Task OnConnectedAsync()
    {
        // Public demo: an anonymous visitor is scoped to the announced demo
        // projects and never joins the unscoped group, so a project the seed did
        // not announce cannot reach the socket even if it exists in the store.
        if (SecurityProfiles.IsPublicDemo(_configuration))
        {
            foreach (var project in DemoProjects())
                await Groups.AddToGroupAsync(Context.ConnectionId, ProjectGroup(project, _projects));
            await base.OnConnectedAsync();
            return;
        }

        var principal = LivePrincipal();
        if (principal is null || principal.User.Role == StudioRoles.Owner || principal.User.Projects.Count == 0)
            await Groups.AddToGroupAsync(Context.ConnectionId, UnscopedSecurityGroup);
        foreach (var project in _projects.List())
        {
            if (principal is null || ProjectAccessAuthorization.Allows(principal.User, project.Id, _projects))
                await Groups.AddToGroupAsync(Context.ConnectionId, ProjectGroup(project.Id, _projects));
        }
        await base.OnConnectedAsync();
    }

    // Client methods:
    // - jobsChanged                                          → board refresh
    // - cliOutput(jobId, line, stream, timestamp)            → live CLI output line
    // - cliStarted(jobId, processId, startedAt)              → CLI process started
    // - cliFinished(jobId, exitCode, duration, status)       → CLI process finished
    // - runnerStatusChanged(projectName, mode, activeJobId)  → runner mode/status change
    // - busMessageAdded(AgentMessage)                        → new bus event appended
    // - orchestratorContextChanged(payload)                 → central Chat History refresh
    // - workbenchCreated/Updated/DecisionRecorded/StatusChanged(WorkbenchHubEvent)
    // F22:
    // - conversationEventsAppended(jobId, ProjectedEvent[])  → live append from a source change
    // - conversationProjectionInvalidated(jobId)             → client should refetch the snapshot

    /// <summary>
    /// Join the per-job group that receives <c>conversationEventsAppended</c>
    /// and <c>conversationProjectionInvalidated</c> pushes. Caller is the
    /// detail-pane component that opened the protocol tab for the job.
    /// </summary>
    public Task SubscribeToConversation(string jobId)
    {
        if (SecurityProfiles.IsPublicDemo(_configuration))
        {
            var demoTask = _scanner.FindJob(jobId);
            if (demoTask is null) throw new HubException("Task not found.");
            if (!IsDemoProjectGroup(ProjectGroup(demoTask.ProjectName, _projects)))
                throw new HubException("Project access denied.");
            return Groups.AddToGroupAsync(Context.ConnectionId, ConversationProjector.GroupName(jobId));
        }

        var principal = LivePrincipal();
        var task = _scanner.FindJob(jobId);
        if (task is null) throw new HubException("Task not found.");
        if (principal is not null && !ProjectAccessAuthorization.Allows(principal.User, task.ProjectName, _projects))
            throw new HubException("Project access denied.");
        return Groups.AddToGroupAsync(Context.ConnectionId, ConversationProjector.GroupName(jobId));
    }

    public Task UnsubscribeFromConversation(string jobId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationProjector.GroupName(jobId));

    /// <summary>The announced demo projects, defaulting to the ADR-0056 pair.</summary>
    private IReadOnlyList<string> DemoProjects()
    {
        var configured = _configuration
            .GetSection($"{PublicDemoOptions.SectionName}:Projects")
            .Get<string[]>();
        return configured is { Length: > 0 } ? configured : new PublicDemoOptions().Projects;
    }

    /// <summary>
    /// Compare on the resolved group name so a task addressed by display name,
    /// short code, or storage location is judged exactly the way its events are
    /// routed. One resolution rule, not two.
    /// </summary>
    private bool IsDemoProjectGroup(string group)
        => DemoProjects().Any(project =>
            string.Equals(ProjectGroup(project, _projects), group, StringComparison.Ordinal));

    internal static string ProjectGroup(string projectHandle, AgentStudio.Registry.ProjectRegistry projects)
    {
        var project = projects.FindByIdOrDisplayName(projectHandle)
                      ?? projects.FindByShortCode(projectHandle)
                      ?? projects.FindByStorageLocation(projectHandle);
        return "project:" + (project?.Id ?? projectHandle).ToLowerInvariant();
    }

    private HumanPrincipal? LivePrincipal()
    {
        if (!SecurityProfiles.IsNetworked(_configuration)) return null;
        var http = Context.GetHttpContext();
        var principal = _security.AuthenticateSession(http?.Request.Cookies[AccessSecurityStore.SessionCookieName], touch: false);
        if (principal is not null) return principal;
        Context.Abort();
        throw new HubException("Studio session expired.");
    }
}
