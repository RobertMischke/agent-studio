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
        var principal = LivePrincipal();
        var task = _scanner.FindJob(jobId);
        if (task is null) throw new HubException("Task not found.");
        if (principal is not null && !ProjectAccessAuthorization.Allows(principal.User, task.ProjectName, _projects))
            throw new HubException("Project access denied.");
        return Groups.AddToGroupAsync(Context.ConnectionId, ConversationProjector.GroupName(jobId));
    }

    public Task UnsubscribeFromConversation(string jobId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationProjector.GroupName(jobId));

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
