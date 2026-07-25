using AgentStudio.Cli;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CliQuotaWaitPolicyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quota-wait-policy-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;

    public CliQuotaWaitPolicyServiceTests()
    {
        Directory.CreateDirectory(_root);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
    }

    [Fact]
    public void GlobalPolicy_PersistsAndClampsTheThreshold()
    {
        var service = NewService();

        var saved = service.SetGlobal(true, 999);
        var reloaded = NewService().GetGlobal();

        Assert.True(saved.Enabled);
        Assert.Equal(CliQuotaWaitPolicyService.MaxThresholdMinutes, saved.ThresholdMinutes);
        Assert.Equal(saved, reloaded);
    }

    [Fact]
    public void Resolve_UsesIndependentProjectOverridesOverGlobalDefaults()
    {
        NewService().SetGlobal(true, 45);
        var project = new ProjectSettings
        {
            WaitOnQuotaEnabled = false,
            WaitOnQuotaThresholdMinutes = null,
        };

        var resolved = NewService().Resolve(project);

        Assert.False(resolved.Enabled);
        Assert.Equal(45, resolved.ThresholdMinutes);
        Assert.Equal("project", resolved.Source);
        Assert.True(resolved.GlobalEnabled);
    }

    private CliQuotaWaitPolicyService NewService()
        => new(NullLogger<CliQuotaWaitPolicyService>.Instance, _config);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
