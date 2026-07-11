using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRunner;

/// <summary>
/// Thin typed HTTP client over the Task Server's Runner API surface: the fenced
/// lease (RM-3), log + artifact ingestion (RM-4), out-of-band completion, and the
/// task-file read used to fetch prompt.md. The runner talks to the server only
/// through these routes - it never writes the task store directly.
/// </summary>
public sealed class TaskServerClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private readonly HttpClient _http;

    public TaskServerClient(RunnerOptions options)
    {
        _http = new HttpClient { BaseAddress = new Uri(options.ServerUrl), Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrWhiteSpace(options.AuthToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AuthToken);
        // The server treats X-Client-Id as a registration boundary; seed it with
        // the provisional runner id so reads (and the registration POST itself)
        // are attributed. RegisterAsync swaps in the server-assigned id before the
        // first write, since an unregistered id is rejected 401 on mutations.
        SetClientId(options.RunnerId);
    }

    /// <summary>
    /// Test seam: drive the client against an already-configured <see cref="HttpClient"/>
    /// — e.g. one produced by the backend's in-memory WebApplicationFactory — so the
    /// runner's real HTTP + WireModels round-trip is exercised end-to-end against the
    /// live server endpoints without a socket. Not for production use; the production
    /// path is the <see cref="RunnerOptions"/> constructor above.
    /// </summary>
    internal TaskServerClient(HttpClient http, string runnerId)
    {
        _http = http;
        SetClientId(runnerId);
    }

    /// <summary>
    /// Register the runner as a client identity and adopt the server-assigned id as
    /// the X-Client-Id for every subsequent write. Idempotent on the display name;
    /// the registration route is an open path so it works with the provisional id.
    /// Returns the adopted client id (falls back to the provisional id on an empty
    /// reply so a misconfigured open-auth server still gets a stable header).
    /// </summary>
    public async Task<string> RegisterAsync(string displayName, string kind, CancellationToken ct)
    {
        var resp = await PostJsonAsync<ClientRegisterRequest, ClientRegisterResponse>(
            "/api/clients/register", new ClientRegisterRequest(displayName, kind), ct);
        if (!string.IsNullOrWhiteSpace(resp?.Id))
            SetClientId(resp!.Id);
        return resp?.Id ?? string.Empty;
    }

    /// <summary>The client id this runner presents as X-Client-Id (server-assigned after registration).</summary>
    public string ClientId { get; private set; } = string.Empty;

    /// <summary>How long the liveness probe waits before it calls the server unreachable.</summary>
    private const int HealthProbeTimeoutSeconds = 10;

    /// <summary>
    /// Liveness probe against the Task Server's open <c>/healthz</c> route. Returns
    /// <c>null</c> when the server answers 200; otherwise a short human-readable
    /// reason (HTTP status, timeout, or transport error). It never throws for an
    /// unreachable server - a dropped reverse tunnel is an expected, recoverable
    /// state, so the caller can report "connection lost" cleanly instead of letting
    /// a raw transport exception cascade through register/lease/launch. The probe
    /// uses its own short timeout so a black-holed tunnel fails fast rather than
    /// blocking on the 60 s request timeout.
    /// </summary>
    public async Task<string?> ProbeHealthAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(HealthProbeTimeoutSeconds));
        try
        {
            using var resp = await _http.GetAsync("/healthz", timeout.Token);
            return resp.IsSuccessStatusCode
                ? null
                : $"server answered /healthz with HTTP {(int)resp.StatusCode}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // a real shutdown, not a health failure - let the caller unwind
        }
        catch (OperationCanceledException)
        {
            return $"no response within {HealthProbeTimeoutSeconds}s (reverse tunnel down?)";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void SetClientId(string clientId)
    {
        ClientId = clientId;
        _http.DefaultRequestHeaders.Remove("X-Client-Id");
        if (!string.IsNullOrWhiteSpace(clientId))
            _http.DefaultRequestHeaders.Add("X-Client-Id", clientId);
    }

    public async Task<RunLeaseResponse> AcquireLeaseAsync(RunLeaseAcquireRequest req, CancellationToken ct)
        => await PostJsonAsync<RunLeaseAcquireRequest, RunLeaseResponse>("/api/runner/lease/acquire", req, ct)
           ?? new RunLeaseResponse("Invalid", false, null, "Empty lease response.");

    public async Task<RunnerClaimResponse> ClaimAsync(RunnerClaimRequest req, CancellationToken ct)
        => await PostJsonAsync<RunnerClaimRequest, RunnerClaimResponse>("/api/runner/claim", req, ct)
           ?? new RunnerClaimResponse(RunnerClaimStatus.Empty, Message: "Empty claim response.");

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

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
