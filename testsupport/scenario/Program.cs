using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.TaskServer.Contracts;

if (args.FirstOrDefault() == "fake-cli")
    return await FakeCliAsync(args.Skip(1).ToArray());
if (args.FirstOrDefault() == "fake-review-cli")
    return await FakeReviewCliAsync(args.Skip(1).ToArray());

var options = Options.Parse(args);
var definition = Load<ScenarioDefinition>(Path.Combine(AppContext.BaseDirectory, "scenario.json"));
var fixture = Load<ScenarioFixture>(Path.Combine(AppContext.BaseDirectory, "fixture.json"));
ValidateDefinition(definition);

Directory.CreateDirectory(options.OutputDirectory);
var evidenceDirectory = Path.Combine(options.OutputDirectory, "evidence");
Directory.CreateDirectory(evidenceDirectory);
var selected = options.Level == "smoke"
    ? definition.Steps.Take(definition.SmokeStepCount).ToArray()
    : definition.Steps.ToArray();
var results = new List<StepResult>();
var startedAt = DateTimeOffset.UtcNow;
var scenarioRoot = Path.Combine(Path.GetTempPath(), $"agent-studio-scenario-{Guid.NewGuid():N}");
Directory.CreateDirectory(scenarioRoot);
var processes = new List<RunningProcess>();
HttpClient? client = null;
ScenarioState? state = null;

try
{
    var targetUrl = options.Url;
    if (options.Target == "inproc")
    {
        var repositoryRoot = RepositoryRoot();
        var serverUrl = $"http://127.0.0.1:{FreePort()}";
        var studioUrl = $"http://127.0.0.1:{FreePort()}";
        var dataDirectory = Path.Combine(scenarioRoot, "store");
        var backupDirectory = Path.Combine(scenarioRoot, "backups");
        processes.Add(StartBuilt(
            repositoryRoot,
            "task-server",
            "task-server.dll",
            new Dictionary<string, string?>
            {
                ["STORE_PATH"] = dataDirectory,
                ["BACKUP_PATH"] = backupDirectory,
                ["AUTH"] = "none",
            },
            "--urls", serverUrl));
        await WaitForHttpAsync(serverUrl + "/readyz", processes[^1]);
        processes.Add(StartBuilt(
            repositoryRoot,
            "studio-bff",
            "agent-studio-bff.dll",
            null,
            "--urls", studioUrl,
            "--TaskServer:BaseUrl", serverUrl));
        await WaitForHttpAsync(studioUrl + "/healthz", processes[^1]);
        targetUrl = studioUrl;
    }

    if (string.IsNullOrWhiteSpace(targetUrl))
        throw new ScenarioException($"--url is required for target '{options.Target}'.");

    client = new HttpClient { BaseAddress = new Uri(targetUrl), Timeout = TimeSpan.FromSeconds(15) };
    client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
    client.DefaultRequestHeaders.Add(TaskServerProtocol.ClientVersionHeaderName, "deployment-scenario-v1");
    client.DefaultRequestHeaders.Add("X-Actor-Id", "deployment-scenario");
    if (!string.IsNullOrWhiteSpace(options.Token))
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);

    state = new ScenarioState(
        options,
        definition,
        fixture,
        client,
        scenarioRoot,
        evidenceDirectory,
        processes);

    foreach (var step in selected)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var evidence = await ExecuteStepAsync(state, step);
            stopwatch.Stop();
            results.Add(new StepResult(step.Id, step.Title, "passed", stopwatch.Elapsed, evidence, null));
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var evidence = await EvidenceAsync(state, step, new { error = exception.Message });
            results.Add(new StepResult(step.Id, step.Title, "failed", stopwatch.Elapsed, evidence, exception.Message));
            break;
        }
    }
}
catch (Exception exception)
{
    if (results.Count == 0)
        results.Add(new StepResult("bootstrap", "Start target", "failed", TimeSpan.Zero, null, exception.Message));
}
finally
{
    if (state is not null && options.Target == "remote")
        await ArchiveRemoteFixtureAsync(state);
    foreach (var process in processes.AsEnumerable().Reverse())
        process.Dispose();
    client?.Dispose();
    try { Directory.Delete(scenarioRoot, recursive: true); } catch { }
}

foreach (var missing in selected.Skip(results.Count))
    results.Add(new StepResult(missing.Id, missing.Title, "skipped", TimeSpan.Zero, null, "A previous step failed."));

var passed = results.Count == selected.Length && results.All(result => result.Status == "passed");
await WriteReportsAsync(options, definition, results, startedAt, DateTimeOffset.UtcNow, passed);
Console.WriteLine($"deployment-scenario target={options.Target} level={options.Level} result={(passed ? "passed" : "failed")}");
Console.WriteLine($"report={Path.Combine(options.OutputDirectory, "scenario-report.md")}");
return passed ? 0 : 1;

static async Task<string> ExecuteStepAsync(ScenarioState state, ScenarioStep step)
{
    return step.Id switch
    {
        "bootstrap-principals" => await BootstrapAsync(state, step),
        "register-runner" => await RegisterAsync(state, step),
        "create-task" => await CreateTaskAsync(state, step),
        "claim" => await ObserveClaimAsync(state, step),
        "run-fake-cli" => await ObserveRunAsync(state, step),
        "auto-review" => await ReviewAsync(state, step),
        "integrate" => await SetTaskStateAsync(state, step, "5-human-review"),
        "complete" => await SetTaskStateAsync(state, step, "6-completed"),
        "orchestrator-chat" => await OrchestratorChatAsync(state, step),
        "dossier-decision" => await DossierDecisionAsync(state, step),
        "backup-restore" => await BackupRestoreAsync(state, step),
        "inventory-hash" => await InventoryHashAsync(state, step),
        _ => throw new ScenarioException($"No executor is registered for step '{step.Id}'."),
    };
}

