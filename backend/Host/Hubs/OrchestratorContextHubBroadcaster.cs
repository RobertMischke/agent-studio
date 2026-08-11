using Microsoft.AspNetCore.SignalR;

namespace AgentStudio.Host;

/// <summary>
/// Publishes central Task Server context-store changes over the existing
/// TaskHub connection. The context store remains authoritative; this event is
/// only a refresh hint for connected Studio clients.
/// </summary>
public sealed class OrchestratorContextHubBroadcaster(
    IHubContext<TaskHub> hub,
    AgentStudio.Registry.ProjectRegistry projects,
    ILogger<OrchestratorContextHubBroadcaster> logger)
{
    public async Task ContextChangedAsync(
        string projectName,
        string contextKey,
        DateTime updatedAt,
        CancellationToken ct)
    {
        try
        {
            await hub.Clients
                .Group(TaskHub.ProjectGroup(projectName, projects))
                .SendAsync(
                    "orchestratorContextChanged",
                    new { contextKey, projectName, updatedAt },
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The context turn is already durable. A disconnected caller may
            // cancel only this best-effort refresh hint.
            logger.LogDebug(
                "TaskHub context refresh broadcast cancelled for {ContextKey}",
                contextKey);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "TaskHub context refresh broadcast failed for {ContextKey}",
                contextKey);
        }
    }
}
