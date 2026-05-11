using System.Diagnostics;
using System.Text.Json;

namespace AgentTaskboard.UpdateService;

/// <summary>
/// Phase 6 (verifying-after-restart) per ADR-0031. The bar is intentionally
/// strict: all six checks must pass. The first failing check still records
/// every preceding check's row in verification.jsonl, but the verifier
/// stops at the first failure to avoid drowning the operator in cascade
/// noise.
///
/// The verifier is split into two halves so unit tests can exercise the
/// pure decision shape (<see cref="EvaluateChecks"/>) without spinning up
/// HTTP. The orchestrator-facing surface (<see cref="RunAsync"/>) wires
/// the real probe in.
/// </summary>
public sealed class UpdateVerifier
{
    private readonly BackendProbe _backend;
    private readonly ILogger<UpdateVerifier> _logger;

    public UpdateVerifier(BackendProbe backend, ILogger<UpdateVerifier> logger)
    {
        _backend = backend;
        _logger = logger;
    }

    /// <summary>
    /// Run the six checks in order. Returns the per-step results AND the
    /// compact failure list (empty when all six passed).
    /// </summary>
    public async Task<VerificationOutcome> RunAsync(
        string runId,
        IReadOnlyDictionary<string, string> preProjectModes,
        Action<VerificationCheck>? onCheck,
        CancellationToken ct)
    {
        var rows = new List<VerificationCheck>();

        // 1. healthz-stable: 5 polls, 3 s spacing, all must be 200 + "ok".
        var healthz = await CheckHealthzStableAsync(runId, ct);
        Emit(healthz);
        if (!healthz.Ok) return Conclude(rows, healthz);

        // 2. runner-status: 200 + every pre-snapshot project present.
        var runnerStatus = await CheckRunnerStatusAsync(runId, preProjectModes, ct);
        Emit(runnerStatus);
        if (!runnerStatus.Ok) return Conclude(rows, runnerStatus);

        // 3. jobs-grouped.
        var jobsGrouped = await CheckJobsGroupedAsync(runId, ct);
        Emit(jobsGrouped);
        if (!jobsGrouped.Ok) return Conclude(rows, jobsGrouped);

        // 4. clients: 200 + at least 1.
        var clients = await CheckClientsAsync(runId, ct);
        Emit(clients);
        if (!clients.Ok) return Conclude(rows, clients);

        // 5. cli-quota: 200; degraded payload acceptable.
        var quota = await CheckCliQuotaAsync(runId, ct);
        Emit(quota);
        if (!quota.Ok) return Conclude(rows, quota);

        // 6. db-touch: POST /api/_internal/probe round-trip.
        var dbTouch = await CheckDbTouchAsync(runId, ct);
        Emit(dbTouch);

        return Conclude(rows, dbTouch.Ok ? null : dbTouch);

        void Emit(VerificationCheck row)
        {
            rows.Add(row);
            try { onCheck?.Invoke(row); } catch (Exception ex) { _logger.LogDebug(ex, "verification listener threw"); }
        }
    }

    /// <summary>
    /// Pure decision: given a list of check rows, return the failure summary
    /// the FE / history should see. Exposed for unit tests.
    /// </summary>
    public static VerificationOutcome EvaluateChecks(IReadOnlyList<VerificationCheck> rows)
    {
        var failures = rows.Where(r => !r.Ok)
                           .Select(r => new VerificationFailure(r.Step, r.Observed, r.Expected))
                           .ToList();
        return new VerificationOutcome(rows.ToList(), failures);
    }

    private static VerificationOutcome Conclude(List<VerificationCheck> rows, VerificationCheck? firstFail)
    {
        var failures = rows.Where(r => !r.Ok)
                           .Select(r => new VerificationFailure(r.Step, r.Observed, r.Expected))
                           .ToList();
        return new VerificationOutcome(rows, failures);
    }

