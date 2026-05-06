namespace AgentTaskboard.UpdateService;

/// <summary>
/// Boot-time configuration. Defaults are tuned for the local dev/stable
/// devspace layout; everything is overridable via env vars / appsettings
/// (see Program.cs Bind call).
/// </summary>
public sealed class UpdateServiceOptions
{
    /// <summary>
    /// Listening URL(s) — semicolon-separated for ASP.NET Core. Binds both
    /// IPv4 loopback and IPv6 loopback so the FE reaches us via either
    /// `localhost` (often resolves to ::1) or `127.0.0.1`. CORS is wide
    /// open inside the process so cross-origin (4011 → 5039) just works.
    /// </summary>
    public string ListenUrl { get; set; } = "http://127.0.0.1:5039;http://[::1]:5039";

    /// <summary>Path to the stable checkout we manage.</summary>
    public string StableCheckoutDir { get; set; } = @"C:\Projects\agent-taskboard-devspace\agent-taskboard-stable";

    /// <summary>Path to the parent devspace folder (where update-stable.sh and start-stable.sh live).</summary>
    public string DevspaceDir { get; set; } = @"C:\Projects\agent-taskboard-devspace";

    /// <summary>Update script path (executed via bash).</summary>
    public string UpdateScript { get; set; } = "update-stable.sh";

    /// <summary>
    /// Absolute path to a POSIX bash binary. Default points at Git for
    /// Windows so we don't accidentally hit WSL's `bash.exe` launcher,
    /// which resolves `/bin/bash` against the Linux filesystem and fails.
    /// </summary>
    public string BashPath { get; set; } = @"C:\Program Files\Git\bin\bash.exe";

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
