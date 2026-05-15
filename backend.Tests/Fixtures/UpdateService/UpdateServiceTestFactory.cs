extern alias UpdSvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace OrchestratorApi.Tests.Fixtures.UpdateService;

/// <summary>
/// WebApplicationFactory wrapper around the standalone Update Service so the
/// integration suite can boot the full 9-phase pipeline in-process. The
/// factory wires the real <see cref="UpdSvc::AgentTaskboard.UpdateService.UpdateOrchestrator"/>
/// + <see cref="UpdSvc::AgentTaskboard.UpdateService.UpdateVerifier"/> against
/// a temp <see cref="FakeStableCheckout"/> and the loopback
/// <see cref="FakeBackendHarness"/>. ADR-0031 follow-up.
/// </summary>
public sealed class UpdateServiceTestFactory : WebApplicationFactory<UpdSvc::Program>
{
    private readonly FakeStableCheckout _checkout;
    private readonly FakeBackendHarness _backend;
    private readonly bool _autoRollback;
    private readonly int _doneLingerSeconds;
    private readonly int _healthWaitSeconds;

    public UpdateServiceTestFactory(
        FakeStableCheckout checkout,
        FakeBackendHarness backend,
        bool autoRollback,
        int doneLingerSeconds = 2,
        int healthWaitSeconds = 10)
    {
        _checkout = checkout;
        _backend = backend;
        _autoRollback = autoRollback;
        _doneLingerSeconds = doneLingerSeconds;
        _healthWaitSeconds = healthWaitSeconds;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Bind to dynamic loopback for the in-process TestServer.
                // WebApplicationFactory swaps in TestServer regardless, but
                // we don't want UseUrls to point at the real 5039.
                ["UpdateService:ListenUrl"]          = "http://127.0.0.1:0",
                ["UpdateService:StableCheckoutDir"]  = _checkout.StableDir,
                ["UpdateService:DevspaceDir"]        = _checkout.DevspaceDir,
                ["UpdateService:UpdateScript"]       = "update-stable.sh",
                ["UpdateService:StopScript"]         = "stop-stable.sh",
                ["UpdateService:StartScript"]        = "start-stable.sh",
                ["UpdateService:BashPath"]           = _checkout.BashPath,
                ["UpdateService:BackendUrl"]         = _backend.BaseUrl,
                ["UpdateService:HistoryFile"]        = _checkout.HistoryFile,
                ["UpdateService:RunsDirectory"]      = _checkout.RunsDir,
                ["UpdateService:VersionFile"]        = _checkout.VersionFile,
                ["UpdateService:HealthWaitSeconds"]  = _healthWaitSeconds.ToString(),
                ["UpdateService:DoneLingerSeconds"]  = _doneLingerSeconds.ToString(),
                ["UpdateService:ProbeIntervalSeconds"] = "5",
                ["UpdateService:AutoRollback"]       = _autoRollback ? "true" : "false",
            });
        });

        builder.UseEnvironment("Testing");
        return base.CreateHost(builder);
    }
}
