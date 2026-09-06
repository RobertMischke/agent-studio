using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;

try
{
    return await DeploymentScenarioProgram.RunAsync(args);
}
catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

internal static class DeploymentScenarioProgram
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var options = ScenarioOptions.Parse(args);
        Directory.CreateDirectory(options.OutputDirectory);
        var definition = JsonSerializer.Deserialize<ScenarioDefinition>(
            await File.ReadAllTextAsync(options.DefinitionPath), Json)
            ?? throw new InvalidDataException("Scenario definition is empty.");
        Validate(definition);

        var selected = options.Level == "smoke"
            ? definition.Steps.Take(definition.SmokeStepCount).ToArray()
            : definition.Steps.ToArray();
        var run = new ScenarioRun(definition, options, selected);
        try
        {
            await using var target = await ScenarioTarget.CreateAsync(options, definition, run);
            foreach (var step in selected)
                await run.RecordAsync(step, () => target.ExecuteAsync(step.Id));
        }
        catch (Exception exception)
        {
            run.RecordHarnessFailure(exception);
        }
        finally
        {
            await run.WriteReportsAsync();
        }

        Console.WriteLine($"scenario={definition.Id} target={options.Target} level={options.Level} status={(run.Passed ? "passed" : "failed")}");
        Console.WriteLine($"report={Path.Combine(options.OutputDirectory, "scenario-report.md")}");
        Console.WriteLine($"junit={Path.Combine(options.OutputDirectory, "scenario-junit.xml")}");
        return run.Passed ? 0 : 1;
    }

    private static void Validate(ScenarioDefinition definition)
    {
        if (definition.SchemaVersion != 1 || string.IsNullOrWhiteSpace(definition.Id))
            throw new InvalidDataException("Scenario definition must use schemaVersion 1 and have an id.");
        if (definition.SmokeStepCount < 1 || definition.SmokeStepCount > definition.Steps.Count)
            throw new InvalidDataException("smokeStepCount is outside the defined step range.");
        if (definition.Steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != definition.Steps.Count)
            throw new InvalidDataException("Scenario step ids must be unique.");
        var assertionTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "resource.count", "git.test-state", "field.equals", "number.minimum",
            "git.commit", "file.exists", "git.ancestor", "hash.sha256", "hash.equals",
        };
        foreach (var assertion in definition.Steps.SelectMany(step => step.Assertions))
            if (!assertionTypes.Contains(assertion.Type))
                throw new InvalidDataException($"Unknown typed assertion '{assertion.Type}'.");
    }
}

internal sealed class ScenarioTarget : IAsyncDisposable
{
    private const string CodingRunner = "scenario-coding";
    private const string CodingInstance = "scenario-coding-instance";
    private const string ReviewRunner = "scenario-review";
    private const string ReviewInstance = "scenario-review-instance";
    private const string ResultRepositoryId = "scenario-repository";
    private readonly IScenarioApi _api;
    private readonly ScenarioDefinition _definition;
    private readonly ScenarioOptions _options;
    private readonly ScenarioRun _run;
    private readonly string _workingRoot;
    private readonly string _repository;
    private WorkspaceDto? _workspace;
    private ProjectDto? _project;
    private TaskDto? _dossier;
    private TaskDto? _task;
    private RunnerDto? _runner;
    private ClaimResponse? _claim;
    private string? _baseSha;
    private string? _resultSha;
    private ReviewClaimResponse? _reviewClaim;
    private ReviewReportDto? _reviewReport;
    private OrchestrationRunDto? _orchestration;
    private BackupResult? _backup;
    private string? _inventoryBefore;
    private string? _inventoryAfter;
    private RestoreResult? _restoreResult;
    private int _chatReceiptCount;

    private ScenarioTarget(
        IScenarioApi api,
        ScenarioDefinition definition,
        ScenarioOptions options,
        ScenarioRun run,
        string workingRoot)
    {
        _api = api;
        _definition = definition;
        _options = options;
        _run = run;
        _workingRoot = workingRoot;
        _repository = Path.Combine(workingRoot, "repository");
    }

