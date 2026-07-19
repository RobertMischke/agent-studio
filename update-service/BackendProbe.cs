using System.Net.Http.Json;
using System.Text.Json;

namespace AgentTaskboard.UpdateService;

/// <summary>
/// Probes the main backend via HTTP. Every method swallows transport errors
/// and reports them as "false" / null — the update service must stay up
/// even when the backend is mid-restart.
/// </summary>
public sealed class BackendProbe : IBackendProbe
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _clientId;
    private readonly ILogger<BackendProbe> _logger;

    public BackendProbe(HttpClient http, string baseUrl, string clientId, ILogger<BackendProbe> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _clientId = clientId;
        _logger = logger;
    }

    public string BaseUrl => _baseUrl;

    public async Task<HealthzResult> ProbeHealthzAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var resp = await _http.GetAsync($"{_baseUrl}/healthz", cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            return new HealthzResult((int)resp.StatusCode, resp.IsSuccessStatusCode, body);
        }
        catch (Exception)
        {
            return new HealthzResult(0, false, null);
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        var r = await ProbeHealthzAsync(ct);
        return r.Ok;
    }

    public async Task<RuntimeVersion?> ReadRuntimeVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            return await _http.GetFromJsonAsync<RuntimeVersion>($"{_baseUrl}/api/system/version", cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Runtime version probe failed");
            return null;
        }
    }

    public async Task<bool> WaitForHealthyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsHealthyAsync(ct)) return true;
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    public async Task<Dictionary<string, string>?> ReadProjectModesAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/runner/status");
            req.Headers.Add("X-Client-Id", _clientId);
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            var root = doc.RootElement;
            if (!root.TryGetProperty("projects", out var projects)) return null;

            var modes = new Dictionary<string, string>();
            foreach (var prop in projects.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("mode", out var mode))
                    modes[prop.Name] = mode.GetString() ?? "unknown";
            }
            return modes;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReadProjectModesAsync failed");
            return null;
        }
    }

    public async Task<bool> SetModeAsync(string projectName, string mode, string? reason = null, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var encoded = Uri.EscapeDataString(projectName);
            // Send the reason so the backend classifies this as a system-driven
            // flip (ClassifyModeSource), not an operator toggle. Without it the
            // quiesce-to-manual would be recorded as the operator's durable mode
            // and clobber auto-continuous across the restart (ASS-1753).
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}/api/runner/{encoded}/mode")
            {
                Content = string.IsNullOrWhiteSpace(reason)
                    ? JsonContent.Create(new { mode })
                    : JsonContent.Create(new { mode, reason })
            };
            req.Headers.Add("X-Client-Id", _clientId);
            using var resp = await _http.SendAsync(req, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SetModeAsync({Project}, {Mode}) failed", projectName, mode);
            return false;
        }
    }

    /// <summary>
    /// GET <paramref name="path"/> with the standard X-Client-Id header.
    /// Returns (httpStatus, body). httpStatus=0 on transport failure.
    /// Used by phase-6 verification for arbitrary endpoints.
    /// </summary>
    public async Task<(int Status, string Body)> GetAsync(string path, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
            req.Headers.Add("X-Client-Id", _clientId);
            using var resp = await _http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            return ((int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GET {Path} failed", path);
            return (0, "");
        }
    }

    /// <summary>POST a JSON body to the internal probe endpoint and read the echo back.</summary>
    public async Task<(int Status, string Body)> PostJsonAsync(string path, object body, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
            {
                Content = JsonContent.Create(body)
            };
            req.Headers.Add("X-Client-Id", _clientId);
            using var resp = await _http.SendAsync(req, cts.Token);
            var content = await resp.Content.ReadAsStringAsync(cts.Token);
            return ((int)resp.StatusCode, content);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "POST {Path} failed", path);
            return (0, "");
        }
    }
}

public sealed record HealthzResult(int HttpStatus, bool Ok, string? Body);