static async Task<string> BootstrapAsync(ScenarioState state, ScenarioStep step)
{
    using var ready = await state.Client.GetAsync("/api/v1/management/status");
    Require(ready.IsSuccessStatusCode, "Task Server readiness failed.");
    var compatibility = await PostAsync<ProtocolCompatibilityRequest, ProtocolCompatibilityResponse>(
        state.Client,
        "/api/v1/protocol/compatibility",
        new ProtocolCompatibilityRequest("studio", "deployment-scenario", TaskServerProtocol.Current));
    Require(compatibility.Supported, compatibility.Reason ?? "Protocol is not supported.");
    return await EvidenceAsync(state, step, new { ready = (int)ready.StatusCode, compatibility });
}

static async Task<string> RegisterAsync(ScenarioState state, ScenarioStep step)
{
    await PutAsync(
        state.Client,
        $"/api/v1/runners/{state.ReviewRunnerId}",
        new RegisterRunnerRequest(
            "deployment-scenario-review",
            "deployment-scenario-review-host",
            state.ReviewInstanceId,
            "1.0.0",
            TaskServerProtocol.Current,
            [
                ReviewCapabilities.ReviewExecutor,
                ReviewCapabilities.GitMaterialization,
                ReviewCapabilities.SemanticReview,
            ]));

    var repository = await SeedRepositoryAsync(state);
    state.RepositoryPath = repository.Bare;
    state.BaseSha = repository.BaseSha;
    var runnerAssembly = BuiltAssembly(RepositoryRoot(), "runner", "agent-host.dll");
    var scenarioAssembly = typeof(Program).Assembly.Location;
    var environment = new Dictionary<string, string?>
    {
        ["RUNNER_HEARTBEAT_SECONDS"] = "5",
        ["RUNNER_RUN_TIMEOUT_SECONDS"] = "60",
        ["RUNNER_ALLOW_INSECURE_HTTP"] = "1",
    };
    if (!string.IsNullOrWhiteSpace(state.Options.Token))
        environment["RUNNER_AUTH_TOKEN"] = state.Options.Token;
    else if (state.Options.Target == "compose")
        environment["RUNNER_AUTH_TOKEN"] = "deployment-scenario-compose";
    var runner = StartDotnet(
        runnerAssembly,
        RepositoryRoot(),
        environment,
        "--poll",
        "--server", state.Client.BaseAddress!.ToString().TrimEnd('/'),
        "--runner-id", state.CodingRunnerId,
        "--runner-name", "deployment-scenario-coding",
        "--hostname", "deployment-scenario-coding-host",
        "--git-remote", state.RepositoryPath,
        "--workdir", Path.Combine(state.Root, "runner-work"),
        "--state-dir", Path.Combine(state.Root, "runner-state"),
        "--cli", "dotnet",
        "--codex-cli", "dotnet",
        "--claude-cli", "dotnet",
        "--cli-args", $"\"{scenarioAssembly}\" fake-cli",
        "--exec-engine", "legacy",
        "--ttl", "30",
        "--max-parallelism", "1",
        "--poll-seconds", "1");
    state.Processes.Add(runner);
    await WaitUntilAsync(async () =>
    {
        var audit = await GetJsonAsync<List<AuditRecordDto>>(state.Client, "/api/v1/management/audit");
        return audit.Any(item => item.Action == "runner.registered" && item.TargetId == state.CodingRunnerId);
    }, runner, "coding runner registration", TimeSpan.FromSeconds(30));
    return await EvidenceAsync(state, step, new { status = "active", state.CodingRunnerId, state.ReviewRunnerId, state.BaseSha });
}

static async Task<string> CreateTaskAsync(ScenarioState state, ScenarioStep step)
{
    var suffix = state.RunSuffix;
    state.Workspace = await PostAsync<CreateWorkspaceRequest, WorkspaceDto>(
        state.Client,
        "/api/v1/workspaces",
        new CreateWorkspaceRequest(state.Fixture.Workspace.Name, state.Fixture.Workspace.Id + suffix));
    state.Project = await PostAsync<CreateProjectRequest, ProjectDto>(
        state.Client,
        "/api/v1/projects",
        new CreateProjectRequest(
            state.Workspace.WorkspaceId,
            state.Fixture.Project.Name,
            state.Fixture.Project.TaskKeyPrefix,
            state.Fixture.Project.Id + suffix));
    var body = JsonSerializer.Serialize(new { state.Fixture.Epic, state.Fixture.Dossier, fixture = "known-failing-test" }, ScenarioJson.Options);
    state.CodingTask = await PostAsync<CreateTaskRequest, TaskDto>(
        state.Client,
        $"/api/v1/projects/{state.Project.ProjectId}/tasks",
        new CreateTaskRequest(
            state.Fixture.Tasks[0].Title,
            body,
            "2-ready",
            state.Fixture.Tasks[0].Id + suffix,
            state.Fixture.Tasks[0].Key));
    state.DecisionTask = await PostAsync<CreateTaskRequest, TaskDto>(
        state.Client,
        $"/api/v1/projects/{state.Project.ProjectId}/tasks",
        new CreateTaskRequest(
            state.Fixture.Tasks[1].Title,
            body,
            "0-backlog",
            state.Fixture.Tasks[1].Id + suffix));
    return await EvidenceAsync(state, step, new { task = state.CodingTask, decisionTask = state.DecisionTask, state.Fixture.Epic, state.Fixture.Dossier });
}

