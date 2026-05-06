namespace AgentTaskboard.UpdateService;

/// <summary>
/// Boot-time configuration. Defaults are tuned for the local dev/stable
/// devspace layout; everything is overridable via env vars / appsettings
/// (see Program.cs Bind call).
/// </summary>
public sealed class UpdateServiceOptions
{
    /// <summary>Listening URL.</summary>
    public string ListenUrl { get; set; } = "http://127.0.0.1:5039";

    /// <summary>Path to the stable checkout we manage.</summary>
    public string StableCheckoutDir { get; set; } = @"C:\Projects\agent-taskboard-devspace\agent-taskboard-stable";

    /// <summary>Path to the parent devspace folder (where update-stable.sh and start-stable.sh live).</summary>
    public string DevspaceDir { get; set; } = @"C:\Projects\agent-taskboard-devspace";

    /// <summary>Update script path (executed via bash).</summary>
    public string UpdateScript { get; set; } = "update-stable.sh";

    /// <summary>Main backend base URL.</summary>
    public string BackendUrl { get; set; } = "http://127.0.0.1:5031";

    /// <summary>X-Client-Id used when calling the main backend (must be a registered service identity).</summary>
    public string BackendClientId { get; set; } = "stable-restart-watcher";

    /// <summary>Append-only history.</summary>
    public string HistoryFile { get; set; } = @"C:\Projects\agent-taskboard-workspace\logs\stable-updates.jsonl";

    /// <summary>How often the bookkeeping ticker probes git+backend.</summary>
    public int ProbeIntervalSeconds { get; set; } = 30;

    /// <summary>How long to wait for /healthz=200 after the script restarts the backend.</summary>
    public int HealthWaitSeconds { get; set; } = 180;

    /// <summary>Token required to authorise /update/trigger (empty = open in dev). Set via env var ATP_UPDATE_TOKEN.</summary>
    public string? TriggerToken { get; set; } = null;
}
