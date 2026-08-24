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
        Assert.Equal(BuildProfileGateReasons.PipelineReady, d.ReasonCode);
    }

    [Theory]
    [InlineData(BuildProfileStatuses.Declared, BuildProfileGateReasons.NotValidated)]
    [InlineData(BuildProfileStatuses.Validating, BuildProfileGateReasons.Validating)]
    [InlineData(BuildProfileStatuses.ValidationFailed, BuildProfileGateReasons.ValidationFailed)]
    public void BlockedDecisions_CarryTheirStableCause(string status, string expectedCode)
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile { Status = status });
        Assert.False(d.AllowsPickup);
        Assert.Equal(expectedCode, d.ReasonCode);
    }

    [Theory]
    [InlineData(3, true, BuildProfileGateReasons.RevalidationPending)]
    [InlineData(1, true, BuildProfileGateReasons.RevalidationPending)]
    [InlineData(0, false, BuildProfileGateReasons.RevalidationGraceExhausted)]
    [InlineData(null, false, BuildProfileGateReasons.RevalidationGraceExhausted)]
    public void RevalidationPending_AllowsPickupWhileGraceRemains(
        int? runsRemaining,
        bool expectedAllows,
        string expectedCode)
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile
        {
            Status = BuildProfileStatuses.RevalidationPending,
            RevalidationRunsRemaining = runsRemaining,
        });

        Assert.Equal(expectedAllows, d.AllowsPickup);
        Assert.Equal(expectedCode, d.ReasonCode);
    }

    [Fact]
    public void MatchingRemoteVerification_OpensTheGateForADeclaredProfile()
    {
        // The AGT-2677 case: the local dry-run can never go green because the
        // Studio host has no checkout, but the project's own build/test gate
        // proves the exact same commands on the host that runs the project.
        var profile = WithRemoteVerification(
            new BuildProfile { Status = BuildProfileStatuses.Declared, BuildCmds = ["dotnet build QualityStudio.slnx"] },
            verifiedAt: new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc));

        var d = BuildProfileGate.Evaluate(profile);

        Assert.True(d.AllowsPickup);
        Assert.Equal(BuildProfileGateReasons.RemoteVerified, d.ReasonCode);
        Assert.Contains("agent-runner-01", d.Reason);
    }

    [Fact]
    public void RemoteVerificationOfDifferentCommands_DoesNotOpenTheGate()
    {
        var proven = WithRemoteVerification(
            new BuildProfile { Status = BuildProfileStatuses.Declared, BuildCmds = ["dotnet build old.slnx"] },
            verifiedAt: new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc));
        var edited = proven with { BuildCmds = ["dotnet build new.slnx"] };

        Assert.True(BuildProfileGate.AllowsAutoPickup(proven));
        Assert.False(BuildProfileGate.AllowsAutoPickup(edited));
    }

    [Fact]
    public void NewerLocalRedRun_OutranksAnOlderRemoteVerification()
    {
        var verifiedAt = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);
        var profile = WithRemoteVerification(
            new BuildProfile { Status = BuildProfileStatuses.ValidationFailed, BuildCmds = ["dotnet build"] },
            verifiedAt);

        Assert.True(BuildProfileGate.AllowsAutoPickup(profile));
        Assert.False(BuildProfileGate.AllowsAutoPickup(
            profile with { LastValidationAttemptAt = verifiedAt.AddMinutes(1) }));
        // Same instant still counts as current: the gate only demands "not older".
        Assert.True(BuildProfileGate.AllowsAutoPickup(
            profile with { LastValidationAttemptAt = verifiedAt }));
    }

    private static BuildProfile WithRemoteVerification(BuildProfile profile, DateTime verifiedAt) =>
        profile with
        {
            LastRemoteVerification = new BuildProfileRemoteVerification
            {
                VerifiedAtUtc = verifiedAt,
                VerifiedBy = "agent-runner-01",
                TaskKey = "QS-92",
                CommandFingerprint = BuildProfileCommandFingerprint.Create(profile),
            },
        };
}

public sealed class BuildProfileCommandFingerprintTests
{
    [Fact]
    public void Fingerprint_IgnoresEverythingTheDryRunCannotDisprove()
    {
        var profile = new BuildProfile { InstallCmd = "npm ci", BuildCmds = ["npm run build"] };
        var retuned = profile with
        {
            Stack = "node",
            TestCmds = ["npm test"],
            Lockfiles = ["package-lock.json"],
            PreserveGlobs = ["node_modules"],
            PoolSize = 4,
        };

        Assert.Equal(
            BuildProfileCommandFingerprint.Create(profile),
            BuildProfileCommandFingerprint.Create(retuned));
    }

