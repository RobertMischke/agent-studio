using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
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
        foreach (var state in TaskStates.All)
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
            new TaskInfo { Id = "good", Title = "Add login button" },
            "Add a login button to the header. Done when the button navigates to /login and a Playwright spec covers the click.",
            existingPeers: Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.Pass, verdict.Outcome);
    }

    [Fact]
    public void Evaluate_TooShort_NeedsClarification()
    {
        var verdict = IntakeRunner.Evaluate(
            new TaskInfo { Id = "thin", Title = "fix" },
            "fix it",
            existingPeers: Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.NeedsClarification, verdict.Outcome);
        Assert.Contains("short", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_NearDuplicateTitle_FlagsDuplicate()
    {
        var peers = new List<TaskInfo>
        {
            new() { Id = "older-twin", Title = "add login button to header", State = TaskStates.Ready }
        };
        var verdict = IntakeRunner.Evaluate(
            new TaskInfo { Id = "newer-twin", Title = "Add login button to header" },
            "Add a login button to the header. Done when it navigates to /login.",
            peers);

        Assert.Equal(IntakeOutcome.DuplicateCandidate, verdict.Outcome);
        Assert.Contains("older-twin", verdict.Reason);
    }

    [Fact]
    public void Evaluate_ParallelPrompt_NoLongerBlocked()
    {
        // ADR-0052 reversed the intra-project-parallelism non-goal, so a prompt
        // about running multiple agents / merging branches is no longer hard-
        // blocked at intake. Regression guard for the reversal.
        var verdict = IntakeRunner.Evaluate(
            new TaskInfo { Id = "scope", Title = "spawn parallel agents" },
            "Please run multiple agents at once on this repo so we can finish faster. Done when all branches merge cleanly.",
            existingPeers: Array.Empty<TaskInfo>());

        Assert.NotEqual(IntakeOutcome.Blocked, verdict.Outcome);
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
            new TaskInfo { Id = "bundle", Title = "do many things" },
            prompt,
            existingPeers: Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.NeedsSplit, verdict.Outcome);
    }

    // ---- Phase transitions on disk ------------------------------------------

    [Fact]
    public void RunForJob_PassingPrompt_WritesIntakePassedPhase()
    {
        WriteJob(TaskStates.Ready, "good-card", "Add login button",
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
        WriteJob(TaskStates.Ready, "thin-card", "fix", "fix it");
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
        WriteJob(TaskStates.Ready, "older-twin", "add login button to header",
            "Add a login button to the header. Done when /login renders.");
        WriteJob(TaskStates.Ready, "newer-twin", "Add login button to header",
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
        WriteJob(TaskStates.Progress, "in-flight", "anything",
            "A perfectly normal prompt with a fully-formed acceptance line included.");
        var (intake, _) = BuildIntake();

        Assert.Throws<InvalidOperationException>(
            () => intake.RunForJob("in-flight", _watchPath));
    }

    // ---- Pickup gate ---------------------------------------------------------

    [Fact]
    public void PickupGate_Disabled_AllowsPickupRegardlessOfPhase()
    {
        var humanReady = new TaskInfo { State = TaskStates.Ready, Phase = LifecyclePhases.HumanReady };
        var blocked = new TaskInfo { State = TaskStates.Ready, Phase = LifecyclePhases.IntakeBlocked };

        Assert.True(ProjectRunner.IsPickupAllowed(humanReady, intakeEnabled: false));
        Assert.True(ProjectRunner.IsPickupAllowed(blocked, intakeEnabled: false));
    }

    [Fact]
    public void PickupGate_Enabled_OnlyAllowsIntakePassed()
    {
        var humanReady = new TaskInfo { State = TaskStates.Ready, Phase = LifecyclePhases.HumanReady };
        var running = new TaskInfo { State = TaskStates.Ready, Phase = LifecyclePhases.IntakeRunning };
        var blocked = new TaskInfo { State = TaskStates.Ready, Phase = LifecyclePhases.IntakeBlocked };
        var passed = new TaskInfo { State = TaskStates.Ready, Phase = LifecyclePhases.IntakePassed };

        Assert.False(ProjectRunner.IsPickupAllowed(humanReady, intakeEnabled: true));
        Assert.False(ProjectRunner.IsPickupAllowed(running, intakeEnabled: true));
        Assert.False(ProjectRunner.IsPickupAllowed(blocked, intakeEnabled: true));
        Assert.True(ProjectRunner.IsPickupAllowed(passed, intakeEnabled: true));
    }

    // ---- Parallel prep: drain several awaiting cards per tick ----------------

    [Fact]
    public void SelectCandidates_ReturnsAwaitingCardsOldestFirst_SkipsStamped()
    {
        var jobs = new List<TaskInfo>
        {
            new() { Id = "b", State = TaskStates.Ready, Phase = LifecyclePhases.HumanReady, WatchPath = _watchPath, Order = 2 },
            new() { Id = "a", State = TaskStates.Ready, Phase = null,                        WatchPath = _watchPath, Order = 1 },
            new() { Id = "passed", State = TaskStates.Ready, Phase = LifecyclePhases.IntakePassed,  WatchPath = _watchPath, Order = 3 },
            new() { Id = "blocked", State = TaskStates.Ready, Phase = LifecyclePhases.IntakeBlocked, WatchPath = _watchPath, Order = 4 },
            new() { Id = "running", State = TaskStates.Ready, Phase = LifecyclePhases.IntakeRunning, WatchPath = _watchPath, Order = 5 },
            new() { Id = "elsewhere", State = TaskStates.Ready, Phase = LifecyclePhases.HumanReady, WatchPath = "/other", Order = 0 },
            new() { Id = "not-ready", State = TaskStates.Progress, Phase = LifecyclePhases.HumanReady, WatchPath = _watchPath, Order = 0 },
        };

        var picked = IntakeHostedService.SelectCandidates(jobs, _watchPath, cap: 16);

        // Only the two awaiting cards for this project, oldest (Order) first;
        // stamped / other-project / non-ready cards are excluded.
        Assert.Equal(new[] { "a", "b" }, picked.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void SelectCandidates_HonorsCap()
    {
        var jobs = Enumerable.Range(0, 10)
            .Select(i => new TaskInfo { Id = $"j{i}", State = TaskStates.Ready, Phase = LifecyclePhases.HumanReady, WatchPath = _watchPath, Order = i })
            .ToList();

        var picked = IntakeHostedService.SelectCandidates(jobs, _watchPath, cap: 3);

        Assert.Equal(3, picked.Count);
        Assert.Equal(new[] { "j0", "j1", "j2" }, picked.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void ShouldAutoRunIntake_RequiresEnabledAndNonManualAutonomy()
    {
        Assert.False(IntakeHostedService.ShouldAutoRunIntake(new ProjectSettings()));
        Assert.True(IntakeHostedService.ShouldAutoRunIntake(new ProjectSettings
        {
            IntakeEnabled = true,
            AutonomyLevel = null
        }));
        Assert.False(IntakeHostedService.ShouldAutoRunIntake(new ProjectSettings
        {
            IntakeEnabled = true,
            AutonomyLevel = 0
        }));
        Assert.True(IntakeHostedService.ShouldAutoRunIntake(new ProjectSettings
        {
            IntakeEnabled = true,
            AutonomyLevel = 1
        }));
    }

    [Fact]
    public void Intake_DrainsEveryAwaitingCardInOneTick()
    {
        // The parallel-prep contract: several awaiting cards are all stamped in
        // a single pass, none left in human-ready. Intake holds no code seat, so
        // there is no single-active-run gate as with 3-progress.
        WriteJob(TaskStates.Ready, "card-1", "Add login button",
            "Add a login button to the header. Done when it navigates to /login.");
        WriteJob(TaskStates.Ready, "card-2", "Add logout button",
            "Add a logout button to the header. Done when it clears the session.");
        WriteJob(TaskStates.Ready, "card-3", "fix", "fix it"); // will land intake-blocked
        var (intake, scanner) = BuildIntake();

        var picked = IntakeHostedService.SelectCandidates(
            scanner.ScanAllJobs(), _watchPath, IntakeHostedService.MaxIntakePerProjectPerTick);
        Assert.Equal(3, picked.Count);
        foreach (var c in picked)
            intake.RunForJob(c.Id, _watchPath);

        foreach (var slug in new[] { "card-1", "card-2", "card-3" })
        {
            var info = scanner.FindJob(slug);
            Assert.NotNull(info);
            Assert.NotEqual(LifecyclePhases.HumanReady, info!.Phase);
            Assert.True(
                info.Phase is LifecyclePhases.IntakePassed or LifecyclePhases.IntakeBlocked,
                $"{slug} ended in unexpected phase {info.Phase}");
        }
    }

    // ---- Done-precheck routing (requirement 5) -------------------------------

    [Fact]
    public void RouteAlreadyDone_AlreadyDoneCard_RoutesToHumanReviewThroughFunnel()
    {
        // A card whose prompt declares the work already done is not executed:
        // it is routed to 5-human-review through the HumanReviewEscalation funnel
        // (HumanDecisionNeeded) so a person confirms-and-completes. The
        // orchestrator never auto-moves to 6-completed.
        WriteJob(TaskStates.Ready, "done-card", "Add the rollup card",
            "This rollup card is already implemented on main and merged. No work needed.");
        var (intake, scanner) = BuildIntake();
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        // workspaceRoot=null: the decision-journal append is skipped (no
        // TaskRepository configured for this temp watch path) but the move +
        // status.md stub still run, which is what this test pins. The sync
        // Escalate path never touches the transition service, so null! is safe.
        var escalation = new HumanReviewEscalation(
            states, null!, workspaceRoot: null, NullLogger<HumanReviewEscalation>.Instance);

        var verdict = intake.RunForJob("done-card", _watchPath);
        Assert.Equal(IntakeOutcome.AlreadyDone, verdict.Outcome);

        IntakeHostedService.RouteAlreadyDone(
            escalation, "done-card", _watchPath, "test", verdict.Reason,
            NullLogger<IntakeHostedService>.Instance);

        // Folder physically moved out of 2-ready into 5-human-review, and the
        // escalation wrote a status.md stub so the card is explainable.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "done-card")),
            "the already-done card must leave 2-ready");
        var parked = Path.Combine(_watchPath, TaskStates.HumanReview, "done-card");
        Assert.True(Directory.Exists(parked), "the already-done card must land in 5-human-review");
        Assert.True(File.Exists(Path.Combine(parked, "status.md")), "a status.md stub must be written");
    }

    // ---- helpers -------------------------------------------------------------

    private (IntakeRunner intake, TaskScannerService scanner) BuildIntake()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
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
        File.WriteAllText(Path.Combine(dir, "task.json"),
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
