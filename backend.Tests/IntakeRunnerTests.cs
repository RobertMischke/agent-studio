using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Outcome-fixture coverage for <see cref="IntakeRunner"/>. The pure
/// <see cref="IntakeRunner.Evaluate"/> surface is the regression boundary
/// for the typed verdict matrix promised by
/// <c>ready-orchestrator-intake-lane</c>: pass / needs-clarification /
/// duplicate-candidate / needs-split / blocked. Each outcome is pinned by
/// a fixture so a future swap from heuristic to model-based intake cannot
/// silently change the public contract that the runner pickup gate, the
/// frontend lane projection, and the activity log all key off.
///
/// <para>
/// The integration test at the bottom drives <see cref="IntakeRunner.RunForJob"/>
/// against a temp watch path so the state-transition contract is also
/// covered end-to-end: phase moves human-ready -> intake-passed on Pass
/// and human-ready -> intake-blocked on a non-Pass verdict, plus a
/// <c>lifecycle.json</c> sidecar lands next to <c>job.json</c>.
/// </para>
/// </summary>
public class IntakeRunnerTests : IDisposable
{
    private readonly string _watchPath;

    public IntakeRunnerTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "rdo-intake-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    // ---- Pure Evaluate matrix ------------------------------------------------

    [Fact]
    public void Evaluate_HealthyPrompt_Passes()
    {
        var target = new TaskInfo { Id = "alpha", Title = "Add a daily token rollup card", State = TaskStates.Ready };
        var prompt = "Add a daily token rollup section to the project header. " +
                     "Acceptance: chip shows total tokens for the last 24 hours.";

        var v = IntakeRunner.Evaluate(target, prompt, Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.Pass, v.Outcome);
    }

