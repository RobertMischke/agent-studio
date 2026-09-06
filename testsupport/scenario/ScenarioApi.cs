using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;

internal interface IScenarioApi : IAsyncDisposable
{
    Task ReadyAsync();
    Task<WorkspaceDto> CreateWorkspaceAsync(string name);
    Task<ProjectDto> CreateProjectAsync(string workspaceId, string name, string prefix);
    Task<TaskDto> CreateTaskAsync(string projectId, string title, string body, string state);
    Task<IReadOnlyList<TaskDto>> ListTasksAsync(string projectId);
    Task<TaskDto> GetTaskAsync(string projectId, string taskId);
    Task<TaskDto> UpdateTaskAsync(string projectId, string taskId, UpdateTaskRequest request);
    Task<RunnerDto> RegisterRunnerAsync(string runnerId, RegisterRunnerRequest request);
    Task<ClaimResponse> ClaimAsync(ClaimRequest request);
    Task<EventDto> IngestEventAsync(string runId, EventIngestRequest request);
    Task<ArtifactDto> IngestArtifactAsync(string runId, ArtifactIngestRequest request);
    Task<ResultHandoffAck> HandoffAsync(string runId, ResultHandoffRequest request);
    Task<RunDto> CompleteRunAsync(string runId, CompleteRunRequest request);
    Task<TaskHistoryDto> GetHistoryAsync(string projectId, string taskId);
    Task<ReviewSubjectDto> CreateReviewSubjectAsync(CreateReviewSubjectRequest request);
    Task<ReviewClaimResponse> ClaimReviewAsync(ReviewClaimRequest request);
    Task<ReviewReportDto> ReportReviewAsync(string attemptId, ReviewReportRequest request);
    Task<ReviewCleanupResponse> CleanupReviewAsync(string attemptId, ReviewCleanupRequest request);
    Task<IReadOnlyList<OrchestrationRunDto>> ListOrchestrationRunsAsync(string projectId, string status);
    Task<OrchestrationClaimResponse> ClaimOrchestrationAsync(OrchestrationClaimRequest request);
    Task<OrchestrationRunDto> CompleteOrchestrationAsync(string runId, CompleteOrchestrationStageRequest request);
    Task<OrchestratorContextDto> EnsureContextAsync(string projectIdentity, string taskIdentity);
    Task<OrchestratorContextTurnDto> AppendContextTurnAsync(
        string projectIdentity,
        string taskIdentity,
        AppendOrchestratorContextTurnRequest request);
    Task<OrchestratorContextTranscriptResponse> ReadContextAsync(string projectIdentity, string taskIdentity);
    Task<BackupResult> BackupAsync(BackupRequest request);
    Task<RestoreResult> RestoreAsync(BackupResult backup);
}

internal sealed class InProcessScenarioApi : IScenarioApi
{
    private readonly string _root;
    private readonly FixedTimeProvider _clock;
    private TaskServerStore _store;

    private InProcessScenarioApi(string root, FixedTimeProvider clock, TaskServerStore store)
    {
        _root = root;
        _clock = clock;
        _store = store;
    }

