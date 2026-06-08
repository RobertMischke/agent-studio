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

    [Fact]
    public void Get_UnconfiguredProject_DefaultsParallelismKnobs()
    {
        var svc = Build();

        var settings = svc.Get("new-project");

        Assert.Equal(1, settings.MaxParallelism);
        Assert.Equal("develop", settings.IntegrationBranch);
        Assert.Equal(IntegrationStrategies.DirectMerge, settings.IntegrationStrategy);
    }

    [Fact]
    public void SetMaxParallelism_PersistsAcrossReload()
    {
        var svc = Build();

        svc.SetMaxParallelism("runbook", 4);

        var reloaded = Build();
        Assert.Equal(4, reloaded.Get("runbook").MaxParallelism);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void SetMaxParallelism_ClampsBelowOneToOne(int value)
    {
        var svc = Build();

        svc.SetMaxParallelism("runbook", value);

        Assert.Equal(1, svc.Get("runbook").MaxParallelism);
    }

    [Fact]
    public void SetIntegrationBranch_PersistsAcrossReload()
    {
        var svc = Build();

        svc.SetIntegrationBranch("runbook", "integration");

        var reloaded = Build();
        Assert.Equal("integration", reloaded.Get("runbook").IntegrationBranch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SetIntegrationBranch_BlankRevertsToDefault(string? value)
    {
        var svc = Build();
        svc.SetIntegrationBranch("runbook", "integration");

        svc.SetIntegrationBranch("runbook", value);

        Assert.Equal("develop", svc.Get("runbook").IntegrationBranch);
    }

    [Fact]
    public void SetIntegrationStrategy_NormalizesAndPersistsAcrossReload()
    {
        var svc = Build();

        svc.SetIntegrationStrategy("runbook", "PULL-REQUEST");

        var reloaded = Build();
        Assert.Equal(IntegrationStrategies.PullRequest, reloaded.Get("runbook").IntegrationStrategy);
    }

    [Fact]
    public void SetIntegrationStrategy_InvalidValueFallsBackToDefault()
    {
        var svc = Build();

        svc.SetIntegrationStrategy("runbook", "rebase-and-pray");

        Assert.Equal(IntegrationStrategies.DirectMerge, svc.Get("runbook").IntegrationStrategy);
    }

    [Fact]
    public void ResolveCliMode_UnconfiguredProject_DefaultsToYolo()
    {
        var svc = Build();

        var r = svc.ResolveCliMode("new-project", CliTypes.Claude);

        Assert.Equal(CliPermissionModes.Yolo, r.Mode);
        Assert.Equal(CliPermissionSources.Default, r.Source);
        Assert.Equal(["--dangerously-skip-permissions"], r.Args);
    }

    [Fact]
    public void SetCliMode_OverridePersistsAcrossReloadAndIsSourcedToProject()
    {
        var svc = Build();

        svc.SetCliMode("runbook", CliTypes.Codex, CliPermissionModes.ReadOnly);

        var reloaded = Build();
        var r = reloaded.ResolveCliMode("runbook", CliTypes.Codex);
        Assert.Equal(CliPermissionModes.ReadOnly, r.Mode);
        Assert.Equal(CliPermissionSources.Project, r.Source);
        Assert.Equal(["--sandbox", "read-only"], r.Args);
    }

    [Fact]
    public void SetCliMode_BlankClearsOverrideRevertingToDefault()
    {
        var svc = Build();
        svc.SetCliMode("runbook", CliTypes.Gemini, CliPermissionModes.WorkspaceWrite);
        Assert.Equal(CliPermissionSources.Project, svc.ResolveCliMode("runbook", CliTypes.Gemini).Source);

        svc.SetCliMode("runbook", CliTypes.Gemini, null);

        var r = svc.ResolveCliMode("runbook", CliTypes.Gemini);
        Assert.Equal(CliPermissionModes.Yolo, r.Mode);
        Assert.Equal(CliPermissionSources.Default, r.Source);
        Assert.Null(svc.Get("runbook").CliModes);
    }

    [Fact]
    public void SetCliMode_UnknownCli_IsIgnored()
    {
        var svc = Build();

        svc.SetCliMode("runbook", "not-a-cli", CliPermissionModes.ReadOnly);

        Assert.Null(svc.Get("runbook").CliModes);
    }

    [Fact]
    public void SetCliMode_OnlyOverridesTheNamedCli_OthersStayDefault()
    {
        var svc = Build();

        svc.SetCliMode("runbook", CliTypes.Claude, CliPermissionModes.ReadOnly);

        Assert.Equal(CliPermissionSources.Project, svc.ResolveCliMode("runbook", CliTypes.Claude).Source);
        // Gemini/Copilot have no global-config probe, so an un-overridden CLI is
        // always the platform default here regardless of the host's ~/.codex.
        Assert.Equal(CliPermissionSources.Default, svc.ResolveCliMode("runbook", CliTypes.Gemini).Source);
        Assert.Equal(CliPermissionModes.Yolo, svc.ResolveCliMode("runbook", CliTypes.Copilot).Mode);
    }

    [Fact]
    public void Get_UnconfiguredProject_HasNoBuildProfile()
    {
        var svc = Build();

        Assert.Null(svc.Get("new-project").BuildProfile);
    }

    [Fact]
    public void SetBuildProfile_NormalizesAndStartsDeclared_PersistsAcrossReload()
    {
        var svc = Build();

        svc.SetBuildProfile("runbook", new BuildProfile
        {
            Stack = "  node  ",
            InstallCmd = "  npm ci  ",
            BuildCmds = ["npm run build", "  ", ""],
            Lockfiles = ["package-lock.json", "  "],
            PreserveGlobs = ["node_modules"],
            PoolSize = 0,
            Status = BuildProfileStatuses.PipelineReady, // must be forced back to declared
        });

        var reloaded = Build().Get("runbook").BuildProfile;
        Assert.NotNull(reloaded);
        Assert.Equal("node", reloaded!.Stack);
        Assert.Equal("npm ci", reloaded.InstallCmd);
        Assert.Equal(["npm run build"], reloaded.BuildCmds);
        Assert.Equal(["package-lock.json"], reloaded.Lockfiles);
        Assert.Null(reloaded.PoolSize); // non-positive clamps to null
        Assert.Equal(BuildProfileStatuses.Declared, reloaded.Status);
    }

    [Fact]
    public void MarkBuildProfileValidated_FlipsToPipelineReadyAndStampsTime()
    {
        var svc = Build();
        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci" });

        svc.MarkBuildProfileValidated("runbook");

        var p = svc.Get("runbook").BuildProfile;
        Assert.Equal(BuildProfileStatuses.PipelineReady, p!.Status);
        Assert.NotNull(p.LastValidatedAt);
        Assert.Null(p.LastValidationError);
    }

    [Fact]
    public void MarkBuildProfileValidationFailed_RecordsReason()
    {
        var svc = Build();
        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci" });

        svc.MarkBuildProfileValidationFailed("runbook", "install exited 1");

        var p = svc.Get("runbook").BuildProfile;
        Assert.Equal(BuildProfileStatuses.ValidationFailed, p!.Status);
        Assert.Equal("install exited 1", p.LastValidationError);
    }

    [Fact]
    public void ReDeclaringBuildProfile_ResetsAnyPriorGreenValidation()
    {
        var svc = Build();
        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci" });
        svc.MarkBuildProfileValidated("runbook");
        Assert.Equal(BuildProfileStatuses.PipelineReady, svc.Get("runbook").BuildProfile!.Status);

        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm install" });

        Assert.Equal(BuildProfileStatuses.Declared, svc.Get("runbook").BuildProfile!.Status);
    }

    [Fact]
    public void SetBuildProfileNull_ClearsProfile()
    {
        var svc = Build();
        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci" });

        svc.SetBuildProfile("runbook", null);

        Assert.Null(svc.Get("runbook").BuildProfile);
    }

    [Fact]
    public void MarkValidating_OnProjectWithoutProfile_IsNoOp()
    {
        var svc = Build();

        svc.MarkBuildProfileValidated("new-project");

        Assert.Null(svc.Get("new-project").BuildProfile);
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