static async Task<string> ObserveClaimAsync(ScenarioState state, ScenarioStep step)
{
    await WaitUntilAsync(async () =>
    {
        var history = await GetJsonAsync<TaskHistoryDto>(state.Client, state.HistoryPath);
        if (history.Runs.Count == 0) return false;
        state.RunId = history.Runs[0].RunId;
        return true;
    }, state.CodingRunner, "coding claim", TimeSpan.FromSeconds(30));
    var captured = await GetJsonAsync<TaskHistoryDto>(state.Client, state.HistoryPath);
    return await EvidenceAsync(state, step, new { task = captured.Task, run = captured.Runs.Single(), claimRecorded = true });
}

static async Task<string> ObserveRunAsync(ScenarioState state, ScenarioStep step)
{
    await WaitUntilAsync(async () =>
    {
        var task = await GetJsonAsync<TaskDto>(state.Client, state.TaskPath);
        return task.State == "4-auto-review";
    }, state.CodingRunner, "fake CLI completion", TimeSpan.FromSeconds(60));
    var history = await GetJsonAsync<TaskHistoryDto>(state.Client, state.HistoryPath);
    var run = history.Runs.Single();
    var handoff = await GetJsonAsync<ResultHandoffDto>(state.Client, $"/api/v1/runs/{state.RunId}/result-handoff");
    Require(handoff.Envelope.ResultSha != state.BaseSha, "The fake CLI did not produce a new result commit.");
    var commitSubject = (await RunAsync(
        "git",
        ["--git-dir", state.RepositoryPath!, "show", "-s", "--format=%s", handoff.Envelope.ResultSha],
        state.Root)).Trim();
    Require(commitSubject == "fix: make fixture test pass", $"Unexpected fixture commit subject: {commitSubject}");
    Require(history.Artifacts.Any(item => item.Name.EndsWith("fake-cli.log", StringComparison.Ordinal)), "The fake CLI log artifact is missing.");
    state.ResultSha = handoff.Envelope.ResultSha;
    return await EvidenceAsync(state, step, new { run, handoff, artifacts = history.Artifacts, expectedCommit = "fix: make fixture test pass" });
}

