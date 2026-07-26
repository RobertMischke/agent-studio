using Xunit;

namespace AgentStudio.Tests;

public sealed class DispatchQueueDepthWatcherTests
{
    private const string RunnerId = "agent-runner-01";

    [Fact]
    public void Depth_below_slots_promotes_dependency_ready_backlog_work()
    {
        var ready = Task("AGT-1", TaskStates.Ready, order: 10);
        var backlog = Task("AGT-2", TaskStates.Backlog, order: 20);

        var plan = Plan([ready, backlog], slots: 2, cap: 2);

        var action = Assert.Single(plan.Actions);
        Assert.Equal("AGT-2", action.Task.Key);
        Assert.Equal(QueueDepthActionSources.Backlog, action.Source);
        Assert.Equal(1, action.DispatchableDepth);
        Assert.Equal(2, action.TargetDepth);
    }

    [Fact]
    public void Empty_backlog_falls_back_to_one_epic_decomposition()
    {
        var epic = Task("AGT-12", TaskStates.Backlog, order: 30) with
        {
            Kind = TaskKinds.Epic,
        };

        var plan = Plan([epic], slots: 2, cap: 2);

        var action = Assert.Single(plan.Actions);
        Assert.Equal("AGT-12", action.Task.Key);
        Assert.Equal(QueueDepthActionSources.Epic, action.Source);
    }

    [Fact]
    public void Blocked_backlog_also_falls_back_to_one_epic_decomposition()
    {
        var dependency = Task("AGT-10", TaskStates.Progress, order: 10);
        var blocked = Task("AGT-11", TaskStates.Backlog, order: 20) with
        {
            References = new TaskReferences { DependsOn = ["AGT-10"] },
        };
        var epic = Task("AGT-12", TaskStates.Backlog, order: 30) with
        {
            Kind = TaskKinds.Epic,
        };

        var plan = Plan([dependency, blocked, epic], slots: 2, cap: 2);

        var action = Assert.Single(plan.Actions);
        Assert.Equal("AGT-12", action.Task.Key);
        Assert.Equal(QueueDepthActionSources.Epic, action.Source);
    }

    [Fact]
    public void Sight_review_keys_flags_tags_and_website_project_are_never_auto_dispatched()
    {
        var keyed = Task("MKT-3", TaskStates.Backlog, order: 10, project: "Marketing");
        var flagged = Task("AGT-20", TaskStates.Backlog, order: 20) with
        {
            AutoDispatch = false,
        };
        var tagged = Task("AGT-21", TaskStates.Backlog, order: 30) with
        {
            Tags = ["sight-review"],
        };
        var website = Task("WEB-1", TaskStates.Backlog, order: 40, project: "Website");

        var plan = Plan(
            [keyed, flagged, tagged, website],
            slots: 4,
            cap: 4,
            projects: ["Marketing", "Agent Studio", "Website"]);

        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Interval_cap_limits_promotions_even_when_all_slots_are_empty()
    {
        var tasks = new[]
        {
            Task("AGT-30", TaskStates.Backlog, order: 10),
            Task("AGT-31", TaskStates.Backlog, order: 20),
            Task("AGT-32", TaskStates.Backlog, order: 30),
            Task("AGT-33", TaskStates.Backlog, order: 40),
        };

        var plan = Plan(tasks, slots: 4, cap: 2);

        Assert.Equal(2, plan.Actions.Count);
        Assert.Equal(["AGT-30", "AGT-31"], plan.Actions.Select(action => action.Task.Key));
    }

    [Fact]
    public void Full_host_does_not_refill_even_when_ready_is_empty()
    {
        var backlog = Task("AGT-40", TaskStates.Backlog, order: 10);

        var plan = Plan([backlog], slots: 4, cap: 2, activeSlots: 4);

        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Read_only_runner_counts_epics_but_refuses_coding_backlog()
    {
        var coding = Task("AGT-50", TaskStates.Backlog, order: 10);
        var epic = Task("AGT-51", TaskStates.Backlog, order: 20) with
        {
            Kind = TaskKinds.Epic,
        };

        var plan = Plan([coding, epic], slots: 2, cap: 2, readOnly: true);

        var action = Assert.Single(plan.Actions);
        Assert.Equal("AGT-51", action.Task.Key);
        Assert.Equal(QueueDepthActionSources.Epic, action.Source);
    }

    private static QueueDepthPlan Plan(
        IReadOnlyList<TaskInfo> tasks,
        int slots,
        int cap,
        int activeSlots = 0,
        bool readOnly = false,
        IReadOnlyList<string>? projects = null)
    {
        projects ??= ["Agent Studio"];
        var settings = projects.ToDictionary(
            project => project,
            _ => new ProjectSettings
            {
                PickupMode = PickupModes.Auto,
                ExecutionLocation = RunnerId,
                RemoteExecutionEnabled = true,
            },
            StringComparer.OrdinalIgnoreCase);
        var policy = new QueueDepthPolicy(
            cap,
            TargetDepth: null,
            QueueDepthPolicy.DefaultExcludedTaskKeys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            QueueDepthPolicy.DefaultExcludedProjects.ToHashSet(StringComparer.OrdinalIgnoreCase));
        var references = TaskReferenceIndex.Build(tasks);

        return DispatchQueueDepthPlanner.CreatePlan(
            tasks,
            references,
            settings,
            [
                new QueueDepthRunnerCapacity(
                    RunnerId,
                    RunnerId,
                    activeSlots,
                    Math.Max(0, slots - activeSlots),
                    readOnly),
            ],
            policy);
    }

    private static TaskInfo Task(
        string key,
        string state,
        int order,
        string project = "Agent Studio") =>
        new()
        {
            Id = key.ToLowerInvariant(),
            Key = key,
            TaskKey = key,
            Title = key,
            State = state,
            Order = order,
            Agent = AgentTypes.Codex,
            CreatedAt = new DateTime(2026, 7, 26, 8, 0, 0, DateTimeKind.Utc)
                .AddMinutes(order),
            ProjectName = project,
            WatchPath = "/" + project.Replace(' ', '-').ToLowerInvariant(),
            FolderPath = "/" + project.Replace(' ', '-').ToLowerInvariant() + "/" + key,
            Kind = TaskKinds.Task,
            Mode = TaskModes.Coding,
            References = new TaskReferences(),
        };
}
