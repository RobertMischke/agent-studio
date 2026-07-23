using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Way 3 (non-deterministic half): the epic planning/decomposition run.
/// Two halves are covered here:
///   - <see cref="EpicDecompositionParser"/> turns the agent's authored output
///     into sub-task specs (tolerant of fencing, key aliases, blank titles).
///   - <see cref="EpicSubTaskFactory"/> creates those sub-tasks under the epic
///     with <see cref="TaskInfo.EpicId"/> set, in the configured lane, and
///     round-trips them through the scanner.
/// </summary>
public class EpicDecompositionTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "test-project";

    public EpicDecompositionTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "epic-decomp-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    // ---- Parser matrix -----------------------------------------------------

    [Fact]
    public void Parse_FencedJsonObject_WithSubTasks_ReadsTitlesAndPrompts()
    {
        var output = """
            Here is the plan for the epic.

            ```json
            {
              "subTasks": [
                { "title": "Add the model", "prompt": "Create the EpicX model." },
                { "title": "Wire the endpoint", "prompt": "Expose POST /api/x." }
              ]
            }
            ```

            [[TASK_DONE]]
            """;

        var result = EpicDecompositionParser.Parse(output);

        Assert.Null(result.Error);
        Assert.True(result.HasSubTasks);
        Assert.Equal(2, result.SubTasks.Count);
        Assert.Equal("Add the model", result.SubTasks[0].Title);
        Assert.Equal("Create the EpicX model.", result.SubTasks[0].PromptMarkdown);
        Assert.Equal("Wire the endpoint", result.SubTasks[1].Title);
    }

    [Fact]
    public void Parse_AcceptsPromptMarkdownAndCliAliases()
    {
        var output = """
            ```json
            { "subTasks": [ { "title": "T", "promptMarkdown": "body", "cli": "codex", "model": "gpt-5" } ] }
            ```
            """;

        var result = EpicDecompositionParser.Parse(output);

        var spec = Assert.Single(result.SubTasks);
        Assert.Equal("T", spec.Title);
        Assert.Equal("body", spec.PromptMarkdown);
        Assert.Equal("codex", spec.CliType);
        Assert.Equal("gpt-5", spec.Model);
    }

    [Fact]
    public void Parse_ReadsGoalGraphAndVerificationPurpose()
    {
        var output = """
            ```json
            {
              "subTasks": [
                { "id": "ship", "title": "Ship", "prompt": "deliver", "purpose": "delivery", "dependsOn": [] },
                { "id": "verify", "title": "Verify", "prompt": "inspect real evidence", "purpose": "verification", "dependsOn": ["ship"] }
              ]
            }
            ```
            [[TASK_DONE]]
            """;

        var result = EpicDecompositionParser.Parse(output);

        Assert.Null(result.Error);
        Assert.Equal("ship", result.SubTasks[0].PlanId);
        Assert.Equal(GoalTaskPurposes.Verification, result.SubTasks[1].Purpose);
        Assert.Equal(new[] { "ship" }, result.SubTasks[1].DependsOn);
    }

    [Fact]
    public void Parse_RejectsCyclicGoalGraphBeforeCreatingCards()
    {
        var output = """
            ```json
            { "subTasks": [
              { "id": "a", "title": "A", "dependsOn": ["b"] },
              { "id": "b", "title": "B", "dependsOn": ["a"] }
            ] }
            ```
            """;

        var result = EpicDecompositionParser.Parse(output);

        Assert.False(result.HasSubTasks);
        Assert.Contains("cycle", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GoalPlanValidator_RejectsDuplicateUnknownAndSelfReferentialIds()
    {
        var invalidPlans = new[]
        {
            (
                Specs: (IReadOnlyList<EpicSubTaskSpec>)
                [
                    new("First", PlanId: "same"),
                    new("Second", PlanId: "same"),
                ],
                ErrorFragment: "duplicate"
            ),
            (
                Specs: (IReadOnlyList<EpicSubTaskSpec>)
                [
                    new("Only", PlanId: "only", DependsOn: ["missing"]),
                ],
                ErrorFragment: "unknown"
            ),
            (
                Specs: (IReadOnlyList<EpicSubTaskSpec>)
                [
                    new("Only", PlanId: "only", DependsOn: ["only"]),
                ],
                ErrorFragment: "itself"
            ),
        };

        foreach (var (specs, errorFragment) in invalidPlans)
        {
            var validation = EpicGoalPlanValidator.Validate(specs);

            Assert.False(validation.IsValid);
            Assert.Contains(errorFragment, validation.Error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Parse_BareFencedArray_IsAccepted()
    {
        var output = """
            ```json
            [ { "title": "Only one", "prompt": "do it" } ]
            ```
            """;

        var result = EpicDecompositionParser.Parse(output);

        var spec = Assert.Single(result.SubTasks);
        Assert.Equal("Only one", spec.Title);
    }

    [Fact]
    public void Parse_LastFencedBlockWins()
    {
        // An earlier scratch block should be overridden by the final plan.
        var output = """
            ```json
            { "subTasks": [ { "title": "scratch", "prompt": "ignore me" } ] }
            ```

            On reflection, the real plan is:

            ```json
            { "subTasks": [ { "title": "final-a", "prompt": "a" }, { "title": "final-b", "prompt": "b" } ] }
            ```
            """;

        var result = EpicDecompositionParser.Parse(output);

        Assert.Equal(2, result.SubTasks.Count);
        Assert.Equal("final-a", result.SubTasks[0].Title);
        Assert.Equal("final-b", result.SubTasks[1].Title);
    }

    [Fact]
    public void Parse_SkipsBlankTitles_KeepsValidOnes()
    {
        var output = """
            ```json
            { "subTasks": [ { "title": "  ", "prompt": "blank" }, { "title": "keep", "prompt": "ok" } ] }
            ```
            """;

        var result = EpicDecompositionParser.Parse(output);

        var spec = Assert.Single(result.SubTasks);
        Assert.Equal("keep", spec.Title);
    }

    [Fact]
    public void Parse_RawJsonWithoutFence_IsAccepted()
    {
        var output = "The plan: { \"subTasks\": [ { \"title\": \"raw\", \"prompt\": \"p\" } ] } done.";

        var result = EpicDecompositionParser.Parse(output);

        var spec = Assert.Single(result.SubTasks);
        Assert.Equal("raw", spec.Title);
    }

    [Fact]
    public void Parse_NoJson_ReturnsEmptyWithError()
    {
        var result = EpicDecompositionParser.Parse("I could not figure out a plan. [[TASK_BLOCKED:unclear]]");

        Assert.False(result.HasSubTasks);
        Assert.Empty(result.SubTasks);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Parse_EmptyOutput_ReturnsError()
    {
        Assert.False(EpicDecompositionParser.Parse((string?)null).HasSubTasks);
        Assert.False(EpicDecompositionParser.Parse("").HasSubTasks);
        Assert.False(EpicDecompositionParser.Parse((IReadOnlyList<string>?)null).HasSubTasks);
    }

    [Fact]
    public void Parse_StructurePresentButAllBlank_ReportsNoSubTasks()
    {
        var output = """
            ```json
            { "subTasks": [ { "prompt": "no title here" } ] }
            ```
            """;

        var result = EpicDecompositionParser.Parse(output);

        Assert.False(result.HasSubTasks);
        Assert.NotNull(result.Error);
    }

    // ---- Planning-run gate (kind x intent) ---------------------------------

    [Theory]
    [InlineData(TaskKinds.Epic, RunIntent.ManualStart, true)]
    [InlineData(TaskKinds.Epic, RunIntent.AutoPickup, true)]
    [InlineData(TaskKinds.Epic, RunIntent.UserContinue, false)] // steering, not re-planning
    [InlineData(TaskKinds.Task, RunIntent.ManualStart, false)]
    [InlineData(TaskKinds.Task, RunIntent.AutoPickup, false)]
    [InlineData(TaskKinds.Task, RunIntent.UserContinue, false)]
    public void IsPlanningRun_GatesOnEpicKindAndFreshStart(string kind, RunIntent intent, bool expected)
    {
        Assert.Equal(expected, EpicRunPolicy.IsPlanningRun(kind, intent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("story")]
    public void IsPlanningRun_NonEpicKind_IsNeverPlanning(string? kind)
    {
        Assert.False(EpicRunPolicy.IsPlanningRun(kind, RunIntent.ManualStart));
        Assert.False(EpicRunPolicy.IsPlanningRun(kind, RunIntent.AutoPickup));
        Assert.False(EpicRunPolicy.IsPlanningRun(kind, RunIntent.UserContinue));
    }

    // ---- Sub-task creation with epicId -------------------------------------

    [Fact]
    public void CreateSubTasks_LandsUnderEpic_WithEpicId_InBacklog()
    {
        var (scanner, mutations) = Build();
        var epic = CreateEpic("the-epic", cli: "claude", model: "claude-opus-4-7");

        var specs = new List<EpicSubTaskSpec>
        {
            new("First", "do first"),
            new("Second", "do second"),
        };

        var created = EpicSubTaskFactory.CreateSubTasks(mutations, epic, specs, TaskStates.Backlog);

        Assert.Equal(2, created.Count);
        foreach (var id in created)
        {
            var sub = scanner.FindJob(id, _watchPath);
            Assert.NotNull(sub);
            Assert.Equal("the-epic", sub!.EpicId);
            Assert.Equal(TaskStates.Backlog, sub.State);
            Assert.Equal(TaskKinds.Task, sub.Kind);
        }
    }

    [Fact]
    public void CreateSubTasks_HonoursTargetReadyLane()
    {
        var (scanner, mutations) = Build();
        var epic = CreateEpic("epic-ready");

        var created = EpicSubTaskFactory.CreateSubTasks(
            mutations, epic, new List<EpicSubTaskSpec> { new("Go", "now") }, TaskStates.Ready);

        var sub = scanner.FindJob(Assert.Single(created), _watchPath);
        Assert.Equal(TaskStates.Ready, sub!.State);
    }

    [Fact]
    public void CreateSubTasks_ClampsAutoReviewTargetToBacklog()
    {
        // ASS-693 / ASS-716 root cause: an epic decomposition that aimed
        // sub-tasks at 4-auto-review left unworked cards in the orchestrator's
        // review lane, where a sweep wiped them to 7-archive without ever
        // running them. Decomposition must never land a fresh sub-task in
        // 4-auto-review; the factory clamps the target to a safe lane.
        var (scanner, mutations) = Build();
        var epic = CreateEpic("epic-clamp");

        var created = EpicSubTaskFactory.CreateSubTasks(
            mutations, epic, new List<EpicSubTaskSpec> { new("Sub", "do it") }, TaskStates.AutoReview);

        var sub = scanner.FindJob(Assert.Single(created), _watchPath);
        Assert.Equal(TaskStates.Backlog, sub!.State);
        Assert.NotEqual(TaskStates.AutoReview, sub.State);
    }

    [Theory]
    [InlineData(TaskStates.Ready, TaskStates.Ready)]
    [InlineData(TaskStates.Backlog, TaskStates.Backlog)]
    [InlineData(TaskStates.AutoReview, TaskStates.Backlog)]
    [InlineData(TaskStates.HumanReview, TaskStates.Backlog)]
    [InlineData(TaskStates.Progress, TaskStates.Backlog)]
    [InlineData(TaskStates.Archive, TaskStates.Backlog)]
    [InlineData("", TaskStates.Backlog)]
    [InlineData(null, TaskStates.Backlog)]
    public void ClampTargetState_OnlyAllowsBacklogOrReady(string? input, string expected)
    {
        Assert.Equal(expected, EpicSubTaskFactory.ClampTargetState(input));
    }

    [Fact]
    public void CreateSubTasks_InheritsEpicCliAndModel_UnlessOverridden()
    {
        var (scanner, mutations) = Build();
        var epic = CreateEpic("epic-inherit", cli: "claude", model: "claude-opus-4-7");

        var created = EpicSubTaskFactory.CreateSubTasks(mutations, epic, new List<EpicSubTaskSpec>
        {
            new("Inherits", "p"),
            new("Overrides", "p", CliType: "codex", Model: "gpt-5"),
        }, TaskStates.Backlog);

        var inherits = scanner.FindJob(created[0], _watchPath)!;
        Assert.Equal("claude", inherits.CliType);
        Assert.Equal("claude-opus-4-7", inherits.Model);

        var overrides = scanner.FindJob(created[1], _watchPath)!;
        Assert.Equal("codex", overrides.CliType);
        Assert.Equal("gpt-5", overrides.Model);
    }

    [Fact]
    public void CreateSubTasks_SkipsBlankTitle()
    {
        var (scanner, mutations) = Build();
        var epic = CreateEpic("epic-blank");

        var created = EpicSubTaskFactory.CreateSubTasks(mutations, epic, new List<EpicSubTaskSpec>
        {
            new("   ", "blank title"),
            new("Real", "kept"),
        }, TaskStates.Backlog);

        Assert.Single(created);
        Assert.Equal("Real", scanner.FindJob(created[0], _watchPath)!.Title);
    }

    [Fact]
    public void ParseThenCreate_EndToEnd_SubTasksCarryEpicId()
    {
        var (scanner, mutations) = Build();
        var epic = CreateEpic("epic-e2e");

        var output = """
            ```json
            { "subTasks": [ { "title": "Step 1", "prompt": "one" }, { "title": "Step 2", "prompt": "two" } ] }
            ```
            [[TASK_DONE]]
            """;

        var parsed = EpicDecompositionParser.Parse(output);
        var created = EpicSubTaskFactory.CreateSubTasks(mutations, epic, parsed.SubTasks, TaskStates.Backlog);

        Assert.Equal(2, created.Count);
        Assert.All(created, id => Assert.Equal("epic-e2e", scanner.FindJob(id, _watchPath)!.EpicId));
    }

    [Fact]
    public void CreateSubTasks_GoalGraphPersistsDependenciesAndOrchestratorProvenance()
    {
        var (scanner, mutations) = Build();
        var epic = CreateEpic("goal-epic");
        var specs = new List<EpicSubTaskSpec>
        {
            new("Verify goal", "inspect submitted revision and real evidence",
                PlanId: "verify", DependsOn: new[] { "delivery" }, Purpose: GoalTaskPurposes.Verification),
            new("Implement goal", "deliver", PlanId: "delivery", Purpose: GoalTaskPurposes.Delivery),
        };

        var created = EpicSubTaskFactory.CreateSubTasks(
            mutations,
            epic,
            specs,
            TaskStates.Ready,
            TaskCreationInitiators.Orchestrator,
            "project:test-project");

        Assert.Equal(2, created.Count);
        // The authored plan intentionally puts verification first. The factory
        // must still create the delivery node before the dependent verifier.
        var delivery = scanner.FindJob(created[0], _watchPath)!;
        var verification = scanner.FindJob(created[1], _watchPath)!;
        Assert.Equal("Implement goal", delivery.Title);
        Assert.Equal("Verify goal", verification.Title);
        Assert.Equal(new[] { delivery.Key }, verification.References.DependsOn);
        Assert.Equal(TaskCreationInitiators.Orchestrator, verification.CreationProvenance?.Initiator);
        Assert.Equal(TaskCreationMethods.GoalDecomposition, verification.CreationProvenance?.Method);
        Assert.Equal(epic.Id, verification.CreationProvenance?.GoalId);
        Assert.Equal(epic.Key, verification.CreationProvenance?.GoalKey);
        Assert.Equal("project:test-project", verification.CreationProvenance?.ContextKey);
        Assert.Equal(GoalTaskPurposes.Verification, verification.CreationProvenance?.Purpose);
        Assert.NotEqual(default, verification.CreationProvenance?.CreatedAt);
    }

    // ---- harness -----------------------------------------------------------

    private TaskInfo CreateEpic(string id, string? cli = null, string? model = null)
    {
        var (scanner, mutations) = (_scanner!, _mutations!);
        mutations.CreateJob(new CreateTaskRequest
        {
            Id = id,
            Title = id,
            Kind = TaskKinds.Epic,
            WatchPath = _watchPath,
            CliType = cli,
            Model = model,
            TargetState = TaskStates.Ready,
        });
        return scanner.FindJob(id, _watchPath)!;
    }

    private TaskScannerService? _scanner;
    private TaskMutationService? _mutations;

    private (TaskScannerService scanner, TaskMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        clients.EnsureLoaded();
        var mutations = new TaskMutationService(scanner, clients, new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        machine.EnsureStateFoldersAndMigrate();
        _scanner = scanner;
        _mutations = mutations;
        return (scanner, mutations);
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
}
