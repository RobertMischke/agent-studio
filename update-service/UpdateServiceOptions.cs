namespace AgentTaskboard.UpdateService;

/// <summary>
/// Boot-time configuration. Defaults are tuned for the local dev/stable
/// devspace layout; everything is overridable via env vars / appsettings
/// (see Program.cs Bind call).
/// </summary>
public sealed class UpdateServiceOptions
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:5039;http://[::1]:5039";
    public string StableCheckoutDir { get; set; } = @"C:\Projects\agent-taskboard-devspace\agent-taskboard-stable";
    public string DevspaceDir { get; set; } = @"C:\Projects\agent-taskboard-devspace";
    public string UpdateScript { get; set; } = "update-stable.sh";
    public string StopScript { get; set; } = "stop-stable.sh";
    public string StartScript { get; set; } = "start-stable.sh";
    public string BashPath { get; set; } = @"C:\Program Files\Git\bin\bash.exe";
    public string BackendUrl { get; set; } = "http://127.0.0.1:5031";
    public string BackendClientId { get; set; } = "stable-restart-watcher";
    public string HistoryFile { get; set; } = @"C:\Projects\agent-taskboard-workspace\logs\stable-updates.jsonl";

    /// <summary>
    /// ADR-0031: per-run folder root. Each run gets its own subdirectory
    /// containing pre/post snapshots, verification.jsonl, captured stdout/
    /// stderr from each phase, and a human-readable summary.md.
    /// </summary>
    public string RunsDirectory { get; set; } = @"C:\Projects\agent-taskboard-workspace\logs\update-service-runs";

    public string VersionFile { get; set; } = @"C:\Projects\agent-taskboard-devspace\agent-taskboard-stable\VERSION";
    public string ReleaseManifestFile { get; set; } = @"C:\Projects\agent-taskboard-devspace\agent-taskboard-stable\release-manifest.json";
    public string? LatestApprovedTag { get; set; }

    public int ProbeIntervalSeconds { get; set; } = 30;
    public int HealthWaitSeconds { get; set; } = 180;

    /// <summary>
    /// Controls whether behind-origin notifications may apply an update on
    /// their own. "manual" still allows explicit manual triggers; "scheduled"
    /// allows scheduled/API triggers to run the apply pipeline.
    /// </summary>
    public string Mode { get; set; } = "manual";

    /// <summary>
    /// ADR-0031: opt-in. When true (env: ATP_UPDATE_AUTO_ROLLBACK=1), a
    /// failed verification triggers an automatic git reset + restart +
    /// re-verify cycle. Default off so verification failures stay loud and
    /// the operator stays in control.
    /// </summary>
    public bool AutoRollback { get; set; } = false;

    /// <summary>
    /// ADR-0031: how long the FE keeps showing the completion toast for the
    /// last successful run. The wire field is `lastRunFinishedAt`; the FE
    /// computes "within the last N seconds" against it.
    /// </summary>
    public int DoneLingerSeconds { get; set; } = 60;

    public string? TriggerToken { get; set; } = null;
}
