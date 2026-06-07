using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Services.Quota;
using OrchestratorApi.Services.Pty;
using Xunit;

namespace OrchestratorApi.Tests;

public class AntigravityQuotaProbeTests
{
    [Fact]
    public async Task ProbeAsync_ReturnsPlaceholderSnapshot()
    {
        var ptyEnv = new CopilotCliEnvironment(NullLogger<CopilotCliEnvironment>.Instance);
        var probe = new AntigravityQuotaProbe(
            NullLogger<AntigravityQuotaProbe>.Instance,
            null!,
            ptyEnv);

        var snap = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal("gemini", snap.CliType);
        Assert.Equal("agentapi", snap.Source);
        Assert.Equal("Antigravity Pro", snap.Plan);
        Assert.Empty(snap.Windows);
        Assert.Contains("managed by the IDE", snap.Error);
    }
}
