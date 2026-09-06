extern alias taskserver;

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using TaskServerProgram = taskserver::Program;

try
{
    return await DeploymentScenarioProgram.RunAsync(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"scenario configuration error: {exception.Message}");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"scenario infrastructure error: {exception.GetBaseException().Message}");
    return 1;
}

internal static class DeploymentScenarioProgram
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var options = Options.Parse(args);
        var definitionPath = Path.Combine(AppContext.BaseDirectory, "deployment-scenario.json");
        var definition = JsonSerializer.Deserialize<ScenarioDefinition>(
            await File.ReadAllTextAsync(definitionPath), Json)
            ?? throw new InvalidOperationException("The deployment scenario definition is empty.");
        definition.Validate(options.Level);
        Directory.CreateDirectory(options.ResultsDirectory);

        ScenarioTarget target;
        try
        {
            target = await ScenarioTarget.CreateAsync(options);
        }
        catch (Exception exception)
        {
            var message = exception.GetBaseException().Message;
            var selected = definition.Steps.Take(definition.Levels[options.Level]).ToArray();
            var steps = selected.Select((step, index) => new StepResult(
                step.Id,
                step.Title,
                index == 0 ? "failed" : "not-run",
                TimeSpan.Zero,
                index == 0 ? "bootstrap-principals.json" : null,
                index == 0 ? message : "The target readiness check failed.")).ToList();
            await File.WriteAllTextAsync(
                Path.Combine(options.ResultsDirectory, "bootstrap-principals.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    scenario = definition.Name,
                    target = options.Target,
                    level = options.Level,
                    step = "bootstrap-principals",
                    observations = new { error = message },
                    assertions = selected[0].Assertions,
                }, Json) + "\n");
            await ScenarioReport.WriteAsync(new ScenarioResult(
                definition.Name, options.Target, options.Level, false, TimeSpan.Zero, steps), options.ResultsDirectory);
            Console.Error.WriteLine($"[scenario] target readiness failed: {message}");
            return 1;
        }

        await using var ownedTarget = target;
        var runner = new ScenarioRunner(definition, options, target.Client);
        var result = await runner.RunAsync();
        await ScenarioReport.WriteAsync(result, options.ResultsDirectory);
        await runner.CleanupAsync();
        return result.Passed ? 0 : 1;
    }

    internal sealed record Options(
        string Target,
        string Level,
        string ResultsDirectory,
        Uri? BaseUrl,
        string? AuthToken,
        Uri? UiUrl,
        string RunId)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Length; index++)
            {
                if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                    throw new ArgumentException($"Invalid scenario argument '{args[index]}'.");
                values[args[index][2..]] = args[++index];
            }

            var target = Value("target", "SCENARIO_TARGET", "inproc");
            var level = Value("level", "SCENARIO_LEVEL", "smoke");
            if (target is not ("inproc" or "compose" or "remote"))
                throw new ArgumentException("--target must be inproc, compose, or remote.");
            if (level is not ("smoke" or "full"))
                throw new ArgumentException("--level must be smoke or full.");
            var results = Path.GetFullPath(Value(
                "results",
                "SCENARIO_RESULTS_DIR",
                Path.Combine(FindRepositoryRoot(), "results", "deployment-scenario")));
            var baseUrlText = Value("url", "SCENARIO_BASE_URL", "");
            if (target != "inproc" && string.IsNullOrWhiteSpace(baseUrlText))
                throw new ArgumentException("SCENARIO_BASE_URL or --url is required outside the inproc target.");
            var uiUrlText = Environment.GetEnvironmentVariable("SCENARIO_UI_URL");
            var runId = Value("run-id", "SCENARIO_RUN_ID", target == "remote"
                ? DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                : "fixed");
            return new Options(
                target,
                level,
                results,
                string.IsNullOrWhiteSpace(baseUrlText) ? null : new Uri(baseUrlText),
                Environment.GetEnvironmentVariable("SCENARIO_AUTH_TOKEN"),
                string.IsNullOrWhiteSpace(uiUrlText) ? null : new Uri(uiUrlText),
                Sanitize(runId));

            string Value(string key, string environment, string fallback)
                => values.TryGetValue(key, out var value)
                    ? value
                    : Environment.GetEnvironmentVariable(environment) ?? fallback;
        }

        private static string Sanitize(string value)
        {
            var result = new string(value.Where(char.IsLetterOrDigit).Take(24).ToArray()).ToLowerInvariant();
            return result.Length == 0 ? "run" : result;
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "agent-taskboard.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? Environment.CurrentDirectory;
        }
    }

    private sealed class ScenarioTarget : IAsyncDisposable
    {
        private readonly WebApplicationFactory<TaskServerProgram>? _factory;
        private readonly string? _temporaryDirectory;

        private ScenarioTarget(HttpClient client, WebApplicationFactory<TaskServerProgram>? factory, string? temporaryDirectory)
        {
            Client = client;
            _factory = factory;
            _temporaryDirectory = temporaryDirectory;
        }

        public HttpClient Client { get; }

        public static async Task<ScenarioTarget> CreateAsync(Options options)
        {
            if (options.Target == "inproc")
            {
                var temporaryDirectory = Path.Combine(
                    Path.GetTempPath(), "agent-studio-deployment-scenario-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);
                var factory = new WebApplicationFactory<TaskServerProgram>().WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["STORE_PATH"] = Path.Combine(temporaryDirectory, "store"),
                            ["BACKUP_PATH"] = Path.Combine(temporaryDirectory, "backups"),
                            ["AUTH"] = "none",
                            ["TaskServer:ResultRefGcEnabled"] = "false",
                            ["TaskServer:MinimumLeaseSeconds"] = "5",
                        }));
                });
                var client = factory.CreateClient(new WebApplicationFactoryClientOptions
                {
                    AllowAutoRedirect = false,
                    BaseAddress = new Uri("http://localhost"),
                });
                ConfigureClient(client, options.AuthToken);
                await ReadyAsync(client);
                return new ScenarioTarget(client, factory, temporaryDirectory);
            }

            var remoteClient = new HttpClient
            {
                BaseAddress = options.BaseUrl,
                Timeout = TimeSpan.FromSeconds(30),
            };
            ConfigureClient(remoteClient, options.AuthToken);
            await ReadyAsync(remoteClient);
            return new ScenarioTarget(remoteClient, null, null);
        }

        private static void ConfigureClient(HttpClient client, string? token)
        {
            client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString(CultureInfo.InvariantCulture));
            client.DefaultRequestHeaders.Add(TaskServerProtocol.ClientVersionHeaderName, "deployment-scenario/1");
            client.DefaultRequestHeaders.Add("X-Actor-Id", "deployment-scenario");
            if (!string.IsNullOrWhiteSpace(token))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        private static async Task ReadyAsync(HttpClient client)
        {
            Exception? lastError = null;
            for (var attempt = 1; attempt <= 150; attempt++)
            {
                try
                {
                    using var response = await client.GetAsync("/api/v1/protocol");
                    if (response.IsSuccessStatusCode)
                        return;
                    lastError = new InvalidOperationException($"HTTP {(int)response.StatusCode}");
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    lastError = exception;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
            throw new InvalidOperationException($"Scenario target is not ready: {lastError?.Message}");
        }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            _factory?.Dispose();
            if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
                TryDeleteDirectory(_temporaryDirectory);
            return ValueTask.CompletedTask;
        }

        private static void TryDeleteDirectory(string path)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    if (attempt < 4) Thread.Sleep(50);
                    else return;
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt < 4) Thread.Sleep(50);
                    else return;
                }
            }
        }
    }

    private sealed class ScenarioRunner
    {
        private static readonly DateTime FixedClock = DateTime.Parse(
            "2026-09-06T12:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        private readonly ScenarioDefinition _definition;
        private readonly Options _options;
        private readonly HttpClient _client;
        private readonly string _suffix;
        private readonly List<StepResult> _results = [];
        private WorkspaceDto? _workspace;
        private ProjectDto? _project;
        private TaskDto? _task;
        private TaskDto? _dossierTask;
        private ClaimResponse? _claim;
        private string? _repositoryPath;
        private string? _resultSha;
        private string? _treeSha;
        private string? _envelopeDigest;
        private BackupResult? _backup;
        private string? _inventoryBefore;

        public ScenarioRunner(ScenarioDefinition definition, Options options, HttpClient client)
        {
            _definition = definition;
            _options = options;
            _client = client;
            _suffix = options.Target == "remote" ? "_" + options.RunId : "";
        }

        public async Task<ScenarioResult> RunAsync()
        {
            var take = _definition.Levels[_options.Level];
            var started = Stopwatch.StartNew();
            var failed = false;
            foreach (var step in _definition.Steps.Take(take))
            {
                if (failed)
                {
                    _results.Add(new StepResult(step.Id, step.Title, "not-run", TimeSpan.Zero, null, "A previous step failed."));
                    continue;
                }

                var timer = Stopwatch.StartNew();
                var evidenceName = $"{step.Id}.json";
                try
                {
                    var observations = await ExecuteAsync(step.Id);
                    Assert(step, observations);
                    await WriteEvidenceAsync(evidenceName, step, observations);
                    _results.Add(new StepResult(step.Id, step.Title, "passed", timer.Elapsed, evidenceName, null));
                    Console.WriteLine($"[scenario] passed {step.Id} ({timer.Elapsed.TotalMilliseconds:F0} ms)");
                }
                catch (Exception exception)
                {
                    failed = true;
                    var error = Unwrap(exception);
                    await WriteEvidenceAsync(evidenceName, step, new Dictionary<string, string> { ["error"] = error });
                    _results.Add(new StepResult(step.Id, step.Title, "failed", timer.Elapsed, evidenceName, error));
                    Console.Error.WriteLine($"[scenario] failed {step.Id}: {error}");
                }
            }

            return new ScenarioResult(
                _definition.Name,
                _options.Target,
                _options.Level,
                !failed,
                started.Elapsed,
                _results);
        }

        public async Task CleanupAsync()
        {
            if (_options.Target == "remote" && _project is not null)
            {
                foreach (var task in new[] { _task, _dossierTask }.Where(item => item is not null))
                {
                    try
                    {
                        var current = await GetAsync<TaskDto>($"/api/v1/projects/{_project.ProjectId}/tasks/{task!.TaskId}");
                        if (current.State != "7-archive")
                            await PutAsync<TaskDto>(
                                $"/api/v1/projects/{_project.ProjectId}/tasks/{current.TaskId}",
                                new UpdateTaskRequest(null, null, "7-archive", current.Version));
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine($"[scenario] cleanup warning for {task!.TaskKey}: {Unwrap(exception)}");
                    }
                }
            }
            if (_repositoryPath is not null && Directory.Exists(_repositoryPath))
            {
                try { Directory.Delete(_repositoryPath, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"[scenario] cleanup warning for fixture repository: {Unwrap(exception)}");
                }
            }
        }

        private Task<Dictionary<string, string>> ExecuteAsync(string id) => id switch
        {
            "bootstrap-principals" => BootstrapAsync(),
            "register-runner" => RegisterRunnerAsync(),
            "create-task" => CreateTaskAsync(),
            "claim" => ClaimAsync(),
            "fake-cli-run" => FakeCliAsync(),
            "handoff-run" => HandoffAsync(),
            "auto-review" => AutoReviewAsync(),
            "integrate" => IntegrateAsync(),
            "complete" => CompleteAsync(),
            "orchestrator-chat" => OrchestratorChatAsync(),
            "dossier-decision" => DossierDecisionAsync(),
            "backup" => BackupAsync(),
            "restore" => RestoreAsync(),
            "inventory-hash" => InventoryHashAsync(),
            _ => throw new InvalidOperationException($"Unknown deployment scenario step '{id}'."),
        };

        private async Task<Dictionary<string, string>> BootstrapAsync()
        {
            var protocol = await GetAsync<ProtocolRangeDto>("/api/v1/protocol");
            if (_options.UiUrl is not null)
            {
                using var ui = new HttpClient { BaseAddress = _options.UiUrl, Timeout = TimeSpan.FromSeconds(10) };
                var health = await ui.GetStringAsync("/healthz");
                var home = await ui.GetStringAsync("/");
                var tasks = await ui.GetStringAsync("/api/tasks/grouped");
                if (!health.Contains("ok", StringComparison.OrdinalIgnoreCase)
                    || !home.Contains("<app-root", StringComparison.Ordinal)
                    || !tasks.Contains("\"backlog\"", StringComparison.Ordinal))
                    throw new InvalidOperationException("The folded Compose browser/API smoke assertions failed.");
            }
            var observations = new Dictionary<string, string>
            {
                ["protocol"] = protocol.MaximumSupported >= TaskServerProtocol.Current ? "supported" : "unsupported",
            };
            var topologyLog = Path.Combine(_options.ResultsDirectory, "topology-harness.log");
            if (File.Exists(topologyLog))
                observations["topologyHarnessSha256"] = Hash(await File.ReadAllTextAsync(topologyLog));
            var topologyStatus = Environment.GetEnvironmentVariable("SCENARIO_TOPOLOGY_STATUS");
            if (topologyStatus is not null)
            {
                observations["topologyHarnessExitCode"] = topologyStatus;
                if (topologyStatus != "0")
                    throw new InvalidOperationException($"The reused TopologyTests harness exited {topologyStatus}.");
            }
            return observations;
        }

        private async Task<Dictionary<string, string>> RegisterRunnerAsync()
        {
            var id = "scenario-runner" + _suffix;
            var runner = await PutAsync<RunnerDto>($"/api/v1/runners/{id}", new RegisterRunnerRequest(
                id,
                "scenario-host" + _suffix,
                "scenario-instance" + _suffix,
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]));
            return new() { ["runnerStatus"] = runner.Status };
        }

        private async Task<Dictionary<string, string>> CreateTaskAsync()
        {
            var fixture = _definition.Fixture;
            _workspace = await PostAsync<WorkspaceDto>("/api/v1/workspaces", new CreateWorkspaceRequest(
                "Deployment scenario" + (_suffix.Length == 0 ? "" : " " + _options.RunId),
                fixture.WorkspaceId + _suffix));
            _project = await PostAsync<ProjectDto>("/api/v1/projects", new CreateProjectRequest(
                _workspace.WorkspaceId,
                fixture.ProjectName + (_suffix.Length == 0 ? "" : " " + _options.RunId),
                fixture.TaskKeyPrefix,
                fixture.ProjectId + _suffix));
            var body = JsonSerializer.Serialize(new
            {
                fixture.Epic,
                fixture.Dossier,
                fixture.DecisionGate,
                expected = "fake CLI changes the known failing fixture into the known passing fixture",
            }, Json);
            _task = await PostAsync<TaskDto>($"/api/v1/projects/{_project.ProjectId}/tasks", new CreateTaskRequest(
                "Prove the deployment path",
                body,
                "2-ready",
                "task_deployment_scenario" + _suffix,
                "DSC-1" + (_suffix.Length == 0 ? "" : "-" + _options.RunId)));
            _dossierTask = await PostAsync<TaskDto>($"/api/v1/projects/{_project.ProjectId}/tasks", new CreateTaskRequest(
                "Record the deployment dossier decision",
                JsonSerializer.Serialize(new { fixture.Dossier, fixture.DecisionGate, decision = "pending" }, Json),
                "0-backlog",
                "task_deployment_dossier" + _suffix,
                "DSC-2" + (_suffix.Length == 0 ? "" : "-" + _options.RunId)));
            var tasks = await GetAsync<List<TaskDto>>($"/api/v1/projects/{_project.ProjectId}/tasks");
            return new() { ["taskCount"] = tasks.Count.ToString(CultureInfo.InvariantCulture) };
        }

        private async Task<Dictionary<string, string>> ClaimAsync()
        {
            var id = "scenario-runner" + _suffix;
            _claim = await PostAsync<ClaimResponse>($"/api/v1/runners/{id}/claims", new ClaimRequest(
                id,
                "scenario-instance" + _suffix,
                RequestedTtlSeconds: 120));
            if (_claim.Run is null || _claim.Lease is null || _claim.Task?.TaskId != _task?.TaskId)
                throw new InvalidOperationException("The fake runner did not receive the seeded ready task.");
            return new() { ["claimStatus"] = _claim.Status, ["fence"] = _claim.Lease.Fence.ToString(CultureInfo.InvariantCulture) };
        }

        private async Task<Dictionary<string, string>> FakeCliAsync()
        {
            RequireClaim();
            var source = Path.Combine(AppContext.BaseDirectory, "fixture");
            _repositoryPath = Path.Combine(Path.GetTempPath(), "deployment-scenario-repository-" + Guid.NewGuid().ToString("N"));
            CopyDirectory(source, _repositoryPath);
            await GitAsync("init", "-b", "main");
            await GitAsync("config", "user.name", "Deployment Scenario");
            await GitAsync("config", "user.email", "scenario@example.invalid");
            await GitAsync("add", ".");
            await GitAsync("commit", "-m", "test: seed deterministic failing fixture");
            var actual = (await File.ReadAllTextAsync(Path.Combine(_repositoryPath, "app", "value.txt"))).Trim();
            var expected = (await File.ReadAllTextAsync(Path.Combine(_repositoryPath, "test", "expected.txt"))).Trim();
            if (actual == expected)
                throw new InvalidOperationException("The seeded fixture did not expose its known failure.");
            await File.WriteAllTextAsync(Path.Combine(_repositoryPath, "app", "value.txt"), expected + "\n");
            var log = "fake-cli: observed known failure\nfake-cli: wrote passing value\nfake-cli: test passed\n";
            await File.WriteAllTextAsync(Path.Combine(_repositoryPath, "fake-cli.log"), log);
            await GitAsync("add", ".");
            await GitAsync("commit", "-m", "fix: make deterministic deployment fixture pass");
            _resultSha = (await GitAsync("rev-parse", "HEAD")).Trim();
            _treeSha = (await GitAsync("rev-parse", "HEAD^{tree}")).Trim();
            return new()
            {
                ["knownFailure"] = "observed",
                ["knownPass"] = (await File.ReadAllTextAsync(Path.Combine(_repositoryPath, "app", "value.txt"))).Trim() == expected ? "observed" : "missing",
                ["resultSha"] = _resultSha,
                ["logSha256"] = Hash(log),
            };
        }

        private async Task<Dictionary<string, string>> HandoffAsync()
        {
            var (run, lease) = RequireClaim();
            var runnerId = "scenario-runner" + _suffix;
            var instanceId = "scenario-instance" + _suffix;
            var log = await File.ReadAllTextAsync(Path.Combine(_repositoryPath!, "fake-cli.log"));
            await PostAsync<EventDto>($"/api/v1/runs/{run.RunId}/events", new EventIngestRequest(
                "evt_fake_cli" + _suffix,
                LifecycleEventKinds.AgentMessage,
                JsonSerializer.Serialize(new { text = "fixed fake CLI completed", resultSha = _resultSha }, Json),
                "event:fake-cli:" + run.RunId,
                lease.Fence,
                FixedClock));
            await PostAsync<ArtifactDto>($"/api/v1/runs/{run.RunId}/artifacts", new ArtifactIngestRequest(
                "art_fake_cli" + _suffix,
                "results/fake-cli.log",
                "text/plain",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(log)),
                Hash(log),
                "artifact:fake-cli:" + run.RunId,
                lease.Fence));
            var envelope = new ImmutableResultEnvelope(
                "repo_deployment_fixture",
                run.RunId,
                (await GitAsync("rev-parse", "HEAD~1")).Trim(),
                _resultSha!,
                FencedGitRefs.ImmutableResult(run.RunId, lease.Fence, _resultSha!),
                null,
                Hash(log),
                RepositoryUrl: "https://example.invalid/deployment-scenario.git");
            _envelopeDigest = ResultEnvelopeDigest.Compute(envelope);
            await PutAsync<ResultHandoffAck>($"/api/v1/runs/{run.RunId}/result-handoff", new ResultHandoffRequest(
                runnerId,
                instanceId,
                lease.LeaseId,
                lease.Fence,
                1,
                "handoff:" + run.RunId,
                _envelopeDigest,
                envelope));
            await PostAsync<RunDto>($"/api/v1/runs/{run.RunId}/completion", new CompleteRunRequest(
                runnerId,
                instanceId,
                lease.LeaseId,
                lease.Fence,
                "success",
                "Fixed fake CLI completed and produced one commit.",
                _envelopeDigest,
                "completion:" + run.RunId,
                2));
            _task = await GetAsync<TaskDto>($"/api/v1/projects/{_project!.ProjectId}/tasks/{_task!.TaskId}");
            return new() { ["taskState"] = _task.State, ["resultSha"] = _resultSha! };
        }

        private async Task<Dictionary<string, string>> AutoReviewAsync()
        {
            var (run, _) = RequireClaim();
            var command = new ReviewCommandDto("verify-fixed-fixture", "build-tests", "fake-review-cli", ["verify"]);
            var subject = await PostAsync<ReviewSubjectDto>("/api/v1/reviews/subjects", new CreateReviewSubjectRequest(
                _task!.TaskId,
                run.RunId,
                "repo_deployment_fixture",
                "https://example.invalid/deployment-scenario.git",
                _resultSha!,
                FencedGitRefs.ImmutableResult(run.RunId, _claim!.Lease!.Fence, _resultSha!),
                null,
                null,
                "scenario-host" + _suffix,
                "deployment-scenario-policy-v1",
                new ReviewPlanDto([command], ["build-tests"]),
                "review-subject:" + run.RunId));
            var reviewerId = "scenario-reviewer" + _suffix;
            var reviewerInstance = "scenario-reviewer-instance" + _suffix;
            await PutAsync<RunnerDto>($"/api/v1/runners/{reviewerId}", new RegisterRunnerRequest(
                reviewerId,
                "scenario-review-host" + _suffix,
                reviewerInstance,
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.ReviewExecutor, ReviewCapabilities.GitMaterialization]));
            var review = await PostAsync<ReviewClaimResponse>($"/api/v1/runners/{reviewerId}/review-claims", new ReviewClaimRequest(
                reviewerId,
                reviewerInstance));
            if (review.Subject?.SubjectId != subject.SubjectId || review.Attempt is null || review.Lease is null)
                throw new InvalidOperationException("The fake review CLI did not receive the immutable subject.");
            var stdout = "fake-review-cli: fixed fixture passed\n";
            var stderr = string.Empty;
            var reviewWorkspace = $"/review/{review.Lease.ResourceNamespace}";
            var evidence = new ReviewCommandEvidenceDto(
                command.StepId,
                command.Aspect,
                command.FileName,
                command.Arguments,
                _resultSha!,
                _resultSha!,
                _treeSha!,
                FixedClock.AddSeconds(1),
                FixedClock.AddSeconds(2),
                0,
                null,
                Hash(stdout),
                Hash(stderr),
                ExecutionLocation: "remote",
                ExecutorId: reviewerId,
                HostId: review.Lease.HostId,
                AttemptId: review.Attempt.AttemptId);
            var report = await PostAsync<ReviewReportDto>($"/api/v1/reviews/attempts/{review.Attempt.AttemptId}/report", new ReviewReportRequest(
                reviewerId,
                reviewerInstance,
                review.Lease.LeaseId,
                review.Lease.Fence,
                "review-report:" + review.Attempt.AttemptId,
                "Pass",
                null,
                "The deterministic fixture passes.",
                new ReviewWorkspaceProofDto(
                    "repo_deployment_fixture", _resultSha!, _resultSha!, _treeSha!, false, false,
                    Hash(reviewWorkspace), review.Lease.ResourceNamespace),
                new ReviewEnvironmentDto(
                    review.Lease.HostId, reviewerId, reviewerInstance, "deterministic", "portable", "1",
                    new Dictionary<string, string>
                    {
                        ["runtime"] = ".NET 10",
                        ["git"] = "git;sha256=" + Hash("git"),
                        ["command:" + command.StepId] = command.FileName + ";sha256=" + Hash(command.FileName),
                    },
                    new Dictionary<string, string>
                    {
                        ["workspace"] = reviewWorkspace,
                        ["cache"] = $"{reviewWorkspace}/cache",
                        ["temp"] = $"{reviewWorkspace}/temp",
                        ["ports"] = $"{review.Lease.PortBase}-{review.Lease.PortBase + 7}",
                        ["containers"] = review.Lease.ResourceNamespace,
                        ["databases"] = review.Lease.ResourceNamespace,
                        ["credentials"] = "review-read-only",
                    }),
                [evidence],
                [
                    new ReviewArtifactEvidenceDto("verify.stdout.log", "text/plain", Hash(stdout), Encoding.UTF8.GetByteCount(stdout), Convert.ToBase64String(Encoding.UTF8.GetBytes(stdout))),
                    new ReviewArtifactEvidenceDto("verify.stderr.log", "text/plain", Hash(stderr), 0, Convert.ToBase64String(Encoding.UTF8.GetBytes(stderr))),
                ],
                [new ReviewVerdictDto("build-tests", "pass", "Verified", "The fixed review output passed.")]));
            var cleanup = await PostAsync<ReviewCleanupResponse>($"/api/v1/reviews/attempts/{review.Attempt.AttemptId}/cleanup", new ReviewCleanupRequest(
                reviewerId,
                reviewerInstance,
                review.Lease.LeaseId,
                review.Lease.Fence,
                "review-cleanup:" + review.Attempt.AttemptId,
                true));
            var runs = await GetAsync<List<OrchestrationRunDto>>($"/api/v1/orchestration/runs?projectId={_project!.ProjectId}");
            var orchestration = runs.SingleOrDefault(item => item.TaskId == _task.TaskId);
            if (orchestration is null)
            {
                if (cleanup.Status != "cleaned")
                    throw new InvalidOperationException($"Review cleanup ended in '{cleanup.Status}'.");
                var payload = new ReviewOrchestrationPayloadDto(
                    run.RunId,
                    subject.SubjectId,
                    review.Attempt.AttemptId,
                    _resultSha!,
                    subject.ReviewPolicyHash,
                    report.ReportSha256,
                    report.Outcome,
                    report.FailureClassification,
                    report.Summary,
                    [new ReviewVerdictDto("build-tests", "pass", "Verified", "The fixed review output passed.")],
                    [new ReviewOrchestrationGateDto(command.StepId, command.Aspect, "passed")]);
                orchestration = await PostAsync<OrchestrationRunDto>($"/api/v1/orchestration/projects/{_project.ProjectId}/runs", new CreateOrchestrationRunRequest(
                    _task.TaskId,
                    JsonSerializer.Serialize(payload, Json),
                    "scenario-review-orchestration:" + review.Attempt.AttemptId));
            }
            while (orchestration.Status == "pending")
            {
                var claim = await PostAsync<OrchestrationClaimResponse>("/api/v1/orchestration/claims", new OrchestrationClaimRequest(
                    "scenario-engine" + _suffix,
                    "scenario-engine-instance" + _suffix,
                    [orchestration.CurrentStage]));
                orchestration = await PostAsync<OrchestrationRunDto>($"/api/v1/orchestration/runs/{orchestration.RunId}/stages/complete", new CompleteOrchestrationStageRequest(
                    "scenario-engine" + _suffix,
                    "scenario-engine-instance" + _suffix,
                    claim.Lease!.LeaseId,
                    claim.Lease.Fence,
                    orchestration.CurrentStage,
                    orchestration.CurrentStage == OrchestrationStage.CompletionJudge ? OrchestrationAction.Complete : OrchestrationAction.Continue,
                    "{}",
                    $"settle:{orchestration.RunId}:{orchestration.CurrentStage}"));
            }
            _task = await GetAsync<TaskDto>($"/api/v1/projects/{_project.ProjectId}/tasks/{_task.TaskId}");
            return new() { ["reviewOutcome"] = report.Outcome, ["taskState"] = _task.State };
        }

        private async Task<Dictionary<string, string>> IntegrateAsync()
        {
            var head = (await GitAsync("rev-parse", "refs/heads/main")).Trim();
            var clean = string.IsNullOrWhiteSpace(await GitAsync("status", "--porcelain"));
            return new() { ["commitIntegrated"] = (head == _resultSha && clean).ToString().ToLowerInvariant(), ["head"] = head };
        }

        private async Task<Dictionary<string, string>> CompleteAsync()
        {
            _task = await GetAsync<TaskDto>($"/api/v1/projects/{_project!.ProjectId}/tasks/{_task!.TaskId}");
            _task = await PutAsync<TaskDto>($"/api/v1/projects/{_project.ProjectId}/tasks/{_task.TaskId}", new UpdateTaskRequest(
                null, null, "6-completed", _task.Version));
            return new() { ["taskState"] = _task.State };
        }

        private async Task<Dictionary<string, string>> OrchestratorChatAsync()
        {
            var projectId = _project!.ProjectId;
            var user = new OrchestratorContextTurnDto(
                "usr_deployment_scenario" + _suffix,
                FixedClock.AddMinutes(1),
                "user",
                "Summarize the deterministic deployment proof.");
            await PostAsync<OrchestratorContextTurnDto>($"/api/v1/orchestrator-contexts/projects/{projectId}/turns", new AppendOrchestratorContextTurnRequest(user));
            var receipt = new OrchestratorContextReceiptDto(
                "rcp_deployment_scenario" + _suffix,
                user.TurnId,
                "project:" + _project.Name,
                FixedClock.AddMinutes(1).AddSeconds(1),
                new OrchestratorContextBudgetReceiptDto(1000, 2000, 3000, 128),
                [new OrchestratorContextSourceReceiptDto(
                    "deployment-scenario-report", "scenario", "v1", Hash("deployment-scenario-report"),
                    "current", 512, 128, "included")]);
            var reply = new OrchestratorContextTurnDto(
                "orch_deployment_scenario" + _suffix,
                FixedClock.AddMinutes(1).AddSeconds(2),
                "orchestrator",
                "The fixed run, review, integration, and completion assertions passed.",
                "fake-orchestrator",
                new OrchestratorContextTokenUsageDto("fake-orchestrator", 128, 32, 0, 0),
                Receipt: receipt);
            await PostAsync<OrchestratorContextTurnDto>($"/api/v1/orchestrator-contexts/projects/{projectId}/turns", new AppendOrchestratorContextTurnRequest(reply));
            var transcript = await GetAsync<OrchestratorContextTranscriptResponse>($"/api/v1/orchestrator-contexts/projects/{projectId}/turns");
            return new() { ["turnCount"] = transcript.Turns.Count.ToString(CultureInfo.InvariantCulture), ["receiptId"] = receipt.ReceiptId };
        }

        private async Task<Dictionary<string, string>> DossierDecisionAsync()
        {
            _dossierTask = await GetAsync<TaskDto>($"/api/v1/projects/{_project!.ProjectId}/tasks/{_dossierTask!.TaskId}");
            var decision = "accepted";
            _dossierTask = await PutAsync<TaskDto>($"/api/v1/projects/{_project.ProjectId}/tasks/{_dossierTask.TaskId}", new UpdateTaskRequest(
                null,
                JsonSerializer.Serialize(new
                {
                    _definition.Fixture.Dossier,
                    _definition.Fixture.DecisionGate,
                    decision,
                    decidedAt = FixedClock.AddMinutes(2),
                }, Json),
                "6-completed",
                _dossierTask.Version));
            return new() { ["decision"] = decision, ["taskState"] = _dossierTask.State };
        }

        private async Task<Dictionary<string, string>> BackupAsync()
        {
            _inventoryBefore = await InventoryHashValueAsync();
            _backup = await PostAsync<BackupResult>("/api/v1/management/backups", new BackupRequest("deployment-scenario"));
            return new()
            {
                ["backupId"] = _backup.BackupId,
                ["backupShaLength"] = _backup.Sha256.Length.ToString(CultureInfo.InvariantCulture),
                ["inventorySha256"] = _inventoryBefore,
            };
        }

        private async Task<Dictionary<string, string>> RestoreAsync()
        {
            if (_backup is null)
                throw new InvalidOperationException("The backup step has not completed.");
            RestoreResult restore;
            if (_options.Target == "remote")
            {
                restore = await PostAsync<RestoreResult>("/api/v1/management/restore", new RestoreRequest(_backup.BackupId, VerifyOnly: true));
            }
            else
            {
                await PutAsync<TaskServerStatusDto>("/api/v1/management/mode", new ChangeModeRequest(TaskServerMode.Maintenance, "deployment scenario restore rehearsal"));
                restore = await PostAsync<RestoreResult>("/api/v1/management/restore", new RestoreRequest(_backup.BackupId));
                await PutAsync<TaskServerStatusDto>("/api/v1/management/mode", new ChangeModeRequest(TaskServerMode.Normal, "deployment scenario restore rehearsal complete"));
            }
            return new()
            {
                ["verified"] = restore.Verified.ToString().ToLowerInvariant(),
                ["restored"] = restore.Restored.ToString().ToLowerInvariant(),
                ["mode"] = _options.Target == "remote" ? "verify-only" : "empty-staging-restore",
            };
        }

        private async Task<Dictionary<string, string>> InventoryHashAsync()
        {
            var after = await InventoryHashValueAsync();
            return new()
            {
                ["inventoryBefore"] = _inventoryBefore!,
                ["inventoryAfter"] = after,
                ["inventoryEqual"] = string.Equals(_inventoryBefore, after, StringComparison.Ordinal).ToString().ToLowerInvariant(),
            };
        }

        private async Task<string> InventoryHashValueAsync()
        {
            var tasks = await GetAsync<List<TaskDto>>($"/api/v1/projects/{_project!.ProjectId}/tasks");
            var history = await GetAsync<TaskHistoryDto>($"/api/v1/projects/{_project.ProjectId}/tasks/{_task!.TaskId}/history");
            var transcript = await GetAsync<OrchestratorContextTranscriptResponse>($"/api/v1/orchestrator-contexts/projects/{_project.ProjectId}/turns");
            var inventory = new
            {
                project = new { _project.ProjectId, _project.WorkspaceId, _project.Name, _project.TaskKeyPrefix },
                tasks = tasks.OrderBy(item => item.TaskKey).Select(item => new { item.TaskId, item.TaskKey, item.Title, item.State, item.Body }),
                runs = history.Runs.Select(item => new { item.RunId, item.TaskId, item.Status, item.ResultSha, item.RepositoryId }),
                events = history.Events.Select(item => new { item.EventId, item.Kind, item.PayloadJson, item.IdempotencyKey, item.Fence }),
                artifacts = history.Artifacts.Select(item => new { item.ArtifactId, item.Name, item.MediaType, item.Sha256, item.SizeBytes }),
                turns = transcript.Turns.Select(item => new { item.TurnId, item.Role, item.Body, receipt = item.Receipt?.ReceiptId }),
            };
            return Hash(JsonSerializer.Serialize(inventory, Json));
        }

        private (RunDto Run, LeaseDto Lease) RequireClaim()
        {
            if (_claim?.Run is null || _claim.Lease is null)
                throw new InvalidOperationException("The claim step has not completed.");
            return (_claim.Run, _claim.Lease);
        }

        private async Task<T> GetAsync<T>(string path)
        {
            using var response = await _client.GetAsync(path);
            return await ReadAsync<T>(response, path);
        }

        private async Task<T> PostAsync<T>(string path, object request)
        {
            using var response = await _client.PostAsJsonAsync(path, request, Json);
            return await ReadAsync<T>(response, path);
        }

        private async Task<T> PutAsync<T>(string path, object request)
        {
            using var response = await _client.PutAsJsonAsync(path, request, Json);
            return await ReadAsync<T>(response, path);
        }

        private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string path)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"{path} returned HTTP {(int)response.StatusCode}: {body}");
            }
            return await response.Content.ReadFromJsonAsync<T>(Json)
                ?? throw new InvalidOperationException($"{path} returned an empty response.");
        }

        private async Task<string> GitAsync(params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = _repositoryPath!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            start.Environment["GIT_AUTHOR_DATE"] = "2026-09-06T12:00:00Z";
            start.Environment["GIT_COMMITTER_DATE"] = "2026-09-06T12:00:00Z";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} exited {process.ExitCode}: {stderr.Trim()}");
            return stdout;
        }

        private async Task WriteEvidenceAsync(string name, ScenarioStep step, IReadOnlyDictionary<string, string> observations)
        {
            var payload = new
            {
                schemaVersion = 1,
                scenario = _definition.Name,
                target = _options.Target,
                level = _options.Level,
                step = step.Id,
                fixedClock = _definition.Clock,
                observations,
                assertions = step.Assertions,
            };
            await File.WriteAllTextAsync(
                Path.Combine(_options.ResultsDirectory, name),
                JsonSerializer.Serialize(payload, Json) + "\n");
        }

        private static void Assert(ScenarioStep step, IReadOnlyDictionary<string, string> observations)
        {
            foreach (var assertion in step.Assertions)
            {
                if (assertion.Kind != "equals")
                    throw new InvalidOperationException($"Step {step.Id} uses unsupported assertion kind '{assertion.Kind}'.");
                if (!observations.TryGetValue(assertion.Actual, out var actual))
                    throw new InvalidOperationException($"Step {step.Id} did not emit '{assertion.Actual}'.");
                if (!string.Equals(actual, assertion.Expected, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Step {step.Id} expected {assertion.Actual}={assertion.Expected}, got {actual}.");
            }
        }

        private static string Hash(string value)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }

        private static string Unwrap(Exception exception)
            => (exception.GetBaseException().Message + (exception.InnerException is null ? "" : " " + exception.InnerException.Message)).Trim();
    }

    private static class ScenarioReport
    {
        public static async Task WriteAsync(ScenarioResult result, string directory)
        {
            var markdown = new StringBuilder()
                .AppendLine("# Deployment scenario report")
                .AppendLine()
                .AppendLine($"- Scenario: `{result.Name}`")
                .AppendLine($"- Target: `{result.Target}`")
                .AppendLine($"- Level: `{result.Level}`")
                .AppendLine($"- Result: **{(result.Passed ? "PASSED" : "FAILED")}**")
                .AppendLine($"- Duration: {result.Duration.TotalSeconds:F3} s")
                .AppendLine()
                .AppendLine("| Step | Status | Duration | Evidence |")
                .AppendLine("|---|---:|---:|---|");
            foreach (var step in result.Steps)
                markdown.AppendLine($"| {step.Title} | {step.Status} | {step.Duration.TotalSeconds:F3} s | {(step.Evidence is null ? "-" : $"[{step.Evidence}]({step.Evidence})")} |");
            var failures = result.Steps.Where(item => item.Error is not null).ToArray();
            if (failures.Length > 0)
            {
                markdown.AppendLine().AppendLine("## Failures").AppendLine();
                foreach (var failure in failures)
                    markdown.AppendLine($"- `{failure.Id}`: {failure.Error}");
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "scenario-report.md"), markdown.ToString());

            var suite = new XElement("testsuite",
                new XAttribute("name", $"{result.Name}.{result.Target}.{result.Level}"),
                new XAttribute("tests", result.Steps.Count),
                new XAttribute("failures", failures.Length),
                new XAttribute("skipped", result.Steps.Count(item => item.Status == "not-run")),
                new XAttribute("time", result.Duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)));
            foreach (var step in result.Steps)
            {
                var test = new XElement("testcase",
                    new XAttribute("classname", $"deployment-scenario.{result.Target}"),
                    new XAttribute("name", step.Id),
                    new XAttribute("time", step.Duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)));
                if (step.Status == "failed")
                    test.Add(new XElement("failure", new XAttribute("message", step.Error ?? "Scenario step failed."), step.Error));
                else if (step.Status == "not-run")
                    test.Add(new XElement("skipped", new XAttribute("message", step.Error ?? "Not run.")));
                suite.Add(test);
            }
            await File.WriteAllTextAsync(
                Path.Combine(directory, "scenario-junit.xml"),
                new XDocument(new XDeclaration("1.0", "utf-8", null), suite).ToString() + "\n");
        }
    }

    private sealed record ScenarioDefinition(
        int SchemaVersion,
        string Name,
        string Clock,
        ScenarioFixture Fixture,
        Dictionary<string, int> Levels,
        List<ScenarioStep> Steps)
    {
        public void Validate(string level)
        {
            if (SchemaVersion != 1 || Steps.Count == 0 || Steps.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != Steps.Count)
                throw new InvalidOperationException("The deployment scenario definition is invalid.");
            if (!Levels.TryGetValue(level, out var count) || count < 1 || count > Steps.Count)
                throw new InvalidOperationException($"The deployment scenario level '{level}' is invalid.");
            if (Steps.Any(item => item.Assertions.Count == 0))
                throw new InvalidOperationException("Every deployment scenario step needs at least one typed assertion.");
        }
    }

    private sealed record ScenarioFixture(
        string WorkspaceId,
        string ProjectId,
        string ProjectName,
        string TaskKeyPrefix,
        string Epic,
        string Dossier,
        string DecisionGate);
    private sealed record ScenarioStep(string Id, string Title, List<ScenarioAssertion> Assertions);
    private sealed record ScenarioAssertion(string Kind, string Actual, string Expected);
    private sealed record StepResult(string Id, string Title, string Status, TimeSpan Duration, string? Evidence, string? Error);
    private sealed record ScenarioResult(string Name, string Target, string Level, bool Passed, TimeSpan Duration, List<StepResult> Steps);
}