    public static async Task<InProcessScenarioApi> CreateAsync(string root, ScenarioDefinition definition)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse(definition.ClockUtc));
        var store = CreateStore(Path.Combine(root, "store"), clock);
        await store.InitializeAsync();
        return new InProcessScenarioApi(root, clock, store);
    }

    public Task ReadyAsync()
    {
        if (!_store.AuthorityReady)
            throw new InvalidOperationException("In-process Task Server authority is not ready.");
        return Task.CompletedTask;
    }

    public Task<WorkspaceDto> CreateWorkspaceAsync(string name)
        => _store.CreateWorkspaceAsync(new CreateWorkspaceRequest(name), "scenario-owner", default);

    public Task<ProjectDto> CreateProjectAsync(string workspaceId, string name, string prefix)
        => _store.CreateProjectAsync(new CreateProjectRequest(workspaceId, name, prefix), "scenario-owner", default);

    public Task<TaskDto> CreateTaskAsync(string projectId, string title, string body, string state)
        => _store.CreateTaskAsync(projectId, new CreateTaskRequest(title, body, state), "scenario-owner", default);

    public Task<IReadOnlyList<TaskDto>> ListTasksAsync(string projectId)
        => _store.ListTasksAsync(projectId, default);

    public async Task<TaskDto> GetTaskAsync(string projectId, string taskId)
        => await _store.GetTaskAsync(projectId, taskId, default)
           ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");

    public async Task<TaskDto> UpdateTaskAsync(string projectId, string taskId, UpdateTaskRequest request)
        => await _store.UpdateTaskAsync(projectId, taskId, request, "scenario-owner", default)
           ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");

    public Task<RunnerDto> RegisterRunnerAsync(string runnerId, RegisterRunnerRequest request)
        => _store.RegisterRunnerAsync(runnerId, request, runnerId, default);

    public Task<ClaimResponse> ClaimAsync(ClaimRequest request)
        => _store.ClaimAsync(request, request.RunnerId, default);

    public Task<EventDto> IngestEventAsync(string runId, EventIngestRequest request)
        => _store.IngestEventAsync(runId, request, request.RunnerId ?? "scenario-coding", default);

    public Task<ArtifactDto> IngestArtifactAsync(string runId, ArtifactIngestRequest request)
        => _store.IngestArtifactAsync(runId, request, request.RunnerId ?? "scenario-coding", default);

    public Task<ResultHandoffAck> HandoffAsync(string runId, ResultHandoffRequest request)
        => _store.AcknowledgeResultHandoffAsync(runId, request, request.RunnerId, default);

    public Task<RunDto> CompleteRunAsync(string runId, CompleteRunRequest request)
        => _store.CompleteRunAsync(runId, request, request.RunnerId, default);

    public async Task<TaskHistoryDto> GetHistoryAsync(string projectId, string taskId)
        => await _store.GetTaskHistoryAsync(projectId, taskId, 0, default)
           ?? throw new KeyNotFoundException($"Task history '{taskId}' was not found.");

    public Task<ReviewSubjectDto> CreateReviewSubjectAsync(CreateReviewSubjectRequest request)
        => _store.CreateReviewSubjectAsync(request, "scenario-engine", default);

    public Task<ReviewClaimResponse> ClaimReviewAsync(ReviewClaimRequest request)
        => _store.ClaimReviewAsync(request, request.ExecutorId, default);

    public Task<ReviewReportDto> ReportReviewAsync(string attemptId, ReviewReportRequest request)
        => _store.ReportReviewAsync(attemptId, request, request.ExecutorId, default);

    public Task<ReviewCleanupResponse> CleanupReviewAsync(string attemptId, ReviewCleanupRequest request)
        => _store.CleanupReviewAsync(attemptId, request, request.ExecutorId, default);

    public Task<IReadOnlyList<OrchestrationRunDto>> ListOrchestrationRunsAsync(string projectId, string status)
        => _store.ListOrchestrationRunsAsync(projectId, status, default);

    public Task<OrchestrationClaimResponse> ClaimOrchestrationAsync(OrchestrationClaimRequest request)
        => _store.ClaimOrchestrationAsync(request, request.EngineId, default);

    public Task<OrchestrationRunDto> CompleteOrchestrationAsync(string runId, CompleteOrchestrationStageRequest request)
        => _store.CompleteOrchestrationStageAsync(runId, request, request.EngineId, default);

    public Task<OrchestratorContextDto> EnsureContextAsync(string projectIdentity, string taskIdentity)
        => _store.EnsureOrchestratorContextAsync(projectIdentity, taskIdentity, "scenario-owner", default);

    public Task<OrchestratorContextTurnDto> AppendContextTurnAsync(
        string projectIdentity,
        string taskIdentity,
        AppendOrchestratorContextTurnRequest request)
        => _store.AppendOrchestratorContextTurnAsync(
            projectIdentity, taskIdentity, request, "scenario-owner", default);

    public Task<OrchestratorContextTranscriptResponse> ReadContextAsync(string projectIdentity, string taskIdentity)
        => _store.ReadOrchestratorContextAsync(projectIdentity, taskIdentity, 100, "scenario-owner", default);

    public Task<BackupResult> BackupAsync(BackupRequest request)
        => _store.CreateBackupAsync(request, "scenario-owner", default);

    public async Task<RestoreResult> RestoreAsync(BackupResult backup)
    {
        var emptyRoot = Path.Combine(_root, "restored-store");
        var empty = CreateStore(emptyRoot, _clock);
        await empty.InitializeAsync();
        Directory.CreateDirectory(empty.BackupDirectory);
        File.Copy(backup.Path, Path.Combine(empty.BackupDirectory, backup.BackupId + ".db"));
        await empty.ChangeModeAsync(
            new ChangeModeRequest(TaskServerMode.Maintenance, "deployment scenario restore"),
            "scenario-owner",
            default);
        var restored = await empty.RestoreBackupAsync(new RestoreRequest(backup.BackupId), "scenario-owner", default);
        _store = empty;
        return restored;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static TaskServerStore CreateStore(string root, TimeProvider clock)
        => new(Options.Create(new TaskServerOptions
        {
            DataDirectory = root,
            BackupDirectory = Path.Combine(root, "backups"),
            MinimumLeaseSeconds = 5,
            MaximumLeaseSeconds = 120,
        }), clock);
}

