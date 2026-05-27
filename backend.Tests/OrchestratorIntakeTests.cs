using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the orchestrator-intake contract for the
/// <c>ready-orchestrator-intake-lane</c> task:
///
/// - the pure-function <see cref="IntakeRunner.Evaluate"/> returns the right
///   typed outcome for each fixture (pass / clarification / duplicate / split / blocked);
/// - <see cref="IntakeRunner.RunForJob"/> writes the matching phase on disk
///   (<c>intake-passed</c> on Pass, <c>intake-blocked</c> otherwise) and
///   stamps a <c>lifecycle.json</c> sidecar with the verdict;
/// - the runner pickup gate (<see cref="ProjectRunner.IsPickupAllowed"/>)
///   honors the per-project <c>IntakeEnabled</c> setting: pickup proceeds
///   regardless of phase when the gate is off, and blocks anything that
///   isn't <c>intake-passed</c> when the gate is on.
/// </summary>
public class OrchestratorIntakeTests : IDisposable
{
    private readonly string _watchPath;

    public OrchestratorIntakeTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "intake-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in JobStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    // ---- Pure outcome matrix -------------------------------------------------

    [Fact]
    public void Evaluate_HealthyPrompt_Passes()
    {
        var verdict = IntakeRunner.Evaluate(
            new JobInfo { Id = "good", Title = "Add login button" },
            "Add a login button to the header. Done when the button navigates to /login and a Playwright spec covers the click.",
            existingPeers: Array.Empty<JobInfo>());

        Assert.Equal(IntakeOutcome.Pass, verdict.Outcome);
    }