static async Task<string> ReviewAsync(ScenarioState state, ScenarioStep step)
{
    var handoff = await GetJsonAsync<ResultHandoffDto>(state.Client, $"/api/v1/runs/{state.RunId}/result-handoff");
    var plan = new ReviewPlanDto(
        [new ReviewCommandDto("scenario-review", "deployment-scenario", "fake-review-cli", ["verify"], ExecutionKind: ReviewCommandKinds.Tool)],
        ["deployment-scenario"]);
    var subject = await PostAsync<CreateReviewSubjectRequest, ReviewSubjectDto>(
        state.Client,
        "/api/v1/reviews/subjects",
        new CreateReviewSubjectRequest(
            state.CodingTask!.TaskId,
            state.RunId!,
            handoff.Envelope.RepositoryId,
            handoff.Envelope.RepositoryUrl,
            handoff.Envelope.ResultSha,
            handoff.Envelope.ImmutableRemoteRef,
            null,
            null,
            "deployment-scenario-coding-host",
            "deployment-scenario-policy-v1",
            plan,
            $"scenario-subject:{state.RunId}"));
    var claim = await PostAsync<ReviewClaimRequest, ReviewClaimResponse>(
        state.Client,
        $"/api/v1/runners/{state.ReviewRunnerId}/review-claims",
        new ReviewClaimRequest(state.ReviewRunnerId, state.ReviewInstanceId, 30));
    Require(claim.Status == "claimed" && claim.Attempt is not null && claim.Lease is not null, claim.Message ?? "Review was not claimed.");
    var attempt = claim.Attempt ?? throw new ScenarioException("Review claim omitted its attempt.");
    var lease = claim.Lease ?? throw new ScenarioException("Review claim omitted its lease.");
    var reviewExecution = await RunProcessAsync(
        "dotnet",
        [typeof(Program).Assembly.Location, "fake-review-cli", "verify"],
        state.Root);
    Require(reviewExecution.ExitCode == 0, $"Fake review CLI exited {reviewExecution.ExitCode}: {reviewExecution.Stderr}");
    var reviewLog = Path.Combine(state.EvidenceDirectory, "fake-review-cli.log");
    await File.WriteAllTextAsync(reviewLog, reviewExecution.Stdout + reviewExecution.Stderr);
    var stdoutSha = Sha256(reviewExecution.Stdout);
    var stderrSha = Sha256(reviewExecution.Stderr);
    var treeSha = (await RunAsync("git", ["--git-dir", state.RepositoryPath!, "rev-parse", $"{state.ResultSha}^{{tree}}"], state.Root)).Trim();
    var now = state.Definition.ClockUtc;
    var reviewWorkspace = $"/review/{lease.ResourceNamespace}";
    var command = new ReviewCommandEvidenceDto(
        "scenario-review",
        "deployment-scenario",
        "fake-review-cli",
        ["verify"],
        state.ResultSha!,
        state.ResultSha!,
        treeSha,
        now.UtcDateTime,
        now.AddSeconds(1).UtcDateTime,
        0,
        null,
        stdoutSha,
        stderrSha,
        ExecutionLocation: "remote",
        ExecutorId: state.ReviewRunnerId,
        HostId: lease.HostId,
        AttemptId: attempt.AttemptId);
    var reportRequest = new ReviewReportRequest(
        state.ReviewRunnerId,
        state.ReviewInstanceId,
        lease.LeaseId,
        lease.Fence,
        $"scenario-report:{attempt.AttemptId}",
        "Pass",
        null,
        "The deterministic fixture test passed.",
        new ReviewWorkspaceProofDto(
            handoff.Envelope.RepositoryId,
            state.ResultSha!,
            state.ResultSha!,
            treeSha,
            false,
            false,
            Sha256(reviewWorkspace),
            lease.ResourceNamespace),
        new ReviewEnvironmentDto(
            lease.HostId,
            state.ReviewRunnerId,
            state.ReviewInstanceId,
            Environment.OSVersion.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(),
            new Dictionary<string, string>
            {
                ["runtime"] = ".NET 10",
                ["git"] = "git;sha256=" + new string('d', 64),
                ["command:scenario-review"] = "fake-review-cli;sha256=" + new string('e', 64),
            },
            new Dictionary<string, string>
            {
                ["workspace"] = reviewWorkspace,
                ["cache"] = $"{reviewWorkspace}/cache",
                ["temp"] = $"{reviewWorkspace}/tmp",
                ["ports"] = $"{lease.PortBase}-{lease.PortBase + 7}",
                ["containers"] = lease.ResourceNamespace,
                ["databases"] = lease.ResourceNamespace,
                ["credentials"] = "review-read-only",
            }),
        [command],
        [
            new ReviewArtifactEvidenceDto(
                "scenario-review.stdout.log",
                "text/plain",
                stdoutSha,
                Encoding.UTF8.GetByteCount(reviewExecution.Stdout),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(reviewExecution.Stdout))),
            new ReviewArtifactEvidenceDto(
                "scenario-review.stderr.log",
                "text/plain",
                stderrSha,
                Encoding.UTF8.GetByteCount(reviewExecution.Stderr),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(reviewExecution.Stderr))),
        ],
        [new ReviewVerdictDto("deployment-scenario", "pass", "Verified", "Fixture verification passed.")]);
    var report = await PostAsync<ReviewReportRequest, ReviewReportDto>(
        state.Client,
        $"/api/v1/reviews/attempts/{attempt.AttemptId}/report",
        reportRequest);
    var cleanup = await PostAsync<ReviewCleanupRequest, ReviewCleanupResponse>(
        state.Client,
        $"/api/v1/reviews/attempts/{attempt.AttemptId}/cleanup",
        new ReviewCleanupRequest(
            state.ReviewRunnerId,
            state.ReviewInstanceId,
            lease.LeaseId,
            lease.Fence,
            $"scenario-cleanup:{attempt.AttemptId}",
            true));
    Require(
        string.Equals(report.Outcome, "Pass", StringComparison.OrdinalIgnoreCase)
        && cleanup.Status == "cleaned",
        $"Review report or cleanup did not reach its durable terminal: outcome={report.Outcome}, cleanup={cleanup.Status}.");
    return await EvidenceAsync(state, step, new { subject, attempt, review = report, cleanup, log = Path.GetFileName(reviewLog) });
}

static async Task<string> SetTaskStateAsync(ScenarioState state, ScenarioStep step, string targetState)
{
    var current = await GetJsonAsync<TaskDto>(state.Client, state.TaskPath);
    var updated = await PutResponseAsync<UpdateTaskRequest, TaskDto>(
        state.Client,
        state.TaskPath,
        new UpdateTaskRequest(null, null, targetState, current.Version));
    Require(updated.State == targetState, $"Task state is '{updated.State}', expected '{targetState}'.");
    state.CodingTask = updated;
    return await EvidenceAsync(state, step, new { task = updated });
}

static async Task<string> OrchestratorChatAsync(ScenarioState state, ScenarioStep step)
{
    var contextPath = $"/api/v1/orchestrator-contexts/projects/{state.Project!.ProjectId}";
    await PutAsync(state.Client, contextPath, new { });
    var userTurn = new OrchestratorContextTurnDto(
        "scenario-user-turn",
        state.Definition.ClockUtc.UtcDateTime,
        "user",
        "Run the deployment proof.");
    await PostAsync<AppendOrchestratorContextTurnRequest, OrchestratorContextTurnDto>(
        state.Client,
        contextPath + "/turns",
        new AppendOrchestratorContextTurnRequest(userTurn));
    var turn = new OrchestratorContextTurnDto(
        "scenario-turn",
        state.Definition.ClockUtc.UtcDateTime,
        "orchestrator",
        "Deployment context received.",
        "fake-orchestrator",
        new OrchestratorContextTokenUsageDto("fake-orchestrator", 10, 4, 0, 0),
        Receipt: new OrchestratorContextReceiptDto(
            "scenario-receipt",
            "scenario-user-turn",
            $"project:{state.Project.ProjectId}",
            state.Definition.ClockUtc.UtcDateTime,
            new OrchestratorContextBudgetReceiptDto(100, 200, 300, 24),
            [new OrchestratorContextSourceReceiptDto("scenario-dossier", "dossier", state.ResultSha, Sha256(state.ResultSha!), "fixed", 96, 24, "included")]));
    await PostAsync<AppendOrchestratorContextTurnRequest, OrchestratorContextTurnDto>(
        state.Client,
        contextPath + "/turns",
        new AppendOrchestratorContextTurnRequest(turn));
    var transcript = await GetJsonAsync<OrchestratorContextTranscriptResponse>(state.Client, contextPath + "/turns");
    var recorded = transcript.Turns.Single(item => item.TurnId == turn.TurnId);
    Require(recorded.Receipt?.Sources.Single().Status == "included", "The context receipt was not preserved.");
    return await EvidenceAsync(state, step, new { turn = recorded, transcript.Context });
}

