
namespace AgentStudio.Cli;

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

    /// <summary>
    /// Upper bound on how long one <see cref="ProbeAsync"/> may take, in
    /// milliseconds. The caller enforces it as a hard deadline, so a probe whose
    /// own step timeouts can exceed this value would be killed mid-flight and
    /// report a cancellation instead of a parse result (AGT-2679). Implementations
    /// derive it from their step list rather than guessing.
    ///
    /// Defaulted so a probe that does nothing expensive (or a test double) need not
    /// declare one; the PTY-driven probes override it from their own step timeouts.
    /// </summary>
    int BudgetMs => 45_000;
}