    [Fact]
    public void Fingerprint_ChangesWithTheCommandsAndTheirOrder()
    {
        var a = BuildProfileCommandFingerprint.Create(
            new BuildProfile { BuildCmds = ["one", "two"] });
        var b = BuildProfileCommandFingerprint.Create(
            new BuildProfile { BuildCmds = ["two", "one"] });
        var c = BuildProfileCommandFingerprint.Create(
            new BuildProfile { InstallCmd = "one", BuildCmds = ["two"] });

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Fingerprint_CannotBeForgedByCommandTextThatLooksLikeAStepBoundary()
    {
        Assert.NotEqual(
            BuildProfileCommandFingerprint.Create(new BuildProfile { BuildCmds = ["a", "b"] }),
            BuildProfileCommandFingerprint.Create(new BuildProfile { BuildCmds = ["a\nBuild:1:b"] }));
    }
}

/// <summary>
/// AGT-2677 regression matrix for the "declared after edit" path that starved 25
/// Quality Studio cards for five days.
/// </summary>
public sealed class BuildProfileEditPolicyTests
{
    private static readonly DateTime ValidatedAt = new(2026, 8, 18, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstDeclaration_LandsInDeclared()
    {
        var declared = Declared(["dotnet build"]);

        var result = BuildProfileEditPolicy.Apply(previous: null, declared);

        Assert.Equal(BuildProfileStatuses.Declared, result.Status);
        Assert.False(BuildProfileGate.AllowsAutoPickup(result));
    }

    [Fact]
    public void EditingTheCommandsOfAProvenProfile_KeepsPickupOpenOnRevalidationGrace()
    {
        // QS-92 rewrote the review spec of an already pipeline-ready project.
        // Before AGT-2677 that reset the status to `declared` and every Ready
        // card in the project silently stopped being claimable.
        var previous = new BuildProfile
        {
            BuildCmds = ["dotnet build QualityStudio.slnx"],
            Status = BuildProfileStatuses.PipelineReady,
            LastValidatedAt = ValidatedAt,
        };

        var result = BuildProfileEditPolicy.Apply(previous, Declared(["dotnet build QualityStudio.slnx -c Release"]));

        Assert.Equal(BuildProfileStatuses.RevalidationPending, result.Status);
        Assert.Equal(BuildProfileGate.DefaultRevalidationGraceRuns, result.RevalidationRunsRemaining);
        Assert.Equal(ValidatedAt, result.LastValidatedAt);
        Assert.True(BuildProfileGate.AllowsAutoPickup(result));
    }

    [Fact]
    public void EditingMetadataOnly_DoesNotMoveTheGateAtAll()
    {
        var previous = new BuildProfile
        {
            InstallCmd = "npm ci",
            BuildCmds = ["npm run build"],
            Status = BuildProfileStatuses.PipelineReady,
            LastValidatedAt = ValidatedAt,
        };

        var result = BuildProfileEditPolicy.Apply(
            previous,
            Declared(["npm run build"]) with { InstallCmd = "npm ci", PoolSize = 3, PreserveGlobs = ["node_modules"] });

        Assert.Equal(BuildProfileStatuses.PipelineReady, result.Status);
        Assert.Null(result.RevalidationRunsRemaining);
        Assert.Equal(3, result.PoolSize);
        Assert.True(BuildProfileGate.AllowsAutoPickup(result));
    }

    [Fact]
    public void EditingAProfileThatWasNotPassingTheGate_StaysDeclared()
    {
        var previous = new BuildProfile
        {
            BuildCmds = ["dotnet build"],
            Status = BuildProfileStatuses.ValidationFailed,
            LastValidationError = "build exited 1",
        };

        var result = BuildProfileEditPolicy.Apply(previous, Declared(["dotnet build -c Release"]));

        Assert.Equal(BuildProfileStatuses.Declared, result.Status);
        Assert.False(BuildProfileGate.AllowsAutoPickup(result));
    }

    [Fact]
    public void EditingDuringTheGraceWindow_RefillsIt_ButEachRunStillSpendsOne()
    {
        var inGrace = new BuildProfile
        {
            BuildCmds = ["a"],
            Status = BuildProfileStatuses.RevalidationPending,
            RevalidationRunsRemaining = 1,
        };

        var result = BuildProfileEditPolicy.Apply(inGrace, Declared(["b"]));

        Assert.Equal(BuildProfileGate.DefaultRevalidationGraceRuns, result.RevalidationRunsRemaining);
    }

    [Fact]
    public void ConsumeRevalidationRun_CountsDownToAClosedGate()
    {
        BuildProfile? profile = new()
        {
            BuildCmds = ["dotnet build"],
            Status = BuildProfileStatuses.RevalidationPending,
            RevalidationRunsRemaining = 2,
        };

        profile = BuildProfileEditPolicy.ConsumeRevalidationRun(profile);
        Assert.Equal(1, profile!.RevalidationRunsRemaining);
        Assert.True(BuildProfileGate.AllowsAutoPickup(profile));

        profile = BuildProfileEditPolicy.ConsumeRevalidationRun(profile);
        Assert.Equal(0, profile!.RevalidationRunsRemaining);
        Assert.False(BuildProfileGate.AllowsAutoPickup(profile));

        // Bottoming out is idempotent; it never goes negative.
        profile = BuildProfileEditPolicy.ConsumeRevalidationRun(profile);
        Assert.Equal(0, profile!.RevalidationRunsRemaining);
    }

    [Theory]
    [InlineData(BuildProfileStatuses.PipelineReady)]
    [InlineData(BuildProfileStatuses.Declared)]
    public void ConsumeRevalidationRun_IsANoOpOutsideTheGraceWindow(string status)
    {
        var profile = new BuildProfile { Status = status, RevalidationRunsRemaining = 2 };

        Assert.Same(profile, BuildProfileEditPolicy.ConsumeRevalidationRun(profile));
        Assert.Null(BuildProfileEditPolicy.ConsumeRevalidationRun(null));
    }

    private static BuildProfile Declared(string[] buildCmds) => new()
    {
        BuildCmds = buildCmds,
        Status = BuildProfileStatuses.Declared,
    };
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
    public async Task EveryRun_StampsTheAttemptInstant_SoRemoteEvidenceCanBeOrdered()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("runbook", new BuildProfile { BuildCmds = ["npm run build"] });
        var before = DateTime.UtcNow;
        var svc = BuildValidator(settings, new FakeRunner(_ => 1));

        await svc.ValidateAsync("runbook", _workspace, CancellationToken.None);

        var attemptedAt = settings.Get("runbook").BuildProfile!.LastValidationAttemptAt;
        Assert.NotNull(attemptedAt);
        Assert.InRange(attemptedAt!.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task ALocalVerdict_DropsAnyLeftoverRevalidationGrace()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("runbook", new BuildProfile { BuildCmds = ["npm run build"] });
        settings.MarkBuildProfileValidated("runbook");
        settings.SetBuildProfile("runbook", new BuildProfile { BuildCmds = ["npm run build -- --prod"] });
        Assert.Equal(
            BuildProfileGate.DefaultRevalidationGraceRuns,
            settings.Get("runbook").BuildProfile!.RevalidationRunsRemaining);

        await BuildValidator(settings, new FakeRunner(_ => 1))
            .ValidateAsync("runbook", _workspace, CancellationToken.None);

        var profile = settings.Get("runbook").BuildProfile!;
        Assert.Equal(BuildProfileStatuses.ValidationFailed, profile.Status);
        Assert.Null(profile.RevalidationRunsRemaining);
        Assert.False(BuildProfileGate.AllowsAutoPickup(profile));
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

/// <summary>
/// The persisted half of the AGT-2677 rules: what a re-declaration does to the
/// stored profile, and how a green remote gate is recorded.
/// </summary>
public sealed class BuildProfileSettingsPersistenceTests : IDisposable
{
    private readonly string _workspace;

    public BuildProfileSettingsPersistenceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-bp-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ReDeclaringAProvenProfile_KeepsThePickupGateOpen()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("quality-studio", new BuildProfile { BuildCmds = ["dotnet build QualityStudio.slnx"] });
        settings.MarkBuildProfileValidated("quality-studio");

        settings.SetBuildProfile("quality-studio", new BuildProfile { BuildCmds = ["dotnet build QualityStudio.slnx -c Release"] });

        var profile = settings.Get("quality-studio").BuildProfile!;
        Assert.Equal(BuildProfileStatuses.RevalidationPending, profile.Status);
        Assert.True(BuildProfileGate.AllowsAutoPickup(profile));
    }

    [Fact]
    public void ARequestBodyCannotAssertItsOwnGreenStatus()
    {
        var settings = BuildSettings();

        settings.SetBuildProfile("demo", new BuildProfile
        {
            BuildCmds = ["dotnet build"],
            Status = BuildProfileStatuses.PipelineReady,
            LastValidatedAt = DateTime.UtcNow,
        });

        Assert.Equal(BuildProfileStatuses.Declared, settings.Get("demo").BuildProfile!.Status);
        Assert.False(BuildProfileGate.AllowsAutoPickup(settings.Get("demo").BuildProfile));
    }

    [Fact]
    public void AGreenRemoteGate_ReopensAGateTheLocalDryRunCouldNeverOpen()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("quality-studio", new BuildProfile { BuildCmds = ["dotnet build QualityStudio.slnx"] });
        Assert.False(BuildProfileGate.AllowsAutoPickup(settings.Get("quality-studio").BuildProfile));

        settings.MarkBuildProfileRemotelyVerified("quality-studio", "agent-runner-01", "QS-92");

        var profile = settings.Get("quality-studio").BuildProfile!;
        Assert.True(BuildProfileGate.AllowsAutoPickup(profile));
        Assert.Equal("agent-runner-01", profile.LastRemoteVerification!.VerifiedBy);
        Assert.Equal("QS-92", profile.LastRemoteVerification.TaskKey);
    }

    [Fact]
    public void ARemoteProof_DoesNotSurviveACommandEdit()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("quality-studio", new BuildProfile { BuildCmds = ["dotnet build a.slnx"] });
        settings.MarkBuildProfileRemotelyVerified("quality-studio", "agent-runner-01", "QS-92");

        settings.SetBuildProfile("quality-studio", new BuildProfile { BuildCmds = ["dotnet build b.slnx"] });

        var profile = settings.Get("quality-studio").BuildProfile!;
        // The proof itself survives for display, but it no longer describes the
        // declared commands and stops opening the gate on its own.
        Assert.NotNull(profile.LastRemoteVerification);
        Assert.False(BuildProfileGate.HasCurrentRemoteVerification(profile));
        // It was valid evidence at edit time, so the edit earns the grace.
        Assert.Equal(BuildProfileStatuses.RevalidationPending, profile.Status);

        // Once the grace runs out the gate closes: nothing proves the new commands.
        for (var run = 0; run < BuildProfileGate.DefaultRevalidationGraceRuns; run++)
            settings.ConsumeBuildProfileRevalidationRun("quality-studio");
        Assert.False(BuildProfileGate.AllowsAutoPickup(settings.Get("quality-studio").BuildProfile));
    }