static async Task<string> DossierDecisionAsync(ScenarioState state, ScenarioStep step)
{
    var current = await GetJsonAsync<TaskDto>(
        state.Client,
        $"/api/v1/projects/{state.Project!.ProjectId}/tasks/{state.DecisionTask!.TaskKey}");
    var body = JsonSerializer.Serialize(new { state.Fixture.Dossier, decidedAt = state.Definition.ClockUtc }, ScenarioJson.Options);
    var updated = await PutResponseAsync<UpdateTaskRequest, TaskDto>(
        state.Client,
        $"/api/v1/projects/{state.Project.ProjectId}/tasks/{state.DecisionTask.TaskKey}",
        new UpdateTaskRequest(null, body, "6-completed", current.Version));
    state.DecisionTask = updated;
    Require(state.Fixture.Dossier.Decision == "approved", "The fixed dossier decision is not approved.");
    return await EvidenceAsync(state, step, new { decision = state.Fixture.Dossier.Decision, gate = state.Fixture.Dossier.Gate, task = updated });
}

static async Task<string> BackupRestoreAsync(ScenarioState state, ScenarioStep step)
{
    state.InventoryBefore = await InventoryAsync(state);
    state.InventoryHashBefore = Sha256(state.InventoryBefore);
    var backup = await PostAsync<BackupRequest, BackupResult>(
        state.Client,
        "/api/v1/management/backups",
        new BackupRequest("deployment-scenario"));
    RestoreResult restored;
    if (state.Options.Target == "remote")
    {
        restored = await PostAsync<RestoreRequest, RestoreResult>(
            state.Client,
            "/api/v1/management/restore",
            new RestoreRequest(backup.BackupId, VerifyOnly: true));
    }
    else
    {
        await PutAsync(
            state.Client,
            "/api/v1/management/mode",
            new ChangeModeRequest(TaskServerMode.Maintenance, "deployment scenario restore"));
        restored = await PostAsync<RestoreRequest, RestoreResult>(
            state.Client,
            "/api/v1/management/restore",
            new RestoreRequest(backup.BackupId));
        await PutAsync(
            state.Client,
            "/api/v1/management/mode",
            new ChangeModeRequest(TaskServerMode.Normal, "deployment scenario inventory verification"));
    }
    Require(restored.Verified, "Backup verification failed.");
    Require(state.Options.Target == "remote" ? !restored.Restored : restored.Restored, "Restore behavior did not match the target safety contract.");
    return await EvidenceAsync(state, step, new { backup, restore = restored, remoteNonDestructive = state.Options.Target == "remote" });
}

static async Task<string> InventoryHashAsync(ScenarioState state, ScenarioStep step)
{
    var after = await InventoryAsync(state);
    var afterHash = Sha256(after);
    Require(afterHash == state.InventoryHashBefore, $"Inventory hash changed: {state.InventoryHashBefore} != {afterHash}.");
    return await EvidenceAsync(state, step, new { before = state.InventoryHashBefore, after = afterHash, equal = true });
}

static async Task<string> InventoryAsync(ScenarioState state)
{
    var workspaces = await GetJsonAsync<List<WorkspaceDto>>(state.Client, "/api/v1/workspaces");
    var projects = await GetJsonAsync<List<ProjectDto>>(state.Client, $"/api/v1/projects?workspaceId={Uri.EscapeDataString(state.Workspace!.WorkspaceId)}");
    var tasks = await GetJsonAsync<List<TaskDto>>(state.Client, $"/api/v1/projects/{state.Project!.ProjectId}/tasks");
    var transcript = await GetJsonAsync<OrchestratorContextTranscriptResponse>(
        state.Client,
        $"/api/v1/orchestrator-contexts/projects/{state.Project.ProjectId}/turns");
    return JsonSerializer.Serialize(new
    {
        workspaces = workspaces.Where(item => item.WorkspaceId == state.Workspace.WorkspaceId).OrderBy(item => item.WorkspaceId),
        projects = projects.OrderBy(item => item.ProjectId),
        tasks = tasks.OrderBy(item => item.TaskKey),
        turns = transcript.Turns.OrderBy(item => item.TurnId),
    }, ScenarioJson.Options);
}

static async Task ArchiveRemoteFixtureAsync(ScenarioState state)
{
    if (state.Project is null) return;
    foreach (var task in new[] { state.CodingTask, state.DecisionTask }.Where(item => item is not null))
    {
        try
        {
            var current = await GetJsonAsync<TaskDto>(state.Client, $"/api/v1/projects/{state.Project.ProjectId}/tasks/{task!.TaskKey}");
            if (current.State == "7-archive") continue;
            await PutResponseAsync<UpdateTaskRequest, TaskDto>(
                state.Client,
                $"/api/v1/projects/{state.Project.ProjectId}/tasks/{task.TaskKey}",
                new UpdateTaskRequest(null, null, "7-archive", current.Version));
        }
        catch { }
    }
}