    private async Task<VerificationCheck> CheckHealthzStableAsync(string runId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var attempts = 5;
        var spacing = TimeSpan.FromSeconds(3);
        for (int i = 0; i < attempts; i++)
        {
            var r = await _backend.ProbeHealthzAsync(ct);
            var bodyOk = r.Body != null && r.Body.Trim('"').Trim() == "ok";
            if (!r.Ok || !bodyOk)
            {
                sw.Stop();
                return new VerificationCheck(
                    runId, "healthz-stable", false,
                    $"attempt {i + 1}/{attempts}: http={r.HttpStatus} body={Truncate(r.Body, 80)}",
                    "5x http=200 body=\"ok\"",
                    DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
            }
            if (i < attempts - 1)
            {
                try { await Task.Delay(spacing, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        sw.Stop();
        return new VerificationCheck(runId, "healthz-stable", true, "5/5 ok", "5x http=200 body=\"ok\"",
            DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
    }

    private async Task<VerificationCheck> CheckRunnerStatusAsync(string runId, IReadOnlyDictionary<string, string> pre, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (status, body) = await _backend.GetAsync("/api/runner/status", TimeSpan.FromSeconds(10), ct);
        sw.Stop();
        if (status != 200)
            return new VerificationCheck(runId, "runner-status", false,
                $"http={status}", "http=200", DateTime.UtcNow, (int)sw.ElapsedMilliseconds);

        Dictionary<string, string> projects;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("projects", out var p))
                return new VerificationCheck(runId, "runner-status", false, "no `projects` key", "json with `projects`",
                    DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
            projects = p.EnumerateObject().ToDictionary(o => o.Name, _ => "");
        }
        catch (JsonException ex)
        {
            return new VerificationCheck(runId, "runner-status", false, $"parse: {ex.Message}", "valid json",
                DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
        }

        var missing = pre.Keys.Where(k => !projects.ContainsKey(k)).ToList();
        if (missing.Count > 0)
            return new VerificationCheck(runId, "runner-status", false,
                $"missing: {string.Join(",", missing)}",
                "every pre-snapshot project present",
                DateTime.UtcNow, (int)sw.ElapsedMilliseconds);

        return new VerificationCheck(runId, "runner-status", true,
            $"{projects.Count} project(s)", "every pre-snapshot project present",
            DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
    }

    private async Task<VerificationCheck> CheckJobsGroupedAsync(string runId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (status, body) = await _backend.GetAsync("/api/jobs/grouped", TimeSpan.FromSeconds(15), ct);
        sw.Stop();
        if (status != 200)
            return new VerificationCheck(runId, "jobs-grouped", false, $"http={status}", "http=200",
                DateTime.UtcNow, (int)sw.ElapsedMilliseconds);

        try
        {
            using var doc = JsonDocument.Parse(body);
            // Old shape may be a flat object whose values are arrays per state.
            // We accept either {"preparation":[...], ...} or any object that
            // parses; the sentinel is "the endpoint answers with valid JSON".
            if (doc.RootElement.ValueKind != JsonValueKind.Object && doc.RootElement.ValueKind != JsonValueKind.Array)
                return new VerificationCheck(runId, "jobs-grouped", false, $"unexpected root kind {doc.RootElement.ValueKind}", "json object/array",
                    DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
        }
        catch (JsonException ex)
        {
            return new VerificationCheck(runId, "jobs-grouped", false, $"parse: {ex.Message}", "valid json",
                DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
        }
        return new VerificationCheck(runId, "jobs-grouped", true, "ok", "200 + parses",
            DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
    }

    private async Task<VerificationCheck> CheckClientsAsync(string runId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (status, body) = await _backend.GetAsync("/api/clients", TimeSpan.FromSeconds(10), ct);
        sw.Stop();
        if (status != 200)
            return new VerificationCheck(runId, "clients", false, $"http={status}", "http=200",
                DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
        try
        {
            using var doc = JsonDocument.Parse(body);
            int count;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                count = doc.RootElement.GetArrayLength();
            else if (doc.RootElement.TryGetProperty("clients", out var arr) && arr.ValueKind == JsonValueKind.Array)
                count = arr.GetArrayLength();
            else
                return new VerificationCheck(runId, "clients", false, "shape unrecognised", "array or {clients:[...]}",
                    DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
            if (count < 1)
                return new VerificationCheck(runId, "clients", false, "count=0", ">= 1 client (local-default invariant)",
                    DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
            return new VerificationCheck(runId, "clients", true, $"count={count}", ">= 1",
                DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
        }
        catch (JsonException ex)
        {
            return new VerificationCheck(runId, "clients", false, $"parse: {ex.Message}", "valid json",
                DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
        }
    }

    private async Task<VerificationCheck> CheckCliQuotaAsync(string runId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (status, _) = await _backend.GetAsync("/api/cli/quota", TimeSpan.FromSeconds(10), ct);
        sw.Stop();
        if (status != 200)
            return new VerificationCheck(runId, "cli-quota", false, $"http={status}", "http=200",
                DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
        return new VerificationCheck(runId, "cli-quota", true, "200", "200 (degraded payload ok)",
            DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
    }

    private async Task<VerificationCheck> CheckDbTouchAsync(string runId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sentinel = $"vfy-{runId}";
        var (status, body) = await _backend.PostJsonAsync(
            "/api/_internal/probe",
            new { sentinel, runId, ts = DateTime.UtcNow },
            TimeSpan.FromSeconds(10), ct);
        sw.Stop();
        if (status != 200)
            return new VerificationCheck(runId, "db-touch", false,
                $"http={status} body={Truncate(body, 80)}",
                "http=200 (Environment:IsDev or DevTools:UpdateStableEnabled gates this endpoint)",
                DateTime.UtcNow, (int)sw.ElapsedMilliseconds);

        // The endpoint echoes the request, so the sentinel must round-trip.
        if (string.IsNullOrEmpty(body) || !body.Contains(sentinel, StringComparison.Ordinal))
            return new VerificationCheck(runId, "db-touch", false,
                $"sentinel missing in body: {Truncate(body, 80)}",
                $"body contains \"{sentinel}\"",
                DateTime.UtcNow, (int)sw.ElapsedMilliseconds);

        return new VerificationCheck(runId, "db-touch", true, "round-trip ok", "echo",
            DateTime.UtcNow, (int)sw.ElapsedMilliseconds);
    }

    private static string Truncate(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}

public sealed record VerificationOutcome(
    IReadOnlyList<VerificationCheck> Checks,
    IReadOnlyList<VerificationFailure> Failures
)
{
    public bool AllPassed => Failures.Count == 0;
}