internal sealed class HttpScenarioApi : IScenarioApi
{
    private readonly HttpClient _client;
    private readonly bool _verifyOnlyRestore;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollTimeout;

    public HttpScenarioApi(ScenarioOptions options, ScenarioDefinition definition)
    {
        _client = new HttpClient { BaseAddress = new Uri(options.BaseUrl! + "/") };
        _client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        _client.DefaultRequestHeaders.Add("X-Actor-Id", "deployment-scenario");
        if (!string.IsNullOrWhiteSpace(options.AuthToken))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AuthToken);
        _verifyOnlyRestore = options.Target == "remote";
        _pollInterval = TimeSpan.FromMilliseconds(definition.PollIntervalMilliseconds);
        _pollTimeout = TimeSpan.FromSeconds(definition.PollTimeoutSeconds);
    }

    public async Task ReadyAsync()
    {
        var deadline = DateTime.UtcNow + _pollTimeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await _client.GetAsync("readyz");
                if (response.IsSuccessStatusCode)
                    return;
                last = new HttpRequestException($"readyz returned {(int)response.StatusCode}.");
            }
            catch (HttpRequestException exception)
            {
                last = exception;
            }
            await Task.Delay(_pollInterval);
        }
        throw new TimeoutException("Target did not become ready within the polling contract.", last);
    }

    public Task<WorkspaceDto> CreateWorkspaceAsync(string name)
        => PostAsync<WorkspaceDto>("api/v1/workspaces", new CreateWorkspaceRequest(name));

    public Task<ProjectDto> CreateProjectAsync(string workspaceId, string name, string prefix)
        => PostAsync<ProjectDto>("api/v1/projects", new CreateProjectRequest(workspaceId, name, prefix));

    public Task<TaskDto> CreateTaskAsync(string projectId, string title, string body, string state)
        => PostAsync<TaskDto>($"api/v1/projects/{E(projectId)}/tasks", new CreateTaskRequest(title, body, state));

    public Task<IReadOnlyList<TaskDto>> ListTasksAsync(string projectId)
        => GetAsync<IReadOnlyList<TaskDto>>($"api/v1/projects/{E(projectId)}/tasks");

    public Task<TaskDto> GetTaskAsync(string projectId, string taskId)
        => GetAsync<TaskDto>($"api/v1/projects/{E(projectId)}/tasks/{E(taskId)}");

    public Task<TaskDto> UpdateTaskAsync(string projectId, string taskId, UpdateTaskRequest request)
        => PutAsync<TaskDto>($"api/v1/projects/{E(projectId)}/tasks/{E(taskId)}", request);

    public Task<RunnerDto> RegisterRunnerAsync(string runnerId, RegisterRunnerRequest request)
        => PutAsync<RunnerDto>($"api/v1/runners/{E(runnerId)}", request);

    public Task<ClaimResponse> ClaimAsync(ClaimRequest request)
        => PostAsync<ClaimResponse>($"api/v1/runners/{E(request.RunnerId)}/claims", request);

    public Task<EventDto> IngestEventAsync(string runId, EventIngestRequest request)
        => PostAsync<EventDto>($"api/v1/runs/{E(runId)}/events", request);

    public Task<ArtifactDto> IngestArtifactAsync(string runId, ArtifactIngestRequest request)
        => PostAsync<ArtifactDto>($"api/v1/runs/{E(runId)}/artifacts", request);

    public Task<ResultHandoffAck> HandoffAsync(string runId, ResultHandoffRequest request)
        => PutAsync<ResultHandoffAck>($"api/v1/runs/{E(runId)}/result-handoff", request);

    public Task<RunDto> CompleteRunAsync(string runId, CompleteRunRequest request)
        => PostAsync<RunDto>($"api/v1/runs/{E(runId)}/completion", request);

    public Task<TaskHistoryDto> GetHistoryAsync(string projectId, string taskId)
        => GetAsync<TaskHistoryDto>($"api/v1/projects/{E(projectId)}/tasks/{E(taskId)}/history");

    public Task<ReviewSubjectDto> CreateReviewSubjectAsync(CreateReviewSubjectRequest request)
        => PostAsync<ReviewSubjectDto>("api/v1/reviews/subjects", request);

    public Task<ReviewClaimResponse> ClaimReviewAsync(ReviewClaimRequest request)
        => PostAsync<ReviewClaimResponse>($"api/v1/runners/{E(request.ExecutorId)}/review-claims", request);

    public Task<ReviewReportDto> ReportReviewAsync(string attemptId, ReviewReportRequest request)
        => PostAsync<ReviewReportDto>($"api/v1/reviews/attempts/{E(attemptId)}/report", request);

    public Task<ReviewCleanupResponse> CleanupReviewAsync(string attemptId, ReviewCleanupRequest request)
        => PostAsync<ReviewCleanupResponse>($"api/v1/reviews/attempts/{E(attemptId)}/cleanup", request);

    public Task<IReadOnlyList<OrchestrationRunDto>> ListOrchestrationRunsAsync(string projectId, string status)
        => GetAsync<IReadOnlyList<OrchestrationRunDto>>(
            $"api/v1/orchestration/runs?projectId={E(projectId)}&status={E(status)}");

    public Task<OrchestrationClaimResponse> ClaimOrchestrationAsync(OrchestrationClaimRequest request)
        => PostAsync<OrchestrationClaimResponse>("api/v1/orchestration/claims", request);

    public Task<OrchestrationRunDto> CompleteOrchestrationAsync(string runId, CompleteOrchestrationStageRequest request)
        => PostAsync<OrchestrationRunDto>($"api/v1/orchestration/runs/{E(runId)}/stages/complete", request);

    public Task<OrchestratorContextDto> EnsureContextAsync(string projectIdentity, string taskIdentity)
        => PutAsync<OrchestratorContextDto>(
            $"api/v1/orchestrator-contexts/projects/{E(projectIdentity)}/tasks/{E(taskIdentity)}", new { });

    public Task<OrchestratorContextTurnDto> AppendContextTurnAsync(
        string projectIdentity,
        string taskIdentity,
        AppendOrchestratorContextTurnRequest request)
        => PostAsync<OrchestratorContextTurnDto>(
            $"api/v1/orchestrator-contexts/projects/{E(projectIdentity)}/tasks/{E(taskIdentity)}/turns", request);

    public Task<OrchestratorContextTranscriptResponse> ReadContextAsync(string projectIdentity, string taskIdentity)
        => GetAsync<OrchestratorContextTranscriptResponse>(
            $"api/v1/orchestrator-contexts/projects/{E(projectIdentity)}/tasks/{E(taskIdentity)}/turns");

    public Task<BackupResult> BackupAsync(BackupRequest request)
        => PostAsync<BackupResult>("api/v1/management/backups", request);

    public async Task<RestoreResult> RestoreAsync(BackupResult backup)
    {
        if (!_verifyOnlyRestore)
            await PutAsync<TaskServerStatusDto>("api/v1/management/mode",
                new ChangeModeRequest(TaskServerMode.Maintenance, "deployment scenario restore"));
        return await PostAsync<RestoreResult>(
            "api/v1/management/restore",
            new RestoreRequest(backup.BackupId, VerifyOnly: _verifyOnlyRestore));
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var response = await _client.GetAsync(path);
        return await ReadAsync<T>(response, path);
    }

    private async Task<T> PostAsync<T>(string path, object request)
    {
        using var response = await _client.PostAsJsonAsync(path, request);
        return await ReadAsync<T>(response, path);
    }

    private async Task<T> PutAsync<T>(string path, object request)
    {
        using var response = await _client.PutAsJsonAsync(path, request);
        return await ReadAsync<T>(response, path);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string path)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<T>()
               ?? throw new InvalidDataException($"{path} returned an empty response.");
    }

    private static string E(string value) => Uri.EscapeDataString(value);
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
