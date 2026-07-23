using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class BuildProfileGateTests
{
    [Fact]
    public void NullProfile_AllowsPickup_LegacyBehaviour()
    {
        var d = BuildProfileGate.Evaluate(null);
        Assert.True(d.AllowsPickup);
    }

    [Fact]
    public void DeclaredButUnvalidated_BlocksPickup()
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile { Status = BuildProfileStatuses.Declared });
        Assert.False(d.AllowsPickup);
    }

    [Fact]
    public void NullStatusOnProfile_TreatedAsDeclared_BlocksPickup()
    {
        // A hand-written profile with a missing/unknown status must NOT be treated
        // as pipeline-ready; it normalizes to declared and stays blocked.
        var d = BuildProfileGate.Evaluate(new BuildProfile { Status = "" });
        Assert.False(d.AllowsPickup);
    }

    [Fact]
    public void Validating_BlocksPickup()
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile { Status = BuildProfileStatuses.Validating });
        Assert.False(d.AllowsPickup);
    }

    [Fact]
    public void ValidationFailed_BlocksPickup_AndCarriesReason()
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile
        {
            Status = BuildProfileStatuses.ValidationFailed,
            LastValidationError = "build exited 1",
        });
        Assert.False(d.AllowsPickup);
        Assert.Contains("build exited 1", d.Reason);
    }

    [Fact]
    public void PipelineReady_AllowsPickup()
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile { Status = BuildProfileStatuses.PipelineReady });
        Assert.True(d.AllowsPickup);
    }
}

public sealed class BuildProfileDryRunPlannerTests
{
    [Fact]
    public void Plan_Null_IsEmpty()
    {
        Assert.Empty(BuildProfileDryRunPlanner.Plan(null));
    }

    [Fact]
    public void Plan_InstallFirstThenBuildsInOrder()
    {
        var plan = BuildProfileDryRunPlanner.Plan(new BuildProfile
        {
            InstallCmd = "npm ci",
            BuildCmds = ["npm run build", "dotnet build"],
        });

        Assert.Equal(3, plan.Count);
        Assert.Equal(DryRunStepKind.Install, plan[0].Kind);
        Assert.Equal("npm ci", plan[0].Command);
        Assert.Equal(DryRunStepKind.Build, plan[1].Kind);
        Assert.Equal("npm run build", plan[1].Command);
        Assert.Equal("dotnet build", plan[2].Command);
    }

    [Fact]
    public void Plan_OmitsBlankInstall()
    {
        var plan = BuildProfileDryRunPlanner.Plan(new BuildProfile { BuildCmds = ["npm run build"] });
        Assert.Single(plan);
        Assert.Equal(DryRunStepKind.Build, plan[0].Kind);
    }

    [Fact]
    public void Plan_DoesNotIncludeTestCommands()
    {
        var plan = BuildProfileDryRunPlanner.Plan(new BuildProfile
        {
            InstallCmd = "npm ci",
            TestCmds = ["npm test"],
        });
        Assert.Single(plan);
        Assert.Equal(DryRunStepKind.Install, plan[0].Kind);
    }
}

public sealed class BuildProfileValidationServiceTests : IDisposable
{
    private readonly string _workspace;

    public BuildProfileValidationServiceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-bp-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task GreenRun_FlipsProfileToPipelineReady()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci", BuildCmds = ["npm run build"] });
        var svc = BuildValidator(settings, new FakeRunner(_ => 0));

        var result = await svc.ValidateAsync("runbook", _workspace, CancellationToken.None);

