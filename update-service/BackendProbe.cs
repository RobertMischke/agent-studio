using System.Net.Http.Json;

namespace AgentTaskboard.UpdateService;

/// <summary>
/// Probes the main backend via HTTP. Every method swallows transport errors
/// and reports them as "false" / null — the update service must stay up
/// even when the backend is mid-restart.
/// </summary>
public sealed class BackendProbe
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

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var resp = await _http.GetAsync($"{_baseUrl}/healthz", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
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
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            var root = doc.RootElement;
            if (!root.TryGetProperty("projects", out var projects)) return null;

            var modes = new Dictionary<string, string>();
            foreach (var prop in projects.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("mode", out var mode))
                {
                    modes[prop.Name] = mode.GetString() ?? "unknown";
                }
            }
            return modes;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReadProjectModesAsync failed");
            return null;
        }
    }

    public async Task<bool> SetModeAsync(string projectName, string mode, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var encoded = Uri.EscapeDataString(projectName);
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}/api/runner/{encoded}/mode")
            {
                Content = JsonContent.Create(new { mode })
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
}