static async Task<(string Bare, string BaseSha)> SeedRepositoryAsync(ScenarioState state)
{
    var source = Path.Combine(AppContext.BaseDirectory, "fixture", "repository");
    var seed = Path.Combine(state.Root, "seed");
    var bare = Path.Combine(state.Root, "origin.git");
    CopyDirectory(source, seed);
    await RunAsync("git", ["init", "-b", "main"], seed);
    await RunAsync("git", ["config", "user.name", "Deployment Scenario"], seed);
    await RunAsync("git", ["config", "user.email", "scenario@example.invalid"], seed);
    await RunAsync("git", ["add", "."], seed);
    await RunAsync(
        "git",
        ["commit", "-m", "test: seed failing deployment fixture"],
        seed,
        FixedGitClock());
    await RunAsync("git", ["init", "--bare", bare], state.Root);
    await RunAsync("git", ["remote", "add", "origin", bare], seed);
    await RunAsync("git", ["push", "-u", "origin", "main"], seed);
    await RunAsync("git", ["symbolic-ref", "HEAD", "refs/heads/main"], bare);
    var sha = (await RunAsync("git", ["rev-parse", "HEAD"], seed)).Trim();
    return (bare, sha);
}

static async Task<int> FakeCliAsync(string[] args)
{
    if (args.Contains("--version"))
    {
        Console.WriteLine("deployment-scenario-cli 1.0.0");
        return 0;
    }
    await File.WriteAllTextAsync("answer.txt", "42\n");
    await RunAsync("git", ["config", "user.name", "Deployment Scenario"], Environment.CurrentDirectory);
    await RunAsync("git", ["config", "user.email", "scenario@example.invalid"], Environment.CurrentDirectory);
    await RunAsync("git", ["add", "answer.txt"], Environment.CurrentDirectory);
    await RunAsync(
        "git",
        ["commit", "-m", "fix: make fixture test pass"],
        Environment.CurrentDirectory,
        FixedGitClock());
    var verify = OperatingSystem.IsWindows()
        ? (await File.ReadAllTextAsync("answer.txt")).Trim() == "42"
        : (await RunProcessAsync("sh", ["verify.sh"], Environment.CurrentDirectory)).ExitCode == 0;
    if (!verify) return 1;
    var resultsDirectory = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR")
        ?? Path.Combine(Environment.CurrentDirectory, "results");
    Directory.CreateDirectory(resultsDirectory);
    await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "fake-cli.log"), "fixed clock: 2026-09-06T10:00:00Z\nfixture verify: passed\n");
    Console.WriteLine("{\"type\":\"agent_message\",\"text\":\"deterministic fixture repaired\"}");
    Console.WriteLine("{\"type\":\"tool\",\"name\":\"fixture-verify\",\"exitCode\":0}");
    Console.WriteLine("[[TASK_DONE]]");
    return 0;
}

static Task<int> FakeReviewCliAsync(string[] args)
{
    if (args.Contains("--version")) Console.WriteLine("deployment-scenario-review-cli 1.0.0");
    else
    {
        Console.WriteLine("fixed clock: 2026-09-06T10:00:00Z");
        Console.WriteLine("fixture verify: passed");
    }
    return Task.FromResult(0);
}

static async Task<string> EvidenceAsync(ScenarioState state, ScenarioStep step, object value)
{
    var path = Path.Combine(state.EvidenceDirectory, step.Id + ".json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { step.Assertions, value }, ScenarioJson.Options));
    return "evidence/" + Path.GetFileName(path);
}

static async Task WriteReportsAsync(
    Options options,
    ScenarioDefinition definition,
    IReadOnlyList<StepResult> results,
    DateTimeOffset started,
    DateTimeOffset finished,
    bool passed)
{
    var markdown = new StringBuilder()
        .AppendLine("# Deployment scenario report")
        .AppendLine()
        .AppendLine($"- Scenario: `{definition.ScenarioId}`")
        .AppendLine($"- Target: `{options.Target}`")
        .AppendLine($"- Level: `{options.Level}`")
        .AppendLine($"- Result: **{(passed ? "passed" : "failed")}**")
        .AppendLine($"- Duration: `{(finished - started).TotalSeconds:F3}s`")
        .AppendLine()
        .AppendLine("| Step | Status | Duration | Evidence |")
        .AppendLine("|---|---:|---:|---|");
    foreach (var result in results)
    {
        var evidence = result.Evidence is null ? "-" : $"[{result.Evidence}]({result.Evidence})";
        var suffix = result.Error is null ? string.Empty : $"<br>{EscapeMarkdown(result.Error)}";
        markdown.AppendLine($"| {result.Title} | {result.Status} | {result.Duration.TotalMilliseconds:F0} ms | {evidence}{suffix} |");
    }
    await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "scenario-report.md"), markdown.ToString());

    var failures = results.Count(item => item.Status == "failed");
    var skipped = results.Count(item => item.Status == "skipped");
    var xml = new StringBuilder()
        .Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n")
        .Append($"<testsuite name=\"deployment-scenario.{Xml(options.Target)}.{Xml(options.Level)}\" tests=\"{results.Count}\" failures=\"{failures}\" skipped=\"{skipped}\" time=\"{(finished - started).TotalSeconds:F3}\">\n");
    foreach (var result in results)
    {
        xml.Append($"  <testcase classname=\"deployment-scenario\" name=\"{Xml(result.Id)}\" time=\"{result.Duration.TotalSeconds:F3}\">");
        if (result.Status == "failed") xml.Append($"<failure message=\"{Xml(result.Error ?? "failed")}\" />");
        if (result.Status == "skipped") xml.Append($"<skipped message=\"{Xml(result.Error ?? "skipped")}\" />");
        xml.Append("</testcase>\n");
    }
    xml.Append("</testsuite>\n");
    await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "scenario-junit.xml"), xml.ToString());
}

