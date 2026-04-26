using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Quota;

/// <summary>
/// One CLI's quota probe. Implementations spawn the CLI in a PTY in a scratch
/// folder, navigate to the right view, and parse the rendered text into a
/// uniform <see cref="QuotaSnapshot"/>. Probes must be safe to invoke
/// concurrently with other CLI activity (they run in a scratch dir).
/// </summary>
public interface IQuotaProbe
{
    string CliType { get; }
    Task<QuotaSnapshot> ProbeAsync(CancellationToken ct);
}