        Assert.True(result.Green);
        Assert.Equal(BuildProfileStatuses.PipelineReady, settings.Get("runbook").BuildProfile!.Status);
        Assert.True(BuildProfileGate.AllowsAutoPickup(settings.Get("runbook").BuildProfile));
    }

    [Fact]
    public async Task RedRun_FlipsToValidationFailed_AndStopsAtFirstFailure()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci", BuildCmds = ["npm run build"] });
        var ran = new List<string>();
        var runner = new FakeRunner(cmd => { ran.Add(cmd); return cmd == "npm ci" ? 1 : 0; });
        var svc = BuildValidator(settings, runner);

        var result = await svc.ValidateAsync("runbook", _workspace, CancellationToken.None);

        Assert.False(result.Green);
        Assert.Equal("npm ci", result.FailedCommand);
        Assert.Equal(["npm ci"], ran); // build never ran after install failed
        var p = settings.Get("runbook").BuildProfile!;
        Assert.Equal(BuildProfileStatuses.ValidationFailed, p.Status);
        Assert.False(BuildProfileGate.AllowsAutoPickup(p));
    }

    [Fact]
    public async Task NoCommands_IsTriviallyGreen()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("runbook", new BuildProfile { Stack = "docs-only" });
        var svc = BuildValidator(settings, new FakeRunner(_ => throw new Xunit.Sdk.XunitException("runner must not be invoked")));

        var result = await svc.ValidateAsync("runbook", _workspace, CancellationToken.None);

        Assert.True(result.Green);
        Assert.Equal(BuildProfileStatuses.PipelineReady, settings.Get("runbook").BuildProfile!.Status);
    }

    [Fact]
    public async Task NoProfile_ReturnsNotGreen_WithoutTouchingState()
    {
        var settings = BuildSettings();
        var svc = BuildValidator(settings, new FakeRunner(_ => 0));

        var result = await svc.ValidateAsync("runbook", _workspace, CancellationToken.None);

        Assert.False(result.Green);
        Assert.Null(settings.Get("runbook").BuildProfile);
    }

    [Fact]
    public void DotnetValidationCommand_OnWindows_UsesCmdWithoutBash()
    {
        var psi = ProcessBuildCommandRunner.CreateStartInfo(
            _workspace,
            "dotnet build AgentStudio.sln",
            isWindows: true);

        Assert.Equal("cmd.exe", psi.FileName);
        Assert.Equal(["/c", "dotnet build AgentStudio.sln"], psi.ArgumentList.ToArray());
        Assert.DoesNotContain("bash", psi.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationEndpoint_OnWindows_UsesProjectWorkingDirectoryNotTaskStorage()
    {
        var entry = new WatchPathEntry
        {
            Path = @"C:\Projects\agent-taskboard-workspace\projects\agent-taskboard",
            RootPath = @"C:\Projects\agent-taskboard-devspace\agent-taskboard-dev",
            RepositoryPath = @"C:\Projects\agent-taskboard-devspace\agent-taskboard",
        };

        var workingDirectory = ProjectSettingsEndpoints.ResolveBuildProfileWorkingDirectory(entry);

        Assert.Equal(entry.RootPath, workingDirectory);
        Assert.NotEqual(entry.Path, workingDirectory);
    }

    [Fact]
    public void ValidationEndpoint_FallsBackToRepositoryPathWhenRootPathIsMissing()
    {
        var entry = new WatchPathEntry
        {
            Path = @"C:\Projects\agent-taskboard-workspace\projects\agent-taskboard",
            RepositoryPath = @"C:\Projects\agent-taskboard-devspace\agent-taskboard",
        };

        var workingDirectory = ProjectSettingsEndpoints.ResolveBuildProfileWorkingDirectory(entry);

        Assert.Equal(entry.RepositoryPath, workingDirectory);
    }

    [Fact]
    public void ValidationEndpoint_DoesNotUseTaskStorageAsWorkingDirectory()
    {
        var entry = new WatchPathEntry
        {
            Path = @"C:\Projects\agent-taskboard-workspace\projects\agent-taskboard",
        };

        var workingDirectory = ProjectSettingsEndpoints.ResolveBuildProfileWorkingDirectory(entry);

        Assert.Null(workingDirectory);
    }

    [Fact]
    public async Task DotnetProfile_UsesHostShell_ValidatesGreenAndAllowsPickup()
    {
        File.WriteAllText(Path.Combine(_workspace, "Validation.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var settings = BuildSettings();
        settings.SetBuildProfile("runbook", new BuildProfile
        {
            Stack = "dotnet",
            BuildCmds = ["dotnet build Validation.csproj --nologo -p:NuGetAudit=false"],
        });
        var svc = BuildValidator(settings, new ProcessBuildCommandRunner());

        var result = await svc.ValidateAsync("runbook", _workspace, CancellationToken.None);

        Assert.True(result.Green, result.Summary);
        Assert.Equal(BuildProfileStatuses.PipelineReady, settings.Get("runbook").BuildProfile!.Status);
        Assert.True(BuildProfileGate.AllowsAutoPickup(settings.Get("runbook").BuildProfile));

        var reloadedProfile = BuildSettings().Get("runbook").BuildProfile;
        Assert.Equal(BuildProfileStatuses.PipelineReady, reloadedProfile!.Status);
        Assert.True(BuildProfileGate.AllowsAutoPickup(reloadedProfile));
    }

    private ProjectSettingsService BuildSettings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        return new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
    }

    private static BuildProfileValidationService BuildValidator(ProjectSettingsService settings, IBuildCommandRunner runner) =>
        new(settings, runner, NullLogger<BuildProfileValidationService>.Instance);

    private sealed class FakeRunner : IBuildCommandRunner
    {
        private readonly Func<string, int> _exitFor;
        public FakeRunner(Func<string, int> exitFor) => _exitFor = exitFor;
        public Task<BuildCommandResult> RunAsync(string workingDir, string command, CancellationToken ct) =>
            Task.FromResult(new BuildCommandResult(_exitFor(command), $"output of {command}"));
    }
}