static void ValidateDefinition(ScenarioDefinition definition)
{
    Require(definition.SchemaVersion == 1, "Unsupported scenario schema version.");
    Require(definition.SmokeStepCount is > 0 && definition.SmokeStepCount <= definition.Steps.Count, "Invalid smoke step count.");
    Require(definition.Steps.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == definition.Steps.Count, "Scenario step ids must be unique.");
    var types = new HashSet<string>(["httpStatus", "jsonEquals", "fileExists", "gitCommit", "sha256Equals"], StringComparer.Ordinal);
    Require(definition.Steps.All(step => step.Assertions.Count > 0 && step.Assertions.All(assertion => types.Contains(assertion.Type))), "Every scenario step needs supported typed assertions.");
}

static T Load<T>(string path) where T : class
    => JsonSerializer.Deserialize<T>(File.ReadAllText(path), ScenarioJson.Options)
       ?? throw new ScenarioException($"Could not parse {path}.");

static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient client, string path, TRequest request)
{
    using var response = await client.PostAsJsonAsync(path, request, ScenarioJson.Options);
    return await ReadResponseAsync<TResponse>(path, response);
}

static async Task PutAsync<TRequest>(HttpClient client, string path, TRequest request)
{
    using var response = await client.PutAsJsonAsync(path, request, ScenarioJson.Options);
    _ = await ReadResponseAsync<JsonNode>(path, response);
}

static async Task<TResponse> PutResponseAsync<TRequest, TResponse>(HttpClient client, string path, TRequest request)
{
    using var response = await client.PutAsJsonAsync(path, request, ScenarioJson.Options);
    return await ReadResponseAsync<TResponse>(path, response);
}

static async Task<T> GetJsonAsync<T>(HttpClient client, string path)
{
    using var response = await client.GetAsync(path);
    return await ReadResponseAsync<T>(path, response);
}

static async Task<T> ReadResponseAsync<T>(string path, HttpResponseMessage response)
{
    var content = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
        throw new ScenarioException($"{path} returned {(int)response.StatusCode}: {content}");
    return JsonSerializer.Deserialize<T>(content, ScenarioJson.Options)
           ?? throw new ScenarioException($"{path} returned an empty response.");
}

static async Task WaitUntilAsync(Func<Task<bool>> predicate, RunningProcess? process, string description, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    Exception? last = null;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (process is not null && process.Process.HasExited)
            throw new ScenarioException($"Runner exited while waiting for {description}: {process}");
        try
        {
            if (await predicate()) return;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ScenarioException)
        {
            last = exception;
        }
        await Task.Delay(100);
    }
    throw new ScenarioException($"Timed out waiting for {description}: {last?.Message}\n{process}");
}

static async Task WaitForHttpAsync(string url, RunningProcess process)
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    await WaitUntilAsync(async () => (await client.GetAsync(url)).IsSuccessStatusCode, process, url, TimeSpan.FromSeconds(30));
}

static RunningProcess StartBuilt(
    string root,
    string projectDirectory,
    string assemblyName,
    IReadOnlyDictionary<string, string?>? environment,
    params string[] arguments)
    => StartDotnet(BuiltAssembly(root, projectDirectory, assemblyName), root, environment, arguments);

static string BuiltAssembly(string root, string projectDirectory, string assemblyName)
{
    foreach (var configuration in new[] { "Release", "Debug" })
    {
        var path = Path.Combine(root, projectDirectory, "bin", configuration, "net10.0", assemblyName);
        if (File.Exists(path)) return path;
    }
    throw new FileNotFoundException($"Build {projectDirectory} before running the deployment scenario.");
}

static RunningProcess StartDotnet(
    string assembly,
    string workingDirectory,
    IReadOnlyDictionary<string, string?>? environment,
    params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    start.ArgumentList.Add(assembly);
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    if (environment is not null)
        foreach (var (key, value) in environment) start.Environment[key] = value;
    var process = Process.Start(start) ?? throw new ScenarioException($"Could not start {assembly}.");
    var running = new RunningProcess(process);
    process.OutputDataReceived += (_, eventArgs) => running.Append(eventArgs.Data);
    process.ErrorDataReceived += (_, eventArgs) => running.Append(eventArgs.Data);
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return running;
}

static async Task<string> RunAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    IReadOnlyDictionary<string, string>? environment = null)
{
    var result = await RunProcessAsync(fileName, arguments, workingDirectory, environment);
    if (result.ExitCode != 0)
        throw new ScenarioException($"{fileName} exited {result.ExitCode}: {result.Stdout}\n{result.Stderr}");
    return result.Stdout;
}

