namespace AgentStudio.Runner;

/// <summary>
/// Keeps the claimable Remote Ready queue deep enough to use the host slots
/// that runners report. It promotes bounded, dependency-ready Backlog work
/// first and admits at most one undecomposed Epic when ordinary Backlog work is
/// exhausted or blocked.
/// </summary>
public sealed class DispatchQueueDepthWatcher : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly ProjectSettingsService _settings;
    private readonly ClientIdentityStore _clients;
    private readonly AccessSecurityStore _security;
    private readonly TimelineLog _timeline;
    private readonly OrchestratorLog _orchestratorLog;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DispatchQueueDepthWatcher> _logger;

    public DispatchQueueDepthWatcher(
        TaskScannerService scanner,
        TaskStateMachine states,
        ProjectSettingsService settings,
        ClientIdentityStore clients,
        AccessSecurityStore security,
        TimelineLog timeline,
        OrchestratorLog orchestratorLog,
        IConfiguration configuration,
        ILogger<DispatchQueueDepthWatcher> logger)
    {
        _scanner = scanner;
        _states = states;
        _settings = settings;
        _clients = clients;
        _security = security;
        _timeline = timeline;
        _orchestratorLog = orchestratorLog;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(1,
                    _configuration.GetValue("Runner:QueueDepthWatcher:InitialDelaySeconds", 15))),
                stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Test-subject hosts must remain structurally incapable of
                // automatic board advancement, matching TaskRunnerService.
                if (RunnerRoles.ResolveFromConfig(_configuration) != RunnerRole.TestSubject
                    && _configuration.GetValue("Runner:QueueDepthWatcher:Enabled", true))
                    TickOnce();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Queue-depth watcher tick failed");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(5,
                        _configuration.GetValue("Runner:QueueDepthWatcher:TickSeconds", 30))),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs one bounded reconciliation pass. Public for deterministic tests and diagnostics.</summary>
    public int TickOnce()
    {
        var cap = Math.Clamp(
            _configuration.GetValue("Runner:QueueDepthWatcher:MaxActionsPerInterval", 2),
            0,
            100);
        if (cap == 0) return 0;

        var snapshot = _scanner.GetLiveSnapshotWithReferenceIndex();
        var policy = QueueDepthPolicy.FromConfiguration(_configuration, cap);
        var capacities = ReadFreshRunnerCapacities();
        var settingsByProject = snapshot.Live
            .Select(task => task.ProjectName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(name => name, _settings.Get, StringComparer.OrdinalIgnoreCase);
        var plan = DispatchQueueDepthPlanner.CreatePlan(
            snapshot.Live,
            snapshot.References,
            settingsByProject,
            capacities,
            policy);

        var moved = 0;
        foreach (var action in plan.Actions)
        {
            var reason =
                $"Queue depth {action.DispatchableDepth}/{action.TargetDepth} for " +
                $"{action.RunnerName}; in-flight {action.InFlight}/{action.HostSlots}; " +
                $"source {action.Source}; interval cap {policy.MaxActionsPerInterval}.";
            var outcome = _states.MoveJob(
                action.Task.Id,
                TaskStates.Ready,
                action.Task.WatchPath,
                cause: TimelineActors.Orchestrator,
                expectedSourceState: TaskStates.Backlog,
                reason: reason);
            if (outcome.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "queue-depth-auto-dispatch-refused project={Project} task={TaskKey} source={Source} status={Status} detail={Detail}",
                    action.Task.ProjectName,
                    action.Task.Key ?? action.Task.Id,
                    action.Source,
                    outcome.Status,
                    outcome.Message);
                continue;
            }

            var folder = outcome.NewFolderPath ?? action.Task.FolderPath;
            _timeline.Append(
                folder,
                TimelineEventKinds.AutoDispatchQueued,
                TimelineActors.Orchestrator,
                action.Source == QueueDepthActionSources.Epic
                    ? "Queue-depth watcher queued Epic decomposition"
                    : "Queue-depth watcher promoted Backlog work to Ready",
                details: new()
                {
                    ["source"] = action.Source,
                    ["runner"] = action.RunnerName,
                    ["dispatchableDepth"] = action.DispatchableDepth.ToString(),
                    ["targetDepth"] = action.TargetDepth.ToString(),
                    ["inFlight"] = action.InFlight.ToString(),
                    ["hostSlots"] = action.HostSlots.ToString(),
                    ["intervalCap"] = policy.MaxActionsPerInterval.ToString(),
                    ["reason"] = reason,
                });
            _orchestratorLog.Append(action.Task.WatchPath, new OrchestratorLogEntry
            {
                Kind = OrchestratorLogKinds.Action,
                Topic = OrchestratorLogTopics.QueueDepth,
                JobId = action.Task.Id,
                Summary = action.Source == QueueDepthActionSources.Epic
                    ? $"Queued Epic {action.Task.Key ?? action.Task.Id} for decomposition"
                    : $"Promoted {action.Task.Key ?? action.Task.Id} from Backlog to Ready",
                Reasoning = reason,
            });
            _logger.LogInformation(
                "queue-depth-auto-dispatch project={Project} task={TaskKey} source={Source} runner={Runner} depth={Depth}/{Target} inFlight={InFlight}/{Slots} intervalCap={Cap}",
                action.Task.ProjectName,
                action.Task.Key ?? action.Task.Id,
                action.Source,
                action.RunnerName,
                action.DispatchableDepth,
                action.TargetDepth,
                action.InFlight,
                action.HostSlots,
                policy.MaxActionsPerInterval);
            moved++;
        }

        return moved;
    }

    private IReadOnlyList<QueueDepthRunnerCapacity> ReadFreshRunnerCapacities()
    {
        var now = DateTime.UtcNow;
        var staleAfter = TimeSpan.FromSeconds(Math.Clamp(
            _configuration.GetValue("Runner:QueueDepthWatcher:RunnerStaleSeconds", 120),
            30,
            900));
        var clients = _clients.ListAll();
        var result = new List<QueueDepthRunnerCapacity>();

        ClientIdentity? MatchingClient(string id, string name) =>
            clients.FirstOrDefault(client =>
                string.Equals(client.Id, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(client.Id, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(client.DisplayName, name, StringComparison.OrdinalIgnoreCase));

        foreach (var runner in _security.ListRunners())
        {
            if (runner.RevokedAt is not null
                || runner.DrainRequestedAt is not null
                || runner.RetiredAt is not null
                || runner.LastSeenAt is null
                || now - runner.LastSeenAt.Value > staleAfter)
                continue;

            var client = MatchingClient(runner.Id, runner.Name);
            result.Add(new QueueDepthRunnerCapacity(
                runner.Id,
                runner.Name,
                Math.Max(0, runner.ActiveSlots),
                Math.Max(0, runner.AvailableSlots),
                string.Equals(client?.RunnerGitStatus, "read-only", StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var client in clients)
        {
            if (client.Kind == ClientIdentityKind.Retired
                || client.DrainRequestedAt is not null
                || client.LastSeenAt is null
                || now - client.LastSeenAt.Value > staleAfter
                || client.RunnerActiveSlots is null
                || client.RunnerAvailableSlots is null)
                continue;
            if (result.Any(runner =>
                    string.Equals(runner.Id, client.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(runner.Name, client.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(runner.Name, client.DisplayName, StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(new QueueDepthRunnerCapacity(
                client.Id,
                client.DisplayName,
                Math.Max(0, client.RunnerActiveSlots.Value),
                Math.Max(0, client.RunnerAvailableSlots.Value),
                string.Equals(client.RunnerGitStatus, "read-only", StringComparison.OrdinalIgnoreCase)));
        }

        return result;
    }
}

public sealed record QueueDepthRunnerCapacity(
    string Id,
    string Name,
    int ActiveSlots,
    int AvailableSlots,
    bool ReadOnly)
{
    public int HostSlots => ActiveSlots + AvailableSlots;
}

public sealed record QueueDepthPolicy(
    int MaxActionsPerInterval,
    int? TargetDepth,
    IReadOnlySet<string> ExcludedTaskKeys,
    IReadOnlySet<string> ExcludedProjects)
{
    public static readonly string[] DefaultExcludedTaskKeys = ["MKT-3", "MKT-4", "MKT-5", "MKT-7"];
    public static readonly string[] DefaultExcludedProjects = ["Website", "Agent Studio Website"];
    public static readonly string[] ManualDispatchTags =
    [
        "sight-review",
        "concept-sight-review",
        "sichtblick",
        "dispatch:manual",
        "auto-dispatch:disabled",
    ];

    public static QueueDepthPolicy FromConfiguration(IConfiguration configuration, int cap)
    {
        var excludedTaskKeys = DefaultExcludedTaskKeys.Concat(
            configuration.GetSection("Runner:QueueDepthWatcher:ExcludedTaskKeys").Get<string[]>()
            ?? []).ToArray();
        var excludedProjects = DefaultExcludedProjects.Concat(
            configuration.GetSection("Runner:QueueDepthWatcher:ExcludedProjects").Get<string[]>()
            ?? []).ToArray();
        var configuredTarget =
            configuration.GetValue<int?>("Runner:QueueDepthWatcher:TargetDepth");
        return new QueueDepthPolicy(
            cap,
            configuredTarget is > 0 ? configuredTarget : null,
            excludedTaskKeys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            excludedProjects.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public bool Blocks(TaskInfo task)
    {
        var key = !string.IsNullOrWhiteSpace(task.Key)
            ? task.Key
            : !string.IsNullOrWhiteSpace(task.TaskKey)
                ? task.TaskKey
                : task.Id;
        return task.AutoDispatch is false
               || TaskModes.IsConcept(task.Mode)
               || ExcludedTaskKeys.Contains(key)
               || ExcludedProjects.Contains(task.ProjectName)
               || task.Tags.Any(tag =>
                   ManualDispatchTags.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }
}

public static class QueueDepthActionSources
{
    public const string Backlog = "backlog";
    public const string Epic = "epic-decomposition";
}

public sealed record QueueDepthAction(
    TaskInfo Task,
    string Source,
    string RunnerName,
    int DispatchableDepth,
    int TargetDepth,
    int InFlight,
    int HostSlots);

public sealed record QueueDepthPlan(IReadOnlyList<QueueDepthAction> Actions);

/// <summary>Pure queue selection used by the watcher and its regression tests.</summary>
public static class DispatchQueueDepthPlanner
{
    public static QueueDepthPlan CreatePlan(
        IReadOnlyList<TaskInfo> tasks,
        TaskReferenceIndex references,
        IReadOnlyDictionary<string, ProjectSettings> settingsByProject,
        IReadOnlyList<QueueDepthRunnerCapacity> runners,
        QueueDepthPolicy policy)
    {
        if (policy.MaxActionsPerInterval <= 0)
            return new QueueDepthPlan([]);

        var actions = new List<QueueDepthAction>();
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var epicQueued = false;

        static string Identity(TaskInfo task) => $"{task.WatchPath}\0{task.Id}";

        ProjectSettings Settings(TaskInfo task) =>
            settingsByProject.TryGetValue(task.ProjectName, out var settings)
                ? settings
                : new ProjectSettings();

        foreach (var runner in runners.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (actions.Count >= policy.MaxActionsPerInterval) break;
            if (runner.AvailableSlots <= 0 || runner.HostSlots <= runner.ActiveSlots) continue;

            var dispatchableDepth = tasks.Count(task =>
                RemoteDispatchEligibility.IsClaimableReady(
                    task,
                    Settings(task),
                    runner.Id,
                    runner.Name,
                    runner.ReadOnly,
                    references));
            var targetDepth = policy.TargetDepth ?? runner.HostSlots;
            var needed = Math.Min(
                Math.Max(0, targetDepth - dispatchableDepth),
                runner.AvailableSlots);
            needed = Math.Min(
                needed,
                policy.MaxActionsPerInterval - actions.Count);
            if (needed <= 0) continue;

            bool CanAutoDispatch(TaskInfo task) =>
                task.State == TaskStates.Backlog
                && !policy.Blocks(task)
                && !selected.Contains(Identity(task))
                && RemoteDispatchEligibility.IsAssignedAndRunnable(
                    task,
                    Settings(task),
                    runner.Id,
                    runner.Name,
                    references)
                && !RemoteDispatchEligibility.IsReadOnlyRefused(task, runner.ReadOnly);

            var backlog = tasks
                .Where(task => !TaskKinds.IsEpic(task.Kind) && CanAutoDispatch(task))
                .OrderBy(task => task.Order)
                .ThenBy(task => task.CreatedAt)
                .ToList();
            foreach (var task in backlog.Take(needed))
            {
                selected.Add(Identity(task));
                actions.Add(new QueueDepthAction(
                    task,
                    QueueDepthActionSources.Backlog,
                    runner.Name,
                    dispatchableDepth,
                    targetDepth,
                    runner.ActiveSlots,
                    runner.HostSlots));
                needed--;
            }

            if (needed <= 0 || epicQueued || actions.Count >= policy.MaxActionsPerInterval)
                continue;

            var epic = tasks
                .Where(task => TaskKinds.IsEpic(task.Kind) && CanAutoDispatch(task))
                .Where(candidate => !tasks.Any(task =>
                    string.Equals(task.EpicId, candidate.Id, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(task => task.Order)
                .ThenBy(task => task.CreatedAt)
                .FirstOrDefault();
            if (epic is null) continue;

            selected.Add(Identity(epic));
            epicQueued = true;
            actions.Add(new QueueDepthAction(
                epic,
                QueueDepthActionSources.Epic,
                runner.Name,
                dispatchableDepth,
                targetDepth,
                runner.ActiveSlots,
                runner.HostSlots));
        }

        return new QueueDepthPlan(actions);
    }
}