    [Fact]
    public void Evaluate_TooShort_NeedsClarification()
    {
        var verdict = IntakeRunner.Evaluate(
            new JobInfo { Id = "thin", Title = "fix" },
            "fix it",
            existingPeers: Array.Empty<JobInfo>());

        Assert.Equal(IntakeOutcome.NeedsClarification, verdict.Outcome);
        Assert.Contains("short", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_NearDuplicateTitle_FlagsDuplicate()
    {
        var peers = new List<JobInfo>
        {
            new() { Id = "older-twin", Title = "add login button to header", State = JobStates.Ready }
        };
        var verdict = IntakeRunner.Evaluate(
            new JobInfo { Id = "newer-twin", Title = "Add login button to header" },
            "Add a login button to the header. Done when it navigates to /login.",
            peers);

        Assert.Equal(IntakeOutcome.DuplicateCandidate, verdict.Outcome);
        Assert.Contains("older-twin", verdict.Reason);
    }

    [Fact]
    public void Evaluate_OutOfScopePrompt_Blocks()
    {
        var verdict = IntakeRunner.Evaluate(
            new JobInfo { Id = "scope", Title = "spawn parallel agents" },
            "Please run multiple agents at once on this repo so we can finish faster. Done when all branches merge cleanly.",
            existingPeers: Array.Empty<JobInfo>());

        Assert.Equal(IntakeOutcome.Blocked, verdict.Outcome);
    }

    [Fact]
    public void Evaluate_ManyTopLevelHeadings_NeedsSplit()
    {
        var prompt = string.Join("\n", new[]
        {
            "## Section A", "task A details with enough length to clear clarity",
            "## Section B", "task B details with enough length to clear clarity",
            "## Section C", "task C details with enough length to clear clarity",
            "## Section D", "task D details with enough length to clear clarity",
            "## Section E", "task E details with enough length to clear clarity",
            "## Section F", "task F details with enough length to clear clarity",
        });
        var verdict = IntakeRunner.Evaluate(
            new JobInfo { Id = "bundle", Title = "do many things" },
            prompt,
            existingPeers: Array.Empty<JobInfo>());

        Assert.Equal(IntakeOutcome.NeedsSplit, verdict.Outcome);
    }

    // ---- Phase transitions on disk ------------------------------------------

    [Fact]
    public void RunForJob_PassingPrompt_WritesIntakePassedPhase()
    {
        WriteJob(JobStates.Ready, "good-card", "Add login button",
            "Add a login button to the header. Done when the button navigates to /login and a Playwright spec covers the click.");
        var (intake, scanner) = BuildIntake();

        var verdict = intake.RunForJob("good-card", _watchPath);

        Assert.Equal(IntakeOutcome.Pass, verdict.Outcome);

        var info = scanner.FindJob("good-card");
        Assert.NotNull(info);
        Assert.Equal(LifecyclePhases.IntakePassed, info!.Phase);

        var sidecar = ReadLifecycleSidecar(info.FolderPath);
        Assert.NotNull(sidecar);
        Assert.Equal(LifecyclePhases.IntakePassed, sidecar!.Phase);
        Assert.Single(sidecar.IntakeChecks);
        Assert.Equal("passed", sidecar.IntakeChecks[0].Status);
    }

    [Fact]
    public void RunForJob_NeedsClarification_WritesIntakeBlocked()
    {
        WriteJob(JobStates.Ready, "thin-card", "fix", "fix it");
        var (intake, scanner) = BuildIntake();

        var verdict = intake.RunForJob("thin-card", _watchPath);

        Assert.Equal(IntakeOutcome.NeedsClarification, verdict.Outcome);

        var info = scanner.FindJob("thin-card");
        Assert.NotNull(info);
        Assert.Equal(LifecyclePhases.IntakeBlocked, info!.Phase);

        var sidecar = ReadLifecycleSidecar(info.FolderPath);
        Assert.NotNull(sidecar);
        Assert.Equal(LifecyclePhases.IntakeBlocked, sidecar!.Phase);
        Assert.Equal("failed", sidecar.IntakeChecks[0].Status);
        Assert.NotNull(sidecar.BlockingReason);
    }

    [Fact]
    public void RunForJob_DuplicateCandidate_WritesIntakeBlockedAndChatNote()
    {
        WriteJob(JobStates.Ready, "older-twin", "add login button to header",
            "Add a login button to the header. Done when /login renders.");
        WriteJob(JobStates.Ready, "newer-twin", "Add login button to header",
            "Add a login button to the header. Done when /login navigates correctly.");
        var (intake, scanner) = BuildIntake();

        var verdict = intake.RunForJob("newer-twin", _watchPath);

        Assert.Equal(IntakeOutcome.DuplicateCandidate, verdict.Outcome);

        var info = scanner.FindJob("newer-twin");
        Assert.NotNull(info);
        Assert.Equal(LifecyclePhases.IntakeBlocked, info!.Phase);

        // The chat log captures the [intake:*] tag so the activity-log view
        // surfaces intake as its own actor without parsing prose.
        var cliLog = File.ReadAllText(Path.Combine(info.FolderPath, "logs", "cli-output.log"));
        Assert.Contains("[intake:", cliLog);
        Assert.Contains("duplicatecandidate", cliLog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunForJob_OnlyAcceptsReadyState()
    {
        WriteJob(JobStates.Progress, "in-flight", "anything",
            "A perfectly normal prompt with a fully-formed acceptance line included.");
        var (intake, _) = BuildIntake();

        Assert.Throws<InvalidOperationException>(
            () => intake.RunForJob("in-flight", _watchPath));
    }

    // ---- Pickup gate ---------------------------------------------------------

    [Fact]
    public void PickupGate_Disabled_AllowsPickupRegardlessOfPhase()
    {
        var humanReady = new JobInfo { State = JobStates.Ready, Phase = LifecyclePhases.HumanReady };
        var blocked = new JobInfo { State = JobStates.Ready, Phase = LifecyclePhases.IntakeBlocked };

        Assert.True(ProjectRunner.IsPickupAllowed(humanReady, intakeEnabled: false));
        Assert.True(ProjectRunner.IsPickupAllowed(blocked, intakeEnabled: false));
    }

    [Fact]
    public void PickupGate_Enabled_OnlyAllowsIntakePassed()
    {
        var humanReady = new JobInfo { State = JobStates.Ready, Phase = LifecyclePhases.HumanReady };
        var running = new JobInfo { State = JobStates.Ready, Phase = LifecyclePhases.IntakeRunning };
        var blocked = new JobInfo { State = JobStates.Ready, Phase = LifecyclePhases.IntakeBlocked };
        var passed = new JobInfo { State = JobStates.Ready, Phase = LifecyclePhases.IntakePassed };

        Assert.False(ProjectRunner.IsPickupAllowed(humanReady, intakeEnabled: true));
        Assert.False(ProjectRunner.IsPickupAllowed(running, intakeEnabled: true));
        Assert.False(ProjectRunner.IsPickupAllowed(blocked, intakeEnabled: true));
        Assert.True(ProjectRunner.IsPickupAllowed(passed, intakeEnabled: true));
    }

    // ---- helpers -------------------------------------------------------------

    private (IntakeRunner intake, JobScannerService scanner) BuildIntake()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var mutations = new JobMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), NullLogger<JobMutationService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var time = new FakeTimeProvider(new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc));
        var intake = new IntakeRunner(scanner, mutations, chatLog,
            NullLogger<IntakeRunner>.Instance, bus: null, time: time);
        return (intake, scanner);
    }

    private void WriteJob(string state, string slug, string title, string promptBody)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = slug,
                ["title"] = title,
                ["state"] = state,
                ["order"] = 1,
                ["agent"] = "claude",
                ["ownerClientId"] = "default",
            }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
    }

    private static LifecycleSnapshot? ReadLifecycleSidecar(string jobFolder)
    {
        var path = Path.Combine(jobFolder, "lifecycle.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<LifecycleSnapshot>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
