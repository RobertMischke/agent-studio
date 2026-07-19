namespace AgentTaskboard.UpdateService;

/// <summary>
/// HTTP-level surface that the orchestrator + verifier use against the
/// main backend. Extracted as a DI seam so the integration suite can
/// point at a hosted fake backend (or substitute a scripted probe) without
/// touching <see cref="UpdateOrchestrator"/> or <see cref="UpdateVerifier"/>.
/// Every method swallows transport errors and reports them as "false" /
/// "null" / "0" — the update service must stay up even when the backend
/// is mid-restart.
/// </summary>
public interface IBackendProbe
{
    string BaseUrl { get; }
    Task<HealthzResult> ProbeHealthzAsync(CancellationToken ct = default);
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
    Task<RuntimeVersion?> ReadRuntimeVersionAsync(CancellationToken ct = default);
    Task<bool> WaitForHealthyAsync(TimeSpan timeout, CancellationToken ct = default);
    Task<Dictionary<string, string>?> ReadProjectModesAsync(CancellationToken ct = default);
    Task<bool> SetModeAsync(string projectName, string mode, string? reason = null, CancellationToken ct = default);
    Task<(int Status, string Body)> GetAsync(string path, TimeSpan timeout, CancellationToken ct = default);
    Task<(int Status, string Body)> PostJsonAsync(string path, object body, TimeSpan timeout, CancellationToken ct = default);
}
