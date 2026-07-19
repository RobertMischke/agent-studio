namespace AgentStudio.Runner;

/// <summary>
/// Admission policy for an operator-requested deferred mode change. Existing
/// runs drain, but a pending switch to manual or paused closes auto-pickup
/// admission immediately.
/// </summary>
public static class DeferredModePickupPolicy
{
    public static bool AllowsAutoPickup(string? pendingMode)
        => pendingMode is not ("manual" or "paused");
}