    public static async Task<ScenarioTarget> CreateAsync(
        ScenarioOptions options,
        ScenarioDefinition definition,
        ScenarioRun run)
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-studio-scenario-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        IScenarioApi api = options.Target == "inproc"
            ? await InProcessScenarioApi.CreateAsync(root, definition)
            : new HttpScenarioApi(options, definition);
        await api.ReadyAsync();
        return new ScenarioTarget(api, definition, options, run, root);
    }

    public async Task ExecuteAsync(string stepId)
    {
        Func<Task> action = stepId switch
        {
            "bootstrap-principals" => BootstrapAsync,
            "register-runner" => RegisterRunnerAsync,
            "create-task" => CreateTaskAsync,
            "claim" => ClaimAsync,
            "fake-cli-run" => FakeCliRunAsync,
            "auto-review" => AutoReviewAsync,
            "integrate" => IntegrateAsync,
            "complete" => CompleteAsync,
            "orchestrator-chat" => OrchestratorChatAsync,
            "dossier-decision" => DossierDecisionAsync,
            "backup" => BackupAsync,
            "restore" => RestoreAsync,
            "inventory-equality" => InventoryEqualityAsync,
            _ => throw new InvalidDataException($"No executor is registered for scenario step '{stepId}'."),
        };
        await action();
        await AssertDefinitionAsync(stepId);
    }

    private async Task BootstrapAsync()
    {
        CopyDirectory(Path.Combine(_options.DefinitionDirectory, _definition.Fixture.Repository), _repository);
        await GitAsync("init", "-b", "main");
        await GitAsync("add", ".");
        await GitAsync("-c", "user.name=Scenario Runner", "-c", "user.email=scenario@example.invalid",
            "commit", "-m", "test: seed deterministic deployment fixture");
        _baseSha = (await GitAsync("rev-parse", "HEAD")).Output.Trim();
        await GitAsync("checkout", "-b", "scenario-result");
        var red = await ProcessAsync("node", ["--test", "test.mjs"], _repository, allowFailure: true);
        Require(red.ExitCode != 0, "Seeded fixture unexpectedly passed before the fake coding CLI ran.");

        var suffix = _options.Target == "remote" ? $" {Guid.NewGuid():N}" : string.Empty;
        var prefix = _options.Target == "remote"
            ? "D" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant()
            : _definition.Fixture.TaskPrefix;
        _workspace = await _api.CreateWorkspaceAsync($"Deployment scenario{suffix}");
        _project = await _api.CreateProjectAsync(
            _workspace.WorkspaceId,
            $"{_definition.Fixture.ProjectName}{suffix}",
            prefix);
        _dossier = await _api.CreateTaskAsync(
            _project.ProjectId,
            "Deployment decision dossier",
            "decisionGate=pending; fixtureClock=" + _definition.ClockUtc,
            "5-human-review");
        _ = await _api.CreateTaskAsync(_project.ProjectId, "Deployment epic", "kind=epic; children=2", "0-backlog");
        _ = await _api.CreateTaskAsync(_project.ProjectId, "Epic child A", "epicChild=1", "0-backlog");
        _ = await _api.CreateTaskAsync(_project.ProjectId, "Epic child B", "epicChild=2", "0-backlog");
        _run.Evidence("fixture-tasks.json", await _api.ListTasksAsync(_project.ProjectId));
    }

    private async Task RegisterRunnerAsync()
    {
        _runner = await _api.RegisterRunnerAsync(
            CodingRunner,
            new RegisterRunnerRequest(
                CodingRunner, "scenario-host", CodingInstance, "scenario-v1", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]));
        Require(_runner.Status == "active", $"Runner status was '{_runner.Status}'.");
        _run.Evidence("runner.json", _runner);
    }

    private async Task CreateTaskAsync()
    {
        _task = await _api.CreateTaskAsync(
            Required(_project).ProjectId,
            "Deterministic deployment regression",
            "Run the seeded failing test, commit the fix, review, integrate, and complete.",
            "2-ready");
        Require(_task.State == "2-ready", "Coding task was not created in Ready.");
        _run.Evidence("task-created.json", _task);
    }

    private async Task ClaimAsync()
    {
        _claim = await _api.ClaimAsync(new ClaimRequest(CodingRunner, CodingInstance, RequestedTtlSeconds: 120));
        Require(_claim.Status == "claimed", $"Claim status was '{_claim.Status}'.");
        Require(_claim.Lease is { Fence: >= 1 }, "Claim did not return a positive fence.");
        Require(_claim.Task?.TaskId == Required(_task).TaskId, "Claim selected a different task.");
        _run.Evidence("coding-claim.json", _claim);
    }

    private async Task FakeCliRunAsync()
    {
        var logPath = _run.EvidencePath("runner.log");
        var command = await ProcessAsync(
            "sh",
            [Path.Combine(_options.DefinitionDirectory, _definition.Fixture.FakeCodingCli)],
            _repository);
        await File.WriteAllTextAsync(logPath, command.Output);
        Require(command.ExitCode == 0, "Fake coding CLI failed: " + command.Output);
        _resultSha = (await GitAsync("rev-parse", "HEAD")).Output.Trim();
        Require(_resultSha != _baseSha && _resultSha.Length == 40, "Fake coding CLI did not produce a commit.");
        var green = await ProcessAsync("node", ["--test", "test.mjs"], _repository, allowFailure: true);
        Require(green.ExitCode == 0, "Fixture remained red after fake coding CLI: " + green.Output);

        var claim = Required(_claim);
        var lease = Required(claim.Lease);
        var run = Required(claim.Run);
        await _api.IngestEventAsync(run.RunId, new EventIngestRequest(
            "evt-" + run.RunId, LifecycleEventKinds.AgentMessage,
            JsonSerializer.Serialize(new { text = "fake-coding-cli=passed" }),
            "scenario-log:" + run.RunId, lease.Fence, RunnerId: CodingRunner,
            InstanceId: CodingInstance, LeaseId: lease.LeaseId, Sequence: 1));
        var logBytes = await File.ReadAllBytesAsync(logPath);
        var logSha = Sha256(logBytes);
        await _api.IngestArtifactAsync(run.RunId, new ArtifactIngestRequest(
            "artifact-" + run.RunId, "runner.log", "text/plain", Convert.ToBase64String(logBytes),
            logSha, "scenario-runner-log:" + run.RunId, lease.Fence, CodingRunner, CodingInstance, lease.LeaseId, 2));
        var envelope = new ImmutableResultEnvelope(
            ResultRepositoryId, run.RunId, Required(_baseSha), _resultSha,
            FencedGitRefs.ImmutableResult(run.RunId, lease.Fence, _resultSha), null,
            Sha256(Encoding.UTF8.GetBytes("runner.log:" + logSha)),
            RepositoryUrl: new Uri(_repository).AbsoluteUri);
        var digest = ResultEnvelopeDigest.Compute(envelope);
        await _api.HandoffAsync(run.RunId, new ResultHandoffRequest(
            CodingRunner, CodingInstance, lease.LeaseId, lease.Fence, 3,
            "scenario-handoff:" + run.RunId, digest, envelope));
        await _api.CompleteRunAsync(run.RunId, new CompleteRunRequest(
            CodingRunner, CodingInstance, lease.LeaseId, lease.Fence, "success",
            "fake coding CLI passed", digest, "scenario-completion:" + run.RunId, 4));
        _task = await _api.GetTaskAsync(Required(_project).ProjectId, Required(_task).TaskId);
        Require(_task.State == "4-auto-review", $"Completed coding run ended in '{_task.State}'.");
        _run.Evidence("coding-history.json", await _api.GetHistoryAsync(Required(_project).ProjectId, _task.TaskId));
    }

    private async Task AutoReviewAsync()
    {
        var reviewLog = await ProcessAsync(
            "sh",
            [Path.Combine(_options.DefinitionDirectory, _definition.Fixture.FakeReviewCli)],
            _repository,
            allowFailure: true);
        var logPath = _run.EvidencePath("review.log");
        await File.WriteAllTextAsync(logPath, reviewLog.Output);
        Require(reviewLog.ExitCode == 0, "Fake review CLI failed: " + reviewLog.Output);

        await _api.RegisterRunnerAsync(
            ReviewRunner,
            new RegisterRunnerRequest(
                ReviewRunner, "scenario-review-host", ReviewInstance, "scenario-v1",
                TaskServerProtocol.Current,
                [ReviewCapabilities.ReviewExecutor, ReviewCapabilities.GitMaterialization]));
        var resultSha = Required(_resultSha);
        var command = new ReviewCommandDto("fake-review", "build-tests", "sh", ["fake-review-cli.sh"]);
        var subject = await _api.CreateReviewSubjectAsync(new CreateReviewSubjectRequest(
            Required(_task).TaskId,
            Required(Required(_claim).Run).RunId,
            ResultRepositoryId,
            new Uri(_repository).AbsoluteUri,
            resultSha,
            FencedGitRefs.ImmutableResult(
                Required(Required(_claim).Run).RunId,
                Required(Required(_claim).Lease).Fence,
                resultSha),
            null,
            null,
            "scenario-host",
            "deployment-scenario-policy-v1",
            new ReviewPlanDto([command], ["build-tests"]),
            "deployment-scenario-review-subject:" + Required(_task).TaskId));
        _reviewClaim = await _api.ClaimReviewAsync(new ReviewClaimRequest(ReviewRunner, ReviewInstance));
        Require(_reviewClaim.Status == "claimed", "Fake review was not claimed.");
        var lease = Required(_reviewClaim.Lease);
        var attempt = Required(_reviewClaim.Attempt);
        var evidence = new ReviewCommandEvidenceDto(
            command.StepId, command.Aspect, command.FileName, command.Arguments,
            resultSha, resultSha, resultSha,
            DateTime.Parse(_definition.ClockUtc).AddSeconds(-1), DateTime.Parse(_definition.ClockUtc),
            0, null, Sha256(Encoding.UTF8.GetBytes(reviewLog.Output)), Sha256([]),
            ExecutionLocation: "remote",
            ExecutorId: ReviewRunner,
            HostId: lease.HostId,
            AttemptId: attempt.AttemptId);
        var workspacePath = $"/review/{lease.ResourceNamespace}";
        var stdoutSha = evidence.StdoutSha256;
        var stderrSha = evidence.StderrSha256;
        _reviewReport = await _api.ReportReviewAsync(attempt.AttemptId, new ReviewReportRequest(
            ReviewRunner, ReviewInstance, lease.LeaseId, lease.Fence,
            "deployment-scenario-review-report:" + attempt.AttemptId, "Pass", null, "Fake review CLI passed.",
            new ReviewWorkspaceProofDto(
                ResultRepositoryId, resultSha, resultSha, resultSha, false, false,
                Sha256(Encoding.UTF8.GetBytes(workspacePath)), lease.ResourceNamespace),
            new ReviewEnvironmentDto(
                lease.HostId, ReviewRunner, ReviewInstance, Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.Version.ToString(),
                new Dictionary<string, string>
                {
                    ["runtime"] = Environment.Version.ToString(),
                    ["git"] = "git;sha256=" + Sha256(Encoding.UTF8.GetBytes("git")),
                    ["command:fake-review"] = "sh;sha256=" + Sha256(Encoding.UTF8.GetBytes("sh")),
                },
                new Dictionary<string, string>
                {
                    ["workspace"] = workspacePath,
                    ["cache"] = workspacePath + "/cache",
                    ["temp"] = workspacePath + "/tmp",
                    ["ports"] = $"{lease.PortBase}-{lease.PortBase + 7}",
                    ["containers"] = lease.ResourceNamespace,
                    ["databases"] = lease.ResourceNamespace,
                    ["credentials"] = "review-read-only",
                }),
            [evidence],
            [
                new ReviewArtifactEvidenceDto("fake-review.stdout.log", "text/plain", stdoutSha, 1),
                new ReviewArtifactEvidenceDto("fake-review.stderr.log", "text/plain", stderrSha, 0),
            ],
            [new ReviewVerdictDto("build-tests", "pass", "Verified", "Fixture test passed.")]));
        Require(_reviewReport.Outcome == "Pass", $"Review outcome was '{_reviewReport.Outcome}'.");
        if (_options.Target == "remote")
        {
            _task = await _api.GetTaskAsync(Required(_project).ProjectId, Required(_task).TaskId);
            _task = await _api.UpdateTaskAsync(Required(_project).ProjectId, _task.TaskId,
                new UpdateTaskRequest(null, null, "5-human-review", _task.Version));
        }
        await _api.CleanupReviewAsync(attempt.AttemptId, new ReviewCleanupRequest(
            ReviewRunner, ReviewInstance, lease.LeaseId, lease.Fence,
            "deployment-scenario-review-cleanup:" + attempt.AttemptId, true));
        if (_options.Target != "remote")
            _orchestration = (await _api.ListOrchestrationRunsAsync(Required(_project).ProjectId, "pending")).Single();
        _run.Evidence("review-report.json", new { subject, claim = _reviewClaim, report = _reviewReport });
    }

    private async Task IntegrateAsync()
    {
        var resultSha = Required(_resultSha);
        await GitAsync("checkout", "main");
        await GitAsync("merge", "--ff-only", resultSha);
        var ancestor = await GitAsync(["merge-base", "--is-ancestor", resultSha, "main"], allowFailure: true);
        Require(ancestor.ExitCode == 0, "Reviewed result is not integrated into main.");
        _run.Evidence("integration.json", new { branch = "main", resultSha });
    }

    private async Task CompleteAsync()
    {
        if (_options.Target == "remote")
        {
            _task = await _api.GetTaskAsync(Required(_project).ProjectId, Required(_task).TaskId);
            _task = await _api.UpdateTaskAsync(Required(_project).ProjectId, _task.TaskId,
                new UpdateTaskRequest(null, null, "6-completed", _task.Version));
            Require(_task.State == "6-completed", "Remote task did not reach Completed.");
            _run.Evidence("orchestration.json", new
            {
                status = "shared-target-safe",
                reason = "Review authority was settled without claiming unrelated orchestration work.",
            });
            return;
        }
        var orchestration = Required(_orchestration);
        while (orchestration.Status == "pending")
        {
            var claim = await _api.ClaimOrchestrationAsync(new OrchestrationClaimRequest(
                "scenario-engine", "scenario-engine-instance", [orchestration.CurrentStage]));
            var lease = Required(claim.Lease);
            var action = orchestration.CurrentStage == OrchestrationStage.CompletionJudge
                ? OrchestrationAction.Complete
                : OrchestrationAction.Continue;
            orchestration = await _api.CompleteOrchestrationAsync(orchestration.RunId,
                new CompleteOrchestrationStageRequest(
                    "scenario-engine", "scenario-engine-instance", lease.LeaseId, lease.Fence,
                    orchestration.CurrentStage, action, "{}",
                    $"scenario-orchestration-{orchestration.CurrentStage}"));
        }
        _orchestration = orchestration;
        _task = await _api.GetTaskAsync(Required(_project).ProjectId, Required(_task).TaskId);
        Require(_task.State == "5-human-review", "Completed orchestration did not reach Human Review.");
        _task = await _api.UpdateTaskAsync(Required(_project).ProjectId, _task.TaskId,
            new UpdateTaskRequest(null, null, "6-completed", _task.Version));
        Require(_task.State == "6-completed", "Task did not reach Completed.");
        _run.Evidence("orchestration.json", orchestration);
    }

    private async Task OrchestratorChatAsync()
    {
        var project = Required(_project);
        var task = Required(_task);
        await _api.EnsureContextAsync(project.Name, task.TaskKey);
        var userTurnId = "scenario-user-turn-" + task.TaskId;
        await _api.AppendContextTurnAsync(project.Name, task.TaskKey,
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                userTurnId,
                DateTime.Parse(_definition.ClockUtc),
                "user",
                "Confirm the deployment result.")));
        var receipt = new OrchestratorContextReceiptDto(
            "scenario-context-receipt", userTurnId, $"task:{project.ProjectId}:{task.TaskId}",
            DateTime.Parse(_definition.ClockUtc),
            new OrchestratorContextBudgetReceiptDto(500, 1000, 1500, 4),
            [new OrchestratorContextSourceReceiptDto(
                "scenario-task", "task", task.Version.ToString(), Sha256(Encoding.UTF8.GetBytes(task.TaskId)),
                "fixed", task.Title.Length, 4, "included")]);
        await _api.AppendContextTurnAsync(project.Name, task.TaskKey,
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                "scenario-orchestrator-turn-" + task.TaskId,
                DateTime.Parse(_definition.ClockUtc),
                "orchestrator",
                "The deterministic deployment result is complete.",
                Receipt: receipt)));
        var transcript = await _api.ReadContextAsync(project.Name, task.TaskKey);
        _chatReceiptCount = transcript.Turns.Count(turn => turn.Receipt is not null);
        Require(_chatReceiptCount == 1,
            "Orchestrator transcript did not preserve exactly one context receipt.");
        _run.Evidence("orchestrator-transcript.json", transcript);
    }

    private async Task DossierDecisionAsync()
    {
        var dossier = Required(_dossier);
        dossier = await _api.UpdateTaskAsync(Required(_project).ProjectId, dossier.TaskId,
            new UpdateTaskRequest(null, "decisionGate=approved; decision=ship", "6-completed", dossier.Version));
        _dossier = dossier;
        Require(dossier.State == "6-completed" && dossier.Body?.Contains("decision=ship", StringComparison.Ordinal) == true,
            "Dossier decision was not recorded.");
        _run.Evidence("dossier-decision.json", dossier);
    }

    private async Task BackupAsync()
    {
        _inventoryBefore = await InventoryHashAsync();
        _backup = await _api.BackupAsync(new BackupRequest("deployment-scenario"));
        Require(_backup.Sha256.Length == 64, "Backup did not return a SHA-256 digest.");
        _run.Evidence("backup.json", new { backup = _backup, inventorySha256 = _inventoryBefore });
    }

    private async Task RestoreAsync()
    {
        _restoreResult = await _api.RestoreAsync(Required(_backup));
        Require(_restoreResult.Verified, "Backup restore verification failed.");
        _run.Evidence("restore.json", _restoreResult);
    }

    private async Task InventoryEqualityAsync()
    {
        _inventoryAfter = await InventoryHashAsync();
        Require(_inventoryAfter == Required(_inventoryBefore),
            $"Inventory changed across backup/restore: {_inventoryBefore} != {_inventoryAfter}.");
        _run.Evidence("inventory-equality.json", new { before = _inventoryBefore, after = _inventoryAfter });
        if (_options.Target == "remote")
            await CleanupRemoteAsync();
    }

    private async Task<string> InventoryHashAsync()
    {
        var workspace = Required(_workspace);
        var project = Required(_project);
        var tasks = (await _api.ListTasksAsync(project.ProjectId))
            .OrderBy(item => item.TaskKey, StringComparer.Ordinal)
            .Select(item => new { item.TaskId, item.TaskKey, item.Title, item.Body, item.State, item.Version })
            .ToArray();
        var inventory = new
        {
            workspace = new { workspace.WorkspaceId, workspace.Name, workspace.Version },
            project = new { project.ProjectId, project.Name, project.TaskKeyPrefix, project.Version },
            tasks,
        };
        return Sha256(JsonSerializer.SerializeToUtf8Bytes(inventory, DeploymentScenarioProgramJson.Options));
    }

    private async Task CleanupRemoteAsync()
    {
        var project = Required(_project);
        foreach (var task in await _api.ListTasksAsync(project.ProjectId))
        {
            if (task.State == "7-archive")
                continue;
            await _api.UpdateTaskAsync(project.ProjectId, task.TaskId,
                new UpdateTaskRequest(null, null, "7-archive", task.Version));
        }
    }

    private async Task AssertDefinitionAsync(string stepId)
    {
        var step = _definition.Steps.Single(item => item.Id == stepId);
        var results = new List<object>();
        foreach (var assertion in step.Assertions)
        {
            var actual = await ResolveAssertionSubjectAsync(assertion.Subject);
            var passed = assertion.Type switch
            {
                "resource.count" => Convert.ToInt64(actual) == assertion.Expected.GetInt64(),
                "number.minimum" => Convert.ToInt64(actual) >= assertion.Expected.GetInt64(),
                "file.exists" or "field.equals" when assertion.Expected.ValueKind is JsonValueKind.True or JsonValueKind.False
                    => Convert.ToBoolean(actual) == assertion.Expected.GetBoolean(),
                _ => string.Equals(Convert.ToString(actual), assertion.Expected.GetString(), StringComparison.Ordinal),
            };
            if (!passed)
                throw new InvalidOperationException(
                    $"Typed assertion {assertion.Type} failed for {assertion.Subject}: " +
                    $"expected {assertion.Expected}, actual {actual}.");
            results.Add(new { assertion.Type, assertion.Subject, expected = assertion.Expected, actual, status = "passed" });
        }
        _run.Evidence("assertions.json", results);
    }

    private async Task<object> ResolveAssertionSubjectAsync(string subject) => subject switch
    {
        "fixture.tasks" => (await _api.ListTasksAsync(Required(_project).ProjectId)).Count,
        "fixture.repository" => (await ProcessAsync("node", ["--test", "test.mjs"], _repository, allowFailure: true)).ExitCode == 0
            ? "passing"
            : "failing",
        "runner.status" => Required(_runner).Status,
        "task.state" => Required(_task).State,
        "claim.status" => Required(_claim).Status,
        "claim.fence" => Required(Required(_claim).Lease).Fence,
        "result.sha" => string.IsNullOrWhiteSpace(_resultSha) ? "missing" : "present",
        "runner.log" => File.Exists(_run.EvidencePath("runner.log")),
        "review.outcome" => Required(_reviewReport).Outcome,
        "review.log" => File.Exists(_run.EvidencePath("review.log")),
        "result.sha:main" => (await GitAsync(["merge-base", "--is-ancestor", Required(_resultSha), "main"], true)).ExitCode == 0
            ? "main"
            : "missing",
        "chat.receipts" => _chatReceiptCount,
        "dossier.state" => Required(_dossier).State,
        "backup.sha256" => Required(_backup).Sha256.Length == 64 ? "valid" : "invalid",
        "restore.verified" => Required(_restoreResult).Verified,
        "inventory.sha256" => _inventoryBefore == _inventoryAfter ? "pre-backup" : "mismatch",
        _ => throw new InvalidDataException($"No value resolver exists for typed assertion subject '{subject}'."),
    };

    private async Task<ProcessResult> GitAsync(params string[] arguments)
        => await GitAsync(arguments, false);

    private async Task<ProcessResult> GitAsync(string[] arguments, bool allowFailure)
        => await ProcessAsync("git", arguments, _repository, allowFailure);

    private static async Task<ProcessResult> ProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool allowFailure = false)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout) + (await stderr);
        if (process.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"{fileName} exited {process.ExitCode}: {output}");
        return new ProcessResult(process.ExitCode, output);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static T Required<T>(T? value) where T : class
        => value ?? throw new InvalidOperationException($"Required {typeof(T).Name} was not initialized.");

    private static string Required(string? value)
        => value ?? throw new InvalidOperationException("Required scenario value was not initialized.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        if (_options.Target == "remote" && _project is not null && _inventoryAfter is null)
        {
            try
            {
                await CleanupRemoteAsync();
            }
            catch
            {
                // Preserve the original step failure. The target report retains
                // the scenario project identity for an operator cleanup retry.
            }
        }
        await _api.DisposeAsync();
        try
        {
            Directory.Delete(_workingRoot, recursive: true);
        }
        catch (IOException)
        {
            // The OS can retain transient process handles. The scenario result is already durable.
        }
    }
}

internal static class DeploymentScenarioProgramJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

internal sealed record ProcessResult(int ExitCode, string Output);

internal sealed record ScenarioDefinition(
    int SchemaVersion,
    string Id,
    string ClockUtc,
    int PollIntervalMilliseconds,
    int PollTimeoutSeconds,
    int SmokeStepCount,
    ScenarioFixture Fixture,
    IReadOnlyList<ScenarioStep> Steps);

internal sealed record ScenarioFixture(
    string ProjectName,
    string TaskPrefix,
    string Repository,
    string FakeCodingCli,
    string FakeReviewCli);

internal sealed record ScenarioStep(
    string Id,
    string Title,
    IReadOnlyList<TypedAssertion> Assertions);

internal sealed record TypedAssertion(string Type, string Subject, JsonElement Expected);

internal sealed record ScenarioOptions(
    string Target,
    string Level,
    string DefinitionPath,
    string OutputDirectory,
    string? BaseUrl,
    string? AuthToken)
{
    public string DefinitionDirectory => Path.GetDirectoryName(DefinitionPath)!;

    public static ScenarioOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Arguments use --name value pairs.");
            values[args[i][2..]] = args[i + 1];
        }
        var target = values.GetValueOrDefault("target") ?? "inproc";
        var level = values.GetValueOrDefault("level") ?? "smoke";
        if (target is not ("inproc" or "compose" or "remote"))
            throw new ArgumentException("--target must be inproc, compose, or remote.");
        if (level is not ("smoke" or "full"))
            throw new ArgumentException("--level must be smoke or full.");
        var definition = Path.GetFullPath(values.GetValueOrDefault("definition") ??
            Path.Combine(AppContext.BaseDirectory, "definition.json"));
        var output = Path.GetFullPath(values.GetValueOrDefault("output") ??
            Path.Combine(Environment.CurrentDirectory, "scenario-results"));
        var url = values.GetValueOrDefault("url");
        if (target != "inproc" && string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("--url is required for compose and remote targets.");
        var token = values.GetValueOrDefault("token") ?? Environment.GetEnvironmentVariable("SCENARIO_AUTH_TOKEN");
        return new ScenarioOptions(target, level, definition, output, url?.TrimEnd('/'), token);
    }
}