static async Task<ProcessResult> RunProcessAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    IReadOnlyDictionary<string, string>? environment = null)
{
    var start = new ProcessStartInfo(fileName)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    if (environment is not null)
        foreach (var (key, value) in environment) start.Environment[key] = value;
    using var process = Process.Start(start) ?? throw new ScenarioException($"Could not start {fileName}.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return new ProcessResult(process.ExitCode, await stdout, await stderr);
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
}

static int FreePort()
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static string RepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "agent-taskboard.sln"))) current = current.Parent;
    return current?.FullName ?? throw new ScenarioException("Could not locate the repository root.");
}

static string Sha256(string value)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

static string EscapeMarkdown(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
static IReadOnlyDictionary<string, string> FixedGitClock() => new Dictionary<string, string>
{
    ["GIT_AUTHOR_DATE"] = "2026-09-06T10:00:00Z",
    ["GIT_COMMITTER_DATE"] = "2026-09-06T10:00:00Z",
};
static void Require(bool condition, string message) { if (!condition) throw new ScenarioException(message); }

sealed record Options(string Target, string Level, string? Url, string? Token, string OutputDirectory)
{
    public static Options Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        var target = Value("--target") ?? "inproc";
        var level = Value("--level") ?? "smoke";
        if (target is not ("inproc" or "compose" or "remote")) throw new ScenarioException("--target must be inproc, compose, or remote.");
        if (level is not ("smoke" or "full")) throw new ScenarioException("--level must be smoke or full.");
        var output = Value("--output") ?? Environment.GetEnvironmentVariable("JOB_RESULTS_DIR") ?? Path.Combine(Environment.CurrentDirectory, "results", "deployment-scenario");
        return new Options(target, level, Value("--url") ?? Environment.GetEnvironmentVariable("SCENARIO_URL"), Value("--token") ?? Environment.GetEnvironmentVariable("SCENARIO_TOKEN"), Path.GetFullPath(output));
    }
}

sealed class ScenarioState(
    Options options,
    ScenarioDefinition definition,
    ScenarioFixture fixture,
    HttpClient client,
    string root,
    string evidenceDirectory,
    List<RunningProcess> processes)
{
    public Options Options { get; } = options;
    public ScenarioDefinition Definition { get; } = definition;
    public ScenarioFixture Fixture { get; } = fixture;
    public HttpClient Client { get; } = client;
    public string Root { get; } = root;
    public string EvidenceDirectory { get; } = evidenceDirectory;
    public List<RunningProcess> Processes { get; } = processes;
    public string RunSuffix { get; } = options.Target == "remote"
        ? "-" + (Environment.GetEnvironmentVariable("SCENARIO_RUN_ID") ?? Guid.NewGuid().ToString("N")[..10])
        : string.Empty;
    public string CodingRunnerId => "deployment-scenario-coding" + RunSuffix;
    public string ReviewRunnerId => "deployment-scenario-review" + RunSuffix;
    public string ReviewInstanceId => "deployment-scenario-review-instance" + RunSuffix;
    public WorkspaceDto? Workspace { get; set; }
    public ProjectDto? Project { get; set; }
    public TaskDto? CodingTask { get; set; }
    public TaskDto? DecisionTask { get; set; }
    public string? RepositoryPath { get; set; }
    public string? BaseSha { get; set; }
    public string? ResultSha { get; set; }
    public string? RunId { get; set; }
    public string? InventoryBefore { get; set; }
    public string? InventoryHashBefore { get; set; }
    public RunningProcess? CodingRunner => Processes.LastOrDefault(process => process.Label.EndsWith("agent-host.dll", StringComparison.OrdinalIgnoreCase));
    public string TaskPath => $"/api/v1/projects/{Project!.ProjectId}/tasks/{CodingTask!.TaskKey}";
    public string HistoryPath => TaskPath + "/history";
}

sealed class RunningProcess(Process process) : IDisposable
{
    readonly List<string> output = [];
    public Process Process { get; } = process;
    public string Label { get; } = process.StartInfo.ArgumentList.FirstOrDefault() ?? process.StartInfo.FileName;
    public void Append(string? line) { if (line is not null) lock (output) output.Add(line); }
    public void Dispose()
    {
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
            Process.WaitForExit(5000);
        }
        Process.Dispose();
    }
    public override string ToString() { lock (output) return string.Join(Environment.NewLine, output.TakeLast(80)); }
}

sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
sealed record StepResult(string Id, string Title, string Status, TimeSpan Duration, string? Evidence, string? Error);
sealed record ScenarioDefinition(int SchemaVersion, string ScenarioId, DateTimeOffset ClockUtc, int SmokeStepCount, IReadOnlyList<ScenarioStep> Steps);
sealed record ScenarioStep(string Id, string Title, IReadOnlyList<TypedAssertion> Assertions);
sealed record TypedAssertion(string Type, string? Path, string Expected);
sealed record ScenarioFixture(WorkspaceSeed Workspace, ProjectSeed Project, DossierSeed Dossier, EpicSeed Epic, IReadOnlyList<TaskSeed> Tasks);
sealed record WorkspaceSeed(string Id, string Name);
sealed record ProjectSeed(string Id, string Name, string TaskKeyPrefix);
sealed record DossierSeed(string Id, string Title, string Gate, string Decision);
sealed record EpicSeed(string Id, string Title, IReadOnlyList<string> Tasks);
sealed record TaskSeed(string Id, string Key, string Title, string State);
sealed class ScenarioException(string message) : Exception(message);
static class ScenarioJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
