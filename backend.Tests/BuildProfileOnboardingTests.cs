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

    [Theory]
    [InlineData(null, BuildProfileGateCodes.NoProfile)]
    [InlineData(BuildProfileStatuses.Declared, BuildProfileGateCodes.NotValidated)]
    [InlineData(BuildProfileStatuses.Validating, BuildProfileGateCodes.Validating)]
    [InlineData(BuildProfileStatuses.ValidationFailed, BuildProfileGateCodes.ValidationFailed)]
    [InlineData(BuildProfileStatuses.PipelineReady, BuildProfileGateCodes.PipelineReady)]
    public void EveryOutcomeCarriesItsStableCode(string? status, string expectedCode)
    {
        // The code travels into the dispatch rejection and the banner, so each
        // status must map to exactly one stable token (AGT-2677).
        var profile = status is null ? null : new BuildProfile { Status = status };
        Assert.Equal(expectedCode, BuildProfileGate.Evaluate(profile).Code);
    }

    [Fact]
    public void RevalidationGrace_KeepsPickupOpenAndSaysWhy()
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile
        {
            Status = BuildProfileStatuses.PipelineReady,
            RevalidationPending = true,
            RevalidationRunsRemaining = 2,
        });

        Assert.True(d.AllowsPickup);
        Assert.Equal(BuildProfileGateCodes.RevalidationPending, d.Code);
        Assert.Contains("2 grace pickup", d.Reason);
    }

    [Fact]
    public void RevalidationGraceExhausted_BlocksPickup()
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile
        {
            Status = BuildProfileStatuses.PipelineReady,
            RevalidationPending = true,
            RevalidationRunsRemaining = 0,
        });

        Assert.False(d.AllowsPickup);
        Assert.Equal(BuildProfileGateCodes.RevalidationExhausted, d.Code);
    }
}

public sealed class BuildProfileEditPolicyTests
{
    private static BuildProfile Proven(string? install = "npm ci", params string[] builds) => new()
    {
        InstallCmd = install,
        BuildCmds = builds.Length == 0 ? ["npm run build"] : builds,
        Status = BuildProfileStatuses.PipelineReady,
        LastValidatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void NoPreviousProfile_LandsDeclared()
    {
        var result = BuildProfileEditPolicy.Apply(null, new BuildProfile { InstallCmd = "npm ci" });

        Assert.Equal(BuildProfileStatuses.Declared, result.Status);
        Assert.False(result.RevalidationPending);
        Assert.False(BuildProfileGate.AllowsAutoPickup(result));
    }

    [Theory]
    [InlineData(BuildProfileStatuses.Declared)]
    [InlineData(BuildProfileStatuses.Validating)]
    [InlineData(BuildProfileStatuses.ValidationFailed)]
    public void PreviousGateAlreadyClosed_LandsDeclaredWithoutGrace(string previousStatus)
    {
        var previous = Proven() with { Status = previousStatus };

        var result = BuildProfileEditPolicy.Apply(previous, new BuildProfile { InstallCmd = "pnpm i" });

        Assert.Equal(BuildProfileStatuses.Declared, result.Status);
        Assert.False(result.RevalidationPending);
        Assert.Null(result.LastValidatedAt);
    }

    [Fact]
    public void ProvenProfile_EditOutsideTheDryRunMaterial_KeepsValidationUntouched()
    {
        // The dry-run only runs install + build. Changing the test commands or the
        // pool size cannot invalidate what it proved, so the gate must not move.
        var previous = Proven();

        var result = BuildProfileEditPolicy.Apply(previous, new BuildProfile
        {
            InstallCmd = previous.InstallCmd,
            BuildCmds = previous.BuildCmds,
            TestCmds = ["npm test -- --coverage"],
            PoolSize = 4,
        });

        Assert.Equal(BuildProfileStatuses.PipelineReady, result.Status);
        Assert.False(result.RevalidationPending);
        Assert.Equal(previous.LastValidatedAt, result.LastValidatedAt);
        Assert.True(BuildProfileGate.AllowsAutoPickup(result));
    }

    [Fact]
    public void ProvenProfile_EditOfTheDryRunMaterial_OpensABoundedGrace()
    {
        // The QS-92 regression: the review-spec rewrite must not close the gate.
        var previous = Proven();

        var result = BuildProfileEditPolicy.Apply(previous, new BuildProfile
        {
            InstallCmd = previous.InstallCmd,
            BuildCmds = ["dotnet build QualityStudio.slnx"],
        });

        Assert.Equal(BuildProfileStatuses.PipelineReady, result.Status);
        Assert.True(result.RevalidationPending);
        Assert.Equal(BuildProfileEditPolicy.DefaultGraceRuns, result.RevalidationRunsRemaining);
        Assert.True(BuildProfileGate.AllowsAutoPickup(result));
    }

    [Fact]
    public void OperatorSuppliedStatusFields_AreIgnored()
    {
        // Status is server-owned; a client cannot declare itself pipeline-ready.
        var result = BuildProfileEditPolicy.Apply(null, new BuildProfile
        {
            InstallCmd = "npm ci",
            Status = BuildProfileStatuses.PipelineReady,
            LastValidatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            LastRemoteVerifiedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(BuildProfileStatuses.Declared, result.Status);
        Assert.Null(result.LastValidatedAt);
        Assert.Null(result.LastRemoteVerifiedAt);
    }

    [Theory]
    [InlineData("npm ci", "npm ci", false)]
    [InlineData("npm ci", "pnpm install", true)]
    [InlineData(null, "npm ci", true)]
    public void DryRunMaterialChanged_TracksInstallCommand(string? before, string? after, bool changed)
    {
        var previous = Proven(install: before);
        var edited = new BuildProfile { InstallCmd = after, BuildCmds = previous.BuildCmds };

        Assert.Equal(changed, BuildProfileEditPolicy.DryRunMaterialChanged(previous, edited));
    }

    [Fact]
    public void DryRunMaterialChanged_TracksBuildCommandOrder()
    {
        var previous = Proven(builds: ["a", "b"]);
        var reordered = new BuildProfile { InstallCmd = previous.InstallCmd, BuildCmds = ["b", "a"] };

        Assert.True(BuildProfileEditPolicy.DryRunMaterialChanged(previous, reordered));
    }
}

public sealed class BuildProfileValidationWorkspaceTests
{
    [Fact]
    public void PrefersTheRepositoryCheckoutOverTheTaskWatchPath()
    {
        // The outage's second half: the dry-run ran in the watch path, which holds
        // task folders and no sources, so it could never go green (AGT-2677).
        var entry = new WatchPathEntry
        {
            Name = "QualityStudio",
            Path = "/workspace/projects/QualityStudio/tasks",
            RootPath = "/src/quality-studio",
            RepositoryPath = "/src/quality-studio/repo",
        };

        Assert.Equal("/src/quality-studio/repo", BuildProfileValidationWorkspace.Resolve(entry));
    }

    [Fact]
    public void FallsBackToTheWorkspaceRootThenTheWatchPath()
    {
        var rootOnly = new WatchPathEntry { Path = "/tasks", RootPath = "/src", RepositoryPath = "" };
        var taskOnly = new WatchPathEntry { Path = "/tasks", RootPath = "", RepositoryPath = "" };

        Assert.Equal("/src", BuildProfileValidationWorkspace.Resolve(rootOnly));
        Assert.Equal("/tasks", BuildProfileValidationWorkspace.Resolve(taskOnly));
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
