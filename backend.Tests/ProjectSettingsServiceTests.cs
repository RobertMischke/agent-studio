using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class ProjectSettingsServiceTests : IDisposable
{
    private readonly string _workspace;

    public ProjectSettingsServiceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-project-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Get_UnconfiguredProject_DefaultsAutoCommitOn()
    {
        var svc = Build();

        var settings = svc.Get("new-project");

        Assert.True(settings.AutoCommit);
        Assert.Equal(AutoPushStrategies.OnCompleted, settings.AutoPushStrategy);
    }

    [Fact]
    public void Get_ProjectEntryWithoutAutoCommit_DefaultsAutoCommitOn()
    {
        File.WriteAllText(StorePath(), """
        {
          "new-project": {
            "RunnerMode": "manual"
          }
        }
        """);
        var svc = Build();

        var settings = svc.Get("new-project");

        Assert.True(settings.AutoCommit);
    }

    [Fact]
    public void Get_ExplicitFalseAutoCommit_RemainsOff()
    {
        File.WriteAllText(StorePath(), """
        {
          "runbook": {
            "AutoCommit": false
          }
        }
        """);
        var svc = Build();

        var settings = svc.Get("runbook");

        Assert.False(settings.AutoCommit);
    }

    [Fact]
    public void SetAutoCommit_ReplacesCachedValueImmediately()
    {
        var svc = Build();

        svc.SetAutoCommit("demo", false);
        Assert.False(svc.Get("demo").AutoCommit);

        svc.SetAutoCommit("demo", true);
        Assert.True(svc.Get("demo").AutoCommit);
    }

    [Fact]
    public void SetAutoCommit_PersistsExplicitFalseAcrossReload()
    {
        var svc = Build();
        svc.SetAutoCommit("runbook", false);

        var reloaded = Build();

        Assert.False(reloaded.Get("runbook").AutoCommit);
    }

    [Fact]
    public void SetAutoPushStrategy_NormalizesAndPersistsAcrossReload()
    {
        var svc = Build();

        svc.SetAutoPushStrategy("runbook", "ALWAYS-IMMEDIATE");

        var reloaded = Build();
        Assert.Equal(AutoPushStrategies.AlwaysImmediate, reloaded.Get("runbook").AutoPushStrategy);
    }

    [Fact]
    public void SetAutoPushStrategy_InvalidValueFallsBackToDefault()
    {
        var svc = Build();

        svc.SetAutoPushStrategy("runbook", "ship-it");

        Assert.Equal(AutoPushStrategies.OnCompleted, svc.Get("runbook").AutoPushStrategy);
    }

    private ProjectSettingsService Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
            })
            .Build();
        return new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
    }

    private string StorePath() => Path.Combine(_workspace, "project-settings.json");
}
