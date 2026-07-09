using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentRunner;

/// <summary>
/// Thin typed HTTP client over the Task Server's Runner API surface: the fenced
/// lease (RM-3), log + artifact ingestion (RM-4), out-of-band completion, and the
/// task-file read used to fetch prompt.md. The runner talks to the server only
/// through these routes - it never writes the task store directly.
/// </summary>
public sealed class TaskServerClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public TaskServerClient(RunnerOptions options)
    {
        _http = new HttpClient { BaseAddress = new Uri(options.ServerUrl), Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrWhiteSpace(options.AuthToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AuthToken);
        // The server treats X-Client-Id as a registration boundary; identify the
        // runner so the completion attribution and logs name the right actor.
        _http.DefaultRequestHeaders.Add("X-Client-Id", options.RunnerId);
    }

    public async Task<RunLeaseResponse> AcquireLeaseAsync(RunLeaseAcquireRequest req, CancellationToken ct)
        => await PostJsonAsync<RunLeaseAcquireRequest, RunLeaseResponse>("/api/runner/lease/acquire", req, ct)
           ?? new RunLeaseResponse("Invalid", false, null, "Empty lease response.");

    public async Task<RunLeaseResponse> RenewLeaseAsync(RunLeaseHeartbeatRequest req, CancellationToken ct)
        => await PostJsonAsync<RunLeaseHeartbeatRequest, RunLeaseResponse>("/api/runner/lease/renew", req, ct)
           ?? new RunLeaseResponse("Invalid", false, null, "Empty renew response.");

    public async Task<RunLeaseResponse> ReleaseLeaseAsync(RunLeaseReleaseRequest req, CancellationToken ct)
        => await PostJsonAsync<RunLeaseReleaseRequest, RunLeaseResponse>("/api/runner/lease/release", req, ct)
           ?? new RunLeaseResponse("Invalid", false, null, "Empty release response.");

    public async Task<LogIngestResponse?> IngestLogsAsync(LogIngestRequest req, CancellationToken ct)
        => await PostJsonAsync<LogIngestRequest, LogIngestResponse>("/api/runner/logs", req, ct);

    public async Task<ArtifactIngestResponse?> UploadArtifactsAsync(ArtifactIngestRequest req, CancellationToken ct)
        => await PostJsonAsync<ArtifactIngestRequest, ArtifactIngestResponse>("/api/runner/artifacts", req, ct);

    public async Task<ExternalCompletionResponse?> CompleteAsync(string jobId, ExternalCompletionRequest req, CancellationToken ct)
        => await PostJsonAsync<ExternalCompletionRequest, ExternalCompletionResponse>(
            $"/api/tasks/{Uri.EscapeDataString(jobId)}/external-completion", req, ct);

    /// <summary>Fetch a text file from the task's job folder, e.g. prompt.md. Returns null on 404.</summary>
    public async Task<string?> ReadTaskFileAsync(string jobId, string relativePath, CancellationToken ct)
    {
        var url = $"/api/tasks/{Uri.EscapeDataString(jobId)}/files/{relativePath}";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<TResp?> PostJsonAsync<TReq, TResp>(string url, TReq body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(url, body, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new TaskServerException((int)resp.StatusCode, $"POST {url} -> {(int)resp.StatusCode}: {Trim(text)}");
        }
        return await resp.Content.ReadFromJsonAsync<TResp>(Json, ct);
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "...";

    public void Dispose() => _http.Dispose();
}

/// <summary>A non-success HTTP reply from the Task Server, carrying the status code for branching.</summary>
public sealed class TaskServerException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
