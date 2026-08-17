using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.DemoReplayRunner;

/// <summary>
/// The complete server surface of the replay service: post one sealed frame, or
/// probe health. There is intentionally no claim, lease, log, artifact, or
/// completion method on this type, and the egress lock refuses every other path
/// even if one were added by mistake.
/// </summary>
public sealed class ReplayClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public ReplayClient(ReplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var origin = new Uri(options.ServerUrl, UriKind.Absolute);
        _http = new HttpClient(new ReplayEgressLock(origin, new HttpClientHandler()))
        {
            BaseAddress = origin,
            Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
        };
        if (!string.IsNullOrWhiteSpace(options.AuthToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AuthToken);
        _http.DefaultRequestHeaders.Add("X-Client-Id", options.RunnerId);
        _ownsClient = true;
    }

    internal ReplayClient(HttpClient http)
    {
        _http = http;
        _ownsClient = false;
    }

    /// <summary>Probes the demo server. Returns null when healthy, otherwise a reason.</summary>
    public async Task<string?> ProbeHealthAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(ReplayEgressLock.HealthPath, ct);
            return response.IsSuccessStatusCode ? null : $"health probe returned {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ex.Message;
        }
    }

    /// <summary>Offers one sealed frame to the narrow replay scope.</summary>
    public async Task<ReplayPostOutcome> PostFrameAsync(DemoReplayEventRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(ReplayEgressLock.ReplayPath, request, Json, ct);
        if (response.IsSuccessStatusCode)
        {
            var accepted = await response.Content.ReadFromJsonAsync<DemoReplayEventAccepted>(Json, ct);
            return new ReplayPostOutcome((int)response.StatusCode, true, accepted?.Origin, null);
        }
        var body = await response.Content.ReadAsStringAsync(ct);
        return new ReplayPostOutcome((int)response.StatusCode, false, null, DenialCode(body));
    }

    private static string? DenialCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}

public sealed record ReplayPostOutcome(int StatusCode, bool Accepted, string? Origin, string? DenialCode);
