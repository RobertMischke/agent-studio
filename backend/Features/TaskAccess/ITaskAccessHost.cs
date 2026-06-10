namespace AgentStudio.TaskAccess;

/// <summary>
/// Lifecycle surface for the Task Access Layer: boot the index from
/// every watched project's lane folders, reload one project on demand,
/// and shut the layer down cleanly. Owned by a hosted service in
/// later phases; phase 1 ships only the contract.
/// </summary>
public interface ITaskAccessHost
{
    Task BootAsync(CancellationToken ct = default);

    Task ReloadProjectAsync(string projectName, CancellationToken ct = default);

    Task ShutdownAsync(CancellationToken ct = default);
}