internal sealed class ScenarioRun
{
    private readonly ScenarioDefinition _definition;
    private readonly ScenarioOptions _options;
    private readonly IReadOnlyList<ScenarioStep> _selected;
    private readonly List<StepResult> _results = [];
    private string? _activeStep;

    public ScenarioRun(ScenarioDefinition definition, ScenarioOptions options, IReadOnlyList<ScenarioStep> selected)
    {
        _definition = definition;
        _options = options;
        _selected = selected;
    }

    public bool Passed => _results.Count == _selected.Count && _results.All(item => item.Status == "passed");

    public async Task RecordAsync(ScenarioStep step, Func<Task> action)
    {
        _activeStep = step.Id;
        var timer = Stopwatch.StartNew();
        try
        {
            await action();
            _results.Add(new StepResult(step.Id, step.Title, "passed", timer.ElapsedMilliseconds,
                $"evidence/{step.Id}/", null));
        }
        catch (Exception exception)
        {
            _results.Add(new StepResult(step.Id, step.Title, "failed", timer.ElapsedMilliseconds,
                $"evidence/{step.Id}/", exception.Message));
            throw;
        }
        finally
        {
            _activeStep = null;
        }
    }

    public void RecordHarnessFailure(Exception exception)
    {
        if (_activeStep is not null || _results.Any(item => item.Status == "failed"))
            return;
        _results.Add(new StepResult("scenario-harness", "Scenario harness", "failed", 0, "", exception.Message));
    }

