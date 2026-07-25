using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectRunnerPickupAtomicityTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;

    public ProjectRunnerPickupAtomicityTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-pickup-atomicity-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
        InitializeGitRepository();
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task AutoPickupSpawnFailure_RevertsReady_RemovesLock_AndFreesSlot()
    {
        WriteJob(TaskStates.Ready, "job-a");
        var cli = new FailingCliService();
        var runner = BuildRunner(cli);
        runner.SetMode("auto-continuous");

        await runner.TickAsync(CancellationToken.None);

        var readyFolder = Path.Combine(_watchPath, TaskStates.Ready, "job-a");
        var progressFolder = Path.Combine(_watchPath, TaskStates.Progress, "job-a");
        Assert.True(cli.StartCalled, "the fake CLI should have reached the spawn boundary");
        Assert.True(cli.PickupLockExistedAtStart,
            "the pickup lock must be stamped on the actual 3-progress folder before StartAsync is called");
        Assert.True(Directory.Exists(readyFolder), "failed spawn must return the task to 2-ready immediately");
        Assert.False(Directory.Exists(progressFolder), "failed spawn must not leave a zombie in 3-progress");
        Assert.False(File.Exists(Path.Combine(readyFolder, PickupLockFile.LockFileName)),
            "the pickup lock must be removed when the run never starts");
        Assert.Equal(0, runner.GetStatus().OccupiedSlots);

        Assert.False(File.Exists(Path.Combine(readyFolder, "logs", "session-events.jsonl")),
            "a rejected spawn must not create a historical run boundary");
        var timelinePath = Path.Combine(readyFolder, "logs", "timeline.jsonl");
        Assert.False(File.Exists(timelinePath)
            && File.ReadAllText(timelinePath).Contains(TimelineEventKinds.AgentRunStarted, StringComparison.Ordinal),
            "agent_run_started must be durable only after process confirmation");

        var cliOutput = Path.Combine(readyFolder, "logs", "cli-output.log");
        Assert.True(File.Exists(cliOutput), "spawn failures should leave a diagnostic in cli-output.log");
        Assert.Contains("spawn failed", File.ReadAllText(cliOutput), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoPickupAdmissionFault_RevertsReady_RemovesLock_AndFreesSlot()
    {
        WriteJob(TaskStates.Ready, "job-fault");
        var cli = new FailingCliService(throwOnStart: true);
        var runner = BuildRunner(cli);
        runner.SetMode("auto-continuous");

        await runner.TickAsync(CancellationToken.None);

        var readyFolder = Path.Combine(_watchPath, TaskStates.Ready, "job-fault");
        Assert.True(cli.StartCalled, "fault injection must reach the process-start boundary");
        Assert.True(Directory.Exists(readyFolder), "an admission exception must self-heal back to Ready");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "job-fault")),
            "an admission exception must never leave Progress without an active run");
        Assert.False(File.Exists(Path.Combine(readyFolder, PickupLockFile.LockFileName)),
            "fault recovery must release durable ownership so the bounded wake can retry");
        Assert.Equal(0, runner.GetStatus().OccupiedSlots);
        Assert.False(File.Exists(Path.Combine(readyFolder, "logs", "session-events.jsonl")),
            "an admission fault must not create a historical run boundary");
    }

    private void WriteJob(string state, string slug, int order = 1)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "prompt.md"), "Do the task.");
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":{order}," +
            "\"agent\":\"claude\",\"cliType\":\"claude\",\"ownerClientId\":\"local-default\"}");
    }

    private void InitializeGitRepository()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "test@example.invalid");
        RunGit("config", "user.name", "Pickup Atomicity Test");
        File.WriteAllText(Path.Combine(_watchPath, "README.md"), "test repository");
        RunGit("add", "README.md");
        RunGit("commit", "-q", "-m", "seed");
        RunGit("checkout", "-q", "-b", "develop");
    }

    private void RunGit(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _watchPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("git did not start");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
    }

    private ProjectRunner BuildRunner(FailingCliService cli)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot
            })
            .Build();

        var entry = new WatchPathEntry
        {
            Name = ProjectName,
            Path = _watchPath,
            RootPath = _watchPath,
            RepositoryPath = _watchPath
        };

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        var router = new CliRouter(cli);
        var claude = GenericCliExecutionService.ForClaude(NullLogger<GenericCliExecutionService>.Instance, config);
        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);
        var quotaCacheStore = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);
        var pickupLock = new PickupLockFile(NullLogger<PickupLockFile>.Instance);
        var pickupLockOwner = new PickupLockOwner
        {
            Pid = Environment.ProcessId,
            Hostname = Environment.MachineName,
            Role = RunnerRoles.Orchestrator,
            BackendName = "test",
            BackendPort = 0
        };

        return new ProjectRunner(
            ProjectName, entry,
            NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess,
            bus: null,
            pickupLock: pickupLock,
            pickupLockOwner: pickupLockOwner);
    }

    private sealed class FailingCliService : ICliExecutionService
    {
        private readonly bool _throwOnStart;

        public FailingCliService(bool throwOnStart = false)
        {
            _throwOnStart = throwOnStart;
        }

        public string CliType => CliTypes.Claude;
        public bool StartCalled { get; private set; }
        public bool PickupLockExistedAtStart { get; private set; }

        public string GetCliPath() => "fake-claude";
        public bool IsAvailable() => true;
        public (bool Available, string? Version, string Path) TestCliPath(string? path = null) => (true, "test", path ?? GetCliPath());

        public Task<(CliExecution? Execution, string? Error)> StartAsync(
            string jobId,
            string jobKey,
            string prompt,
            string workingDirectory,
            string? sessionName = null,
            bool resumeSession = false,
            string? model = null,
            string? thinkingLevel = null,
            string? jobFolderPath = null,
            string? permissionMode = null,
            string? contextMode = null,
            CancellationToken ct = default)
        {
            StartCalled = true;
            PickupLockExistedAtStart = !string.IsNullOrWhiteSpace(jobFolderPath)
                && File.Exists(Path.Combine(jobFolderPath, PickupLockFile.LockFileName));
            if (_throwOnStart) throw new InvalidOperationException("injected admission fault");
            return Task.FromResult<(CliExecution?, string?)>((null, "fake spawn failure"));
        }

        public bool Stop(string jobKey, RunStopReason reason = RunStopReason.UserStop) => false;
        public bool SendInput(string jobKey, string input) => false;
        public List<CliOutputLine> GetOutput(string jobKey) => [];
        public void DiscardPersistedOutput(string jobKey) { }
        public void ReleaseOutputResources(string jobKey) { }
        public CliExecution? GetExecution(string jobKey) => null;
        public SessionUsage? GetLastUsage(string jobKey) => null;
        public bool IsRunningForProject(string rootPath) => false;
        public DateTime? GetLastStreamedAt(string jobKey) => null;
        public WatchdogState GetWatchdogState(string jobKey) => WatchdogState.Healthy;
        public void SetWatchdogState(string jobKey, WatchdogState state) { }
        public void ReattachOnStartup() { }
        public Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
            => Task.FromResult(new CliModelCatalog { Models = [], Source = "fake", FetchedAt = DateTime.UtcNow });
        public bool IsCompatibleSessionName(string? sessionName) => true;

        public event Action<string, CliOutputLine>? OnOutput;
        public event Action<string, CliExecution>? OnStarted;
        public event Action<string, CliExecution>? OnFinished;
        public event Action<string, CliRunEvent>? OnRunEvent;
    }
}