    [Fact]
    public void MarkingARemoteVerification_IsANoOpWithoutADeclaredProfile()
    {
        var settings = BuildSettings();

        settings.MarkBuildProfileRemotelyVerified("demo", "agent-runner-01", "AGT-1");

        Assert.Null(settings.Get("demo").BuildProfile);
    }

    [Fact]
    public void ConsumingGrace_IsPersistedAndReportsWhatIsLeft()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("demo", new BuildProfile { BuildCmds = ["a"] });
        settings.MarkBuildProfileValidated("demo");
        settings.SetBuildProfile("demo", new BuildProfile { BuildCmds = ["b"] });

        Assert.Equal(2, settings.ConsumeBuildProfileRevalidationRun("demo"));
        Assert.Equal(1, settings.ConsumeBuildProfileRevalidationRun("demo"));
        Assert.Equal(0, settings.ConsumeBuildProfileRevalidationRun("demo"));
        Assert.Null(settings.ConsumeBuildProfileRevalidationRun("demo"));
        Assert.False(BuildProfileGate.AllowsAutoPickup(settings.Get("demo").BuildProfile));
    }

    [Fact]
    public void ConsumingGrace_IsANoOpForAProjectWithoutAProfile()
    {
        var settings = BuildSettings();

        Assert.Null(settings.ConsumeBuildProfileRevalidationRun("demo"));
    }

    private ProjectSettingsService BuildSettings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        return new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
    }
}