    [Fact]
    public void Evaluate_TooShortPrompt_NeedsClarification()
    {
        var target = new TaskInfo { Id = "thin", Title = "fix it", State = TaskStates.Ready };

        var v = IntakeRunner.Evaluate(target, "fix it", Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.NeedsClarification, v.Outcome);
        Assert.Contains("short", v.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_NearDuplicateTitle_FlagsAsDuplicateCandidate()
    {
        var target = new TaskInfo
        {
            Id = "dup-new",
            Title = "Add daily token rollup card to project header",
            State = TaskStates.Ready
        };
        var peer = new TaskInfo
        {
            Id = "dup-old",
            Title = "Add daily token rollup card project header",
            State = TaskStates.Ready
        };

        var v = IntakeRunner.Evaluate(target, "Add daily token rollup card to project header. Done when the chip is visible.", new[] { peer });

        Assert.Equal(IntakeOutcome.DuplicateCandidate, v.Outcome);
        Assert.Contains("dup-old", v.Details ?? Array.Empty<string>());
    }

    [Fact]
    public void Evaluate_OutOfScopePrompt_HardBlocks()
    {
        var target = new TaskInfo { Id = "blocked", Title = "Run parallel coding agents on the API", State = TaskStates.Ready };
        var prompt = "Spawn parallel coding agents on the API and have them race to fix the bug.";

        var v = IntakeRunner.Evaluate(target, prompt, Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.Blocked, v.Outcome);
        Assert.Contains("non-goal", v.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_PromptWithSixLevel2Headings_NeedsSplit()
    {
        var target = new TaskInfo { Id = "fan-out", Title = "Refactor the runner across many surfaces", State = TaskStates.Ready };
        var prompt = string.Join('\n', new[]
        {
            "Top-level rewrite of the runner pickup loop with several independent deliverables.",
            "## Add metric A",
            "Body for metric A so the section is not empty.",
            "## Add metric B",
            "Body B.",
            "## Add metric C",
            "Body C.",
            "## Add metric D",
            "Body D.",
            "## Add metric E",
            "Body E.",
            "## Add metric F",
            "Body F."
        });

        var v = IntakeRunner.Evaluate(target, prompt, Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.NeedsSplit, v.Outcome);
    }

    [Fact]
    public void Evaluate_BlockedTakesPriorityOverDuplicate()
    {
        // Outcome ordering is part of the contract: hard non-goal language
        // should surface even when a near-duplicate also exists, because
        // the user must clear the non-goal before the duplicate question
        // is meaningful. Pinning this so a future check reorder cannot
        // silently swap the priority.
        var target = new TaskInfo { Id = "ordering", Title = "Add worktree support to runner", State = TaskStates.Ready };
        var peer = new TaskInfo { Id = "ordering-peer", Title = "Add worktree support to runner pickup", State = TaskStates.Ready };

        var v = IntakeRunner.Evaluate(target, "Add worktree support to runner. Done when worktrees launch on pickup.", new[] { peer });

        Assert.Equal(IntakeOutcome.Blocked, v.Outcome);
    }

    // ---- RunForJob integration: phase transitions + sidecar ------------------

    [Fact]
    public void RunForJob_PassingCard_StampsIntakePassedAndWritesSidecar()
    {
        WriteJob(TaskStates.Ready, "happy", phase: LifecyclePhases.HumanReady,
            promptMd: "Add a daily token rollup card to the project header. Acceptance: chip shows totals.");

        var runner = BuildRunner();
        var verdict = runner.RunForJob("happy");

        Assert.Equal(IntakeOutcome.Pass, verdict.Outcome);

        // Phase moved to intake-passed.
        var info = BuildScanner().FindJob("happy");
        Assert.NotNull(info);
        Assert.Equal(LifecyclePhases.IntakePassed, info!.Phase);

        // lifecycle.json is next to job.json, status reflects the verdict.
        var sidecar = ReadLifecycleJson("happy");
        Assert.NotNull(sidecar);
        Assert.Equal(LifecyclePhases.IntakePassed, sidecar!.Phase);
        Assert.Null(sidecar.BlockingReason);
        Assert.Single(sidecar.IntakeChecks);
        Assert.Equal("passed", sidecar.IntakeChecks[0].Status);
    }

    [Fact]
    public void RunForJob_BlockedCard_StampsIntakeBlockedAndCarriesReason()
    {
        WriteJob(TaskStates.Ready, "blocky", phase: LifecyclePhases.HumanReady,
            promptMd: "Spawn parallel coding agents on the API and have them race to fix the bug. Acceptance: race condition resolved.");

        var runner = BuildRunner();
        var verdict = runner.RunForJob("blocky");

        Assert.Equal(IntakeOutcome.Blocked, verdict.Outcome);

        var info = BuildScanner().FindJob("blocky");
        Assert.Equal(LifecyclePhases.IntakeBlocked, info!.Phase);

        var sidecar = ReadLifecycleJson("blocky");
        Assert.NotNull(sidecar);
        Assert.Equal(LifecyclePhases.IntakeBlocked, sidecar!.Phase);
        Assert.False(string.IsNullOrWhiteSpace(sidecar.BlockingReason));
    }

    [Fact]
    public void RunForJob_NeedsClarification_StampsIntakeBlockedSoRunnerStaysOff()
    {
        // V1 maps every non-pass outcome onto intake-blocked at the phase
        // level. The verdict still carries the typed outcome and reason so
        // the chat / sidecar can render a needs-clarification UI; the
        // pickup gate just needs a single "not approved" signal.
        WriteJob(TaskStates.Ready, "thin", phase: LifecyclePhases.HumanReady, promptMd: "fix it");

        var runner = BuildRunner();
        var verdict = runner.RunForJob("thin");

        Assert.Equal(IntakeOutcome.NeedsClarification, verdict.Outcome);
        var info = BuildScanner().FindJob("thin");
        Assert.Equal(LifecyclePhases.IntakeBlocked, info!.Phase);
    }

    [Fact]
    public void RunForJob_DuplicateCandidate_PinsPeerInDetails()
    {
        WriteJob(TaskStates.Ready, "dup-old", phase: LifecyclePhases.HumanReady,
            promptMd: "Add daily token rollup card to project header. Acceptance: chip shows totals.");
        WriteJob(TaskStates.Ready, "dup-new", phase: LifecyclePhases.HumanReady,
            promptMd: "Add daily token rollup card to project header. Done when chip is visible.");

        var runner = BuildRunner();
        var verdict = runner.RunForJob("dup-new");

        Assert.Equal(IntakeOutcome.DuplicateCandidate, verdict.Outcome);
        Assert.Contains("dup-old", verdict.Details ?? Array.Empty<string>());

        var info = BuildScanner().FindJob("dup-new");
        Assert.Equal(LifecyclePhases.IntakeBlocked, info!.Phase);
    }

    [Fact]
    public void RunForJob_RejectsNonReadyJobs()
    {
        WriteJob(TaskStates.Preparation, "draft", phase: null, promptMd: "Anything goes.");

        var runner = BuildRunner();

        Assert.Throws<InvalidOperationException>(() => runner.RunForJob("draft"));
    }

    // ---- helpers -------------------------------------------------------------

    private void WriteJob(string state, string slug, string? phase, string promptMd)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var phaseField = phase is null ? "" : $",\"phase\":\"{phase}\"";
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\",\"ownerClientId\":\"default\"{phaseField}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptMd);
    }

    private LifecycleSnapshot? ReadLifecycleJson(string slug)
    {
        var path = Path.Combine(_watchPath, TaskStates.Ready, slug, "lifecycle.json");
        if (!File.Exists(path)) return null;
        var raw = File.ReadAllText(path);
        return JsonSerializer.Deserialize<LifecycleSnapshot>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private TaskScannerService BuildScanner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private IntakeRunner BuildRunner()
    {
        var scanner = BuildScanner();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(cfg, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(cfg, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        return new IntakeRunner(scanner, mutations, chatLog, NullLogger<IntakeRunner>.Instance);
    }
}