    public string EvidencePath(string fileName)
    {
        var step = _activeStep ?? throw new InvalidOperationException("Evidence can only be recorded during a step.");
        var directory = Path.Combine(_options.OutputDirectory, "evidence", step);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }

    public void Evidence(string fileName, object value)
        => File.WriteAllText(EvidencePath(fileName), JsonSerializer.Serialize(value, DeploymentScenarioProgramJson.Options));

    public async Task WriteReportsAsync()
    {
        foreach (var step in _selected.Where(step => _results.All(result => result.Id != step.Id)))
            _results.Add(new StepResult(step.Id, step.Title, "skipped", 0, "", "A previous step failed."));
        var report = new StringBuilder()
            .AppendLine("# Deployment regression scenario")
            .AppendLine()
            .AppendLine($"- Scenario: `{_definition.Id}`")
            .AppendLine($"- Target: `{_options.Target}`")
            .AppendLine($"- Level: `{_options.Level}`")
            .AppendLine($"- Status: `{(Passed ? "passed" : "failed")}`")
            .AppendLine()
            .AppendLine("| Step | Status | Duration | Evidence |")
            .AppendLine("|---|---:|---:|---|");
        foreach (var result in _results)
        {
            var evidence = string.IsNullOrEmpty(result.Evidence)
                ? ""
                : $"[files]({result.Evidence})";
            report.AppendLine($"| {result.Title} | {result.Status} | {result.DurationMilliseconds} ms | {evidence} |");
            if (result.Error is not null)
                report.AppendLine($"| ↳ error | `{result.Error.Replace("|", "\\|")}` | | | ");
        }
        await File.WriteAllTextAsync(Path.Combine(_options.OutputDirectory, "scenario-report.md"), report.ToString());

        var suite = new XElement("testsuite",
            new XAttribute("name", _definition.Id),
            new XAttribute("tests", _results.Count),
            new XAttribute("failures", _results.Count(item => item.Status == "failed")),
            new XAttribute("skipped", _results.Count(item => item.Status == "skipped")),
            new XAttribute("time", _results.Sum(item => item.DurationMilliseconds) / 1000d),
            new XAttribute("target", _options.Target),
            new XAttribute("level", _options.Level));
        foreach (var result in _results)
        {
            var test = new XElement("testcase",
                new XAttribute("classname", $"deployment.{_options.Target}.{_options.Level}"),
                new XAttribute("name", result.Id),
                new XAttribute("time", result.DurationMilliseconds / 1000d));
            if (result.Error is not null)
            {
                if (result.Status == "skipped")
                    test.Add(new XElement("skipped", new XAttribute("message", result.Error)));
                else
                    test.Add(new XElement("failure", new XAttribute("message", result.Error), result.Error));
            }
            test.Add(new XElement("system-out", result.Evidence));
            suite.Add(test);
        }
        await File.WriteAllTextAsync(
            Path.Combine(_options.OutputDirectory, "scenario-junit.xml"),
            new XDocument(new XDeclaration("1.0", "utf-8", null), suite).ToString());
    }
}

internal sealed record StepResult(
    string Id,
    string Title,
    string Status,
    long DurationMilliseconds,
    string Evidence,
    string? Error);
