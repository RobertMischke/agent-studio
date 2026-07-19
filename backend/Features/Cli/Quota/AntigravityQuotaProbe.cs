

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Cli;

public sealed class AntigravityQuotaProbe : QuotaProbeBase
{
    public AntigravityQuotaProbe(
        ILogger<AntigravityQuotaProbe> logger,
        CliRouter router,
        CliEnvironment env)
        : base(logger, router, env) { }

    public override string CliType => CliTypes.Gemini;

    public override Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
    {
        // Antigravity / agentapi does not expose a local quota command line interface.
        // Return a snapshot with no windows, and a descriptive error/status message.
        return Task.FromResult(new QuotaSnapshot
        {
            CliType = CliType,
            Source = "agentapi",
            Plan = "Antigravity Pro",
            Windows = new List<QuotaWindow>(),
            Error = "Antigravity quota information is managed by the IDE session."
        });
    }
}
