extern alias UpdSvc;
using System.Net.Http.Json;
using System.Text.Json;
using OrchestratorApi.Tests.Fixtures.UpdateService;
using UpdSvc::AgentTaskboard.UpdateService;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// ADR-0031 follow-up: full WebApplicationFactory integration suite for the
/// standalone Update Service. Each test boots the orchestrator in-process
/// against:
///
///   1. an isolated fake stable checkout (temp dir + git init --bare remote
///      + clone with one prepared commit + fake start/stop scripts);
///   2. a parallel in-process fake backend
///      (<see cref="FakeBackendHarness"/>) that answers /healthz,
///      /api/runner/status, /api/tasks/grouped, /api/tasks, /api/clients,
///      /api/cli/quota, /api/_internal/probe, and PUT /api/runner/{p}/mode.
///
/// Cases covered:
///
///   - HappyPath: trigger -> all 9 phases observed -> run folder has every
///     expected artefact (pre/post snapshot, pull-output, verification.jsonl
///     with 6 rows, resume-output, summary.md); lastRunFinishedAt populated.
///   - FailureInjection: /api/_internal/probe returns 503 -> phase=failed,
///     verificationFailures = [db-touch], partial jsonl, no rollback.
///   - AutoRollbackPositive: same failure but ATP_UPDATE_AUTO_ROLLBACK=1
///     and the probe self-heals after the first call -> rollback runs,
///     rollback-verification.jsonl has 6 passing rows, rollback-result.json
///     status=ok, history carries a rollback row with rollbackStatus=ok.
///   - AutoRollbackNegative: forward failure + /api/_internal/probe stays
///     503 even during rollback -> rollback-result.json status=failed with
///     verificationFailures populated.
///   - ManualRollback: forward run fails, then POST /update/rollback {runId}
///     -> rollback runs phases 5+6+7, rollback-verification.jsonl gets six
///     passing rows.
///   - DoneLinger: after a happy-path run, lastRunFinishedAt stays
///     populated past the (shortened) linger window.
///
/// Skipped when bash or git aren't reachable on PATH (e.g. Linux CI without
/// Git installed); the fake stable checkout needs both.
/// </summary>
public class UpdateServiceIntegrationTests
{
    private const int TriggerTimeoutMs = 90_000;

    private static async Task<JsonElement> GetStatusAsync(HttpClient client, CancellationToken ct = default)
    {
        using var resp = await client.GetAsync("/update/status", ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static async Task<JsonElement> WaitForPhaseAsync(HttpClient client, string[] terminalPhases, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            last = await GetStatusAsync(client, cts.Token);
            var phase = last.GetProperty("phase").GetString();
            if (phase != null && terminalPhases.Contains(phase)) return last;
            await Task.Delay(200, cts.Token);
        }
        var lastPhase = last.ValueKind == JsonValueKind.Object ? last.GetProperty("phase").GetString() : "(none)";
        throw new TimeoutException($"Did not reach any of [{string.Join(", ", terminalPhases)}] within {timeoutMs} ms. Last phase: {lastPhase}.");
    }

    private static async Task TriggerAsync(HttpClient client)
    {
        using var resp = await client.PostAsJsonAsync("/update/trigger", new { Force = false });
        resp.EnsureSuccessStatusCode();
    }

    private static List<JsonDocument> ReadJsonl(string path)
    {
        if (!File.Exists(path)) return new();
        return File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l))
            .ToList();
    }

    private static UpdateHistoryEntry[] ReadHistory(string path)
    {
        if (!File.Exists(path)) return Array.Empty<UpdateHistoryEntry>();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<UpdateHistoryEntry>(l, opts)!)
            .ToArray();
    }

    private static string LatestRunFolder(string runsRoot)
    {
        Assert.True(Directory.Exists(runsRoot), $"runs root does not exist: {runsRoot}");
        var dir = new DirectoryInfo(runsRoot);
        var latest = dir.GetDirectories().OrderByDescending(d => d.CreationTimeUtc).First();
        return latest.FullName;
    }

    [SkippableFact]
    public async Task HappyPath_NinePhasesObserved_RunFolderArtefactsWritten_LastRunFinishedAtPopulated()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this integration test needs both.");

        await using var backend = new FakeBackendHarness();
        await backend.StartAsync();

        checkout!.AdvanceOriginMain();
        using var factory = new UpdateServiceTestFactory(checkout!, backend, autoRollback: false);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        var phase = status.GetProperty("phase").GetString();
        var msg = status.TryGetProperty("message", out var m) ? m.GetString() : null;
        Assert.True(phase == "done", $"happy path expected phase=done, got {phase}; message={msg}");

        // Wire field is populated for the FE block-modal toast linger.
        var lastRunFinishedAt = status.GetProperty("lastRunFinishedAt");
        Assert.NotEqual(JsonValueKind.Null, lastRunFinishedAt.ValueKind);

        // Stop and start scripts both ran.
        Assert.True(checkout!.StopRan(), "stop-stable.sh marker missing — phase 5 stop did not run");
        Assert.True(checkout.StartRan(), "start-stable.sh marker missing — phase 5 start did not run");

        // Run folder artefacts.
        var runFolder = LatestRunFolder(checkout.RunsDir);
        Assert.True(File.Exists(Path.Combine(runFolder, "pre-snapshot.json")), "pre-snapshot.json missing");
        Assert.True(File.Exists(Path.Combine(runFolder, "post-snapshot.json")), "post-snapshot.json missing");
        Assert.True(File.Exists(Path.Combine(runFolder, "pull-output.txt")), "pull-output.txt missing");
        Assert.True(File.Exists(Path.Combine(runFolder, "verification.jsonl")), "verification.jsonl missing");
        Assert.True(File.Exists(Path.Combine(runFolder, "resume-output.txt")), "resume-output.txt missing");
        Assert.True(File.Exists(Path.Combine(runFolder, "summary.md")), "summary.md missing");

        var rows = ReadJsonl(Path.Combine(runFolder, "verification.jsonl"));
        Assert.Equal(6, rows.Count);
        // Every step is one of the canonical six in canonical order.
        var steps = rows.Select(r => r.RootElement.GetProperty("step").GetString()).ToArray();
        Assert.Equal(new[] { "healthz-stable", "runner-status", "jobs-grouped", "clients", "cli-quota", "db-touch" }, steps);
        Assert.All(rows, r => Assert.True(r.RootElement.GetProperty("ok").GetBoolean()));

        // Pre/post snapshots round-trip into the wire shape declared by
        // docs/schemas/update-run-snapshot.schema.json.
        var pre = JsonSerializer.Deserialize<UpdateRunSnapshot>(File.ReadAllText(Path.Combine(runFolder, "pre-snapshot.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("pre", pre.Kind);
        Assert.True(pre.HealthzOk);

        var post = JsonSerializer.Deserialize<UpdateRunSnapshot>(File.ReadAllText(Path.Combine(runFolder, "post-snapshot.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("post", post.Kind);
        Assert.True(post.HealthzOk);
    }

    [SkippableFact]
    public async Task Trigger_WhenStableIsNotBehind_DoesNotPauseOrStartRun()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this integration test needs both.");

        await using var backend = new FakeBackendHarness();
        await backend.StartAsync();

        using var factory = new UpdateServiceTestFactory(checkout!, backend, autoRollback: false);
        var client = factory.CreateClient();

        using var resp = await client.PostAsJsonAsync("/update/trigger", new { Force = false });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TriggerResponse>();
        Assert.NotNull(body);
        Assert.Equal("(none)", body!.RunId);
        Assert.Equal("already up to date", body.Message);

        var status = await GetStatusAsync(client);
        Assert.Equal("idle", status.GetProperty("phase").GetString());
        Assert.Empty(backend.ModeWrites);
        Assert.Empty(Directory.GetDirectories(checkout!.RunsDir));
        Assert.False(checkout.StopRan(), "stop-stable.sh should not run when behindBy=0");
        Assert.False(checkout.StartRan(), "start-stable.sh should not run when behindBy=0");
    }

    [SkippableFact]
    public async Task ScheduledTrigger_WhenApplyModeManual_DoesNotPauseOrStartRun()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this integration test needs both.");

        await using var backend = new FakeBackendHarness();
        await backend.StartAsync();

        checkout!.AdvanceOriginMain();
        using var factory = new UpdateServiceTestFactory(checkout, backend, autoRollback: false, mode: "manual");
        var client = factory.CreateClient();

        using var resp = await client.PostAsJsonAsync("/update/trigger", new { Reason = "scheduled", Force = false });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TriggerResponse>();
        Assert.NotNull(body);
        Assert.Equal("(none)", body!.RunId);
        Assert.Equal("manual apply mode", body.Message);

        var status = await GetStatusAsync(client);
        Assert.Equal("idle", status.GetProperty("phase").GetString());
        Assert.Equal("manual", status.GetProperty("mode").GetString());
        Assert.Empty(backend.ModeWrites);
        Assert.Empty(Directory.GetDirectories(checkout.RunsDir));
        Assert.False(checkout.StopRan(), "stop-stable.sh should not run in manual apply mode");
        Assert.False(checkout.StartRan(), "start-stable.sh should not run in manual apply mode");
    }

    [SkippableFact]
    public async Task PullFailure_AfterPause_RestoresPreRunModeInFinally()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this integration test needs both.");

        checkout!.AdvanceOriginMain();
        checkout.AdvanceLocalMain();

        await using var backend = new FakeBackendHarness();
        await backend.StartAsync();

        using var factory = new UpdateServiceTestFactory(checkout, backend, autoRollback: false);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var status = await WaitForPhaseAsync(client, new[] { "failed" }, TriggerTimeoutMs);
        Assert.Contains("git pull failed", status.GetProperty("message").GetString());

        await WaitForModeWriteAsync(backend, "agent-taskboard", "auto-continuous", TriggerTimeoutMs);
        Assert.Contains(backend.ModeWrites, w => w is { Project: "agent-taskboard", Mode: "manual" });
        Assert.Contains(backend.ModeWrites, w => w is { Project: "agent-taskboard", Mode: "auto-continuous" });
        Assert.Equal("auto-continuous", backend.ProjectModes["agent-taskboard"]);
    }

    [SkippableFact]
    public async Task FailureInjection_PhaseFailed_VerificationFailuresContainsDbTouch_NoRollback()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this integration test needs both.");

        await using var backend = new FakeBackendHarness { ProbeReturns503 = true };
        await backend.StartAsync();

        checkout!.AdvanceOriginMain();
        using var factory = new UpdateServiceTestFactory(checkout!, backend, autoRollback: false);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        var phase = status.GetProperty("phase").GetString();
        Assert.Equal("failed", phase);

        // VerificationFailures wire field carries the db-touch step.
        var failures = status.GetProperty("verificationFailures");
        Assert.Equal(JsonValueKind.Array, failures.ValueKind);
        Assert.Contains(failures.EnumerateArray(),
            f => f.GetProperty("step").GetString() == "db-touch");

        // Partial verification.jsonl is written: rows for the steps that
        // ran before the strict-bar trip, including db-touch's failing row.
        var runFolder = LatestRunFolder(checkout!.RunsDir);
        var rows = ReadJsonl(Path.Combine(runFolder, "verification.jsonl"));
        Assert.Equal(6, rows.Count);
        Assert.Equal("db-touch", rows[^1].RootElement.GetProperty("step").GetString());
        Assert.False(rows[^1].RootElement.GetProperty("ok").GetBoolean());

        // No rollback ran: rollback-result.json is absent.
        Assert.False(File.Exists(Path.Combine(runFolder, "rollback-result.json")),
            "rollback-result.json should not exist when AutoRollback=false");
        Assert.False(File.Exists(Path.Combine(runFolder, "rollback-verification.jsonl")),
            "rollback-verification.jsonl should not exist when AutoRollback=false");
    }

    [SkippableFact]
    public async Task AutoRollback_OnVerificationFailure_RollbackRunsVerification_HistoryCarriesOkRow()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this integration test needs both.");

        // Forward db-touch fails once, then probe recovers so the
        // rollback's matrix re-run passes.
        await using var backend = new FakeBackendHarness { ProbeFailFirstN = 1 };
        await backend.StartAsync();

        checkout!.AdvanceOriginMain();
        using var factory = new UpdateServiceTestFactory(checkout!, backend, autoRollback: true);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        // The forward run fails first, then the orchestrator drives the
        // rollback to completion. Status snapshots may show either
        // "failed" or "rolling-back" mid-stream; poll until the rollback
        // artefact appears on disk.
        var runFolder = await WaitForRunFolderAsync(checkout!.RunsDir);
        await WaitForFileAsync(Path.Combine(runFolder, "rollback-result.json"), TriggerTimeoutMs);

        // rollback-result.json + rollback-verification.jsonl with 6 passing rows.
        var resultJson = await File.ReadAllTextAsync(Path.Combine(runFolder, "rollback-result.json"));
        var result = JsonSerializer.Deserialize<RollbackResult>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("ok", result.Status);
        Assert.Null(result.VerificationFailures);

        var rbVerification = ReadJsonl(Path.Combine(runFolder, "rollback-verification.jsonl"));
        Assert.Equal(6, rbVerification.Count);
        Assert.All(rbVerification, r => Assert.True(r.RootElement.GetProperty("ok").GetBoolean()));

        // History carries a rollback row with RollbackStatus=ok.
        var history = ReadHistory(checkout.HistoryFile);
        Assert.Contains(history, h => h.RollbackStatus == "ok" && h.Trigger == "auto-rollback");
    }

    [SkippableFact]
    public async Task AutoRollback_HealthzStays503_RollbackResultFailed_VerificationFailuresPopulated()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this integration test needs both.");

        // Probe stays down for the whole run, so the rollback's re-run of
        // the matrix also fails at db-touch (the "negative" case in the
        // ADR-0031 follow-up: rollback verification surfaces the failing
        // step into RollbackResult.VerificationFailures).
        await using var backend = new FakeBackendHarness { ProbeReturns503 = true };
        await backend.StartAsync();

        checkout!.AdvanceOriginMain();
        using var factory = new UpdateServiceTestFactory(checkout!, backend, autoRollback: true);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var runFolder = await WaitForRunFolderAsync(checkout!.RunsDir);
        await WaitForFileAsync(Path.Combine(runFolder, "rollback-result.json"), TriggerTimeoutMs);

        var result = JsonSerializer.Deserialize<RollbackResult>(
            await File.ReadAllTextAsync(Path.Combine(runFolder, "rollback-result.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("failed", result.Status);
        Assert.NotNull(result.VerificationFailures);
        Assert.Contains(result.VerificationFailures!, f => f.Step == "db-touch");

        var history = ReadHistory(checkout.HistoryFile);
        var rbRow = Assert.Single(history, h => h.RollbackStatus == "failed" && h.Trigger == "auto-rollback");
        Assert.NotNull(rbRow.VerificationFailures);
        Assert.Contains(rbRow.VerificationFailures!, f => f.Step == "db-touch");
    }

    [SkippableFact]
    public async Task ManualRollback_AfterFailedForwardRun_WritesRollbackVerification()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this integration test needs both.");

        // Forward fails, manual rollback succeeds.
        await using var backend = new FakeBackendHarness { ProbeFailFirstN = 1 };
        await backend.StartAsync();

        checkout!.AdvanceOriginMain();
        using var factory = new UpdateServiceTestFactory(checkout!, backend, autoRollback: false);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var failed = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        Assert.Equal("failed", failed.GetProperty("phase").GetString());
        var runId = failed.GetProperty("currentRunId").GetString();
        Assert.False(string.IsNullOrEmpty(runId));

        using var rbResp = await client.PostAsJsonAsync("/update/rollback", new { RunId = runId });
        Assert.True(rbResp.IsSuccessStatusCode, $"manual rollback POST failed: {rbResp.StatusCode}");

        var runFolder = LatestRunFolder(checkout!.RunsDir);
        await WaitForFileAsync(Path.Combine(runFolder, "rollback-result.json"), TriggerTimeoutMs);

        var rbVerification = ReadJsonl(Path.Combine(runFolder, "rollback-verification.jsonl"));
        Assert.Equal(6, rbVerification.Count);
        Assert.All(rbVerification, r => Assert.True(r.RootElement.GetProperty("ok").GetBoolean()));

        var history = ReadHistory(checkout.HistoryFile);
        Assert.Contains(history, h => h.RollbackStatus == "ok" && h.Trigger == "manual-rollback");
    }

    [SkippableFact]
    public async Task JobsGroupedRetry_FirstAttemptFails_SecondSucceeds_RunCompletes()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this integration test needs both.");

        // F58: /api/tasks/grouped fails the first call (simulating cold-start
        // delay), then succeeds on the retry. The verifier's new retry logic
        // should recover and the overall run should reach phase=done.
        await using var backend = new FakeBackendHarness { JobsGroupedFailFirstN = 1 };
        await backend.StartAsync();

        checkout!.AdvanceOriginMain();
        using var factory = new UpdateServiceTestFactory(checkout!, backend, autoRollback: false);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        var phase = status.GetProperty("phase").GetString();
        Assert.Equal("done", phase);

        // The retry succeeded: verification.jsonl should have 6 passing rows,
        // and the jobs-grouped row should note the retry attempt.
        var runFolder = LatestRunFolder(checkout!.RunsDir);
        var rows = ReadJsonl(Path.Combine(runFolder, "verification.jsonl"));
        Assert.Equal(6, rows.Count);
        var jobsGroupedRow = rows.First(r => r.RootElement.GetProperty("step").GetString() == "jobs-grouped");
        Assert.True(jobsGroupedRow.RootElement.GetProperty("ok").GetBoolean());
        var observed = jobsGroupedRow.RootElement.GetProperty("observed").GetString();
        Assert.Contains("attempt 2", observed!);
    }

    [SkippableFact]
    public async Task DoneLinger_LastRunFinishedAtSurvivesPastLingerWindow()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this integration test needs both.");

        await using var backend = new FakeBackendHarness();
        await backend.StartAsync();

        checkout!.AdvanceOriginMain();
        // Shortened linger window so the test does not have to wait the
        // production-default 60 s. The wire contract is unchanged: the FE
        // computes "within the last N seconds" against lastRunFinishedAt;
        // we assert the field stays populated and ages past the configured
        // window.
        using var factory = new UpdateServiceTestFactory(checkout!, backend, autoRollback: false, doneLingerSeconds: 2);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var done = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        Assert.Equal("done", done.GetProperty("phase").GetString());

        var lastRunFinishedAt = done.GetProperty("lastRunFinishedAt").GetDateTime();

        // Tick 1: well inside the linger window.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var s1 = await GetStatusAsync(client);
        Assert.Equal(JsonValueKind.String, s1.GetProperty("lastRunFinishedAt").ValueKind);
        var age1 = DateTime.UtcNow - s1.GetProperty("lastRunFinishedAt").GetDateTime();
        Assert.True(age1 < TimeSpan.FromSeconds(2),
            $"after 500 ms, lastRunFinishedAt age should be < 2 s, got {age1.TotalMilliseconds:F0} ms");

        // Tick 2: past the linger window — the field stays populated, but
        // its age now exceeds the configured 2 s window.
        await Task.Delay(TimeSpan.FromMilliseconds(2_600));
        var s2 = await GetStatusAsync(client);
        Assert.Equal(JsonValueKind.String, s2.GetProperty("lastRunFinishedAt").ValueKind);
        var age2 = DateTime.UtcNow - s2.GetProperty("lastRunFinishedAt").GetDateTime();
        Assert.True(age2 > TimeSpan.FromSeconds(2),
            $"after ~3 s, lastRunFinishedAt age should exceed the 2 s linger window, got {age2.TotalSeconds:F1} s");
        Assert.Equal(lastRunFinishedAt, s2.GetProperty("lastRunFinishedAt").GetDateTime());
    }

    private static async Task<string> WaitForRunFolderAsync(string runsRoot)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(runsRoot))
            {
                var dirs = Directory.GetDirectories(runsRoot);
                if (dirs.Length > 0) return new DirectoryInfo(dirs.OrderByDescending(d => Directory.GetCreationTimeUtc(d)).First()).FullName;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"No run folder appeared under {runsRoot} within 30s");
    }

    private static async Task WaitForFileAsync(string path, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return;
            await Task.Delay(200);
        }
        var dir = Path.GetDirectoryName(path);
        var files = dir != null && Directory.Exists(dir)
            ? string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName))
            : "(run folder missing)";
        var summaryPath = dir == null ? null : Path.Combine(dir, "summary.md");
        var pullPath = dir == null ? null : Path.Combine(dir, "pull-output.txt");
        var summary = summaryPath != null && File.Exists(summaryPath) ? File.ReadAllText(summaryPath) : "";
        var pull = pullPath != null && File.Exists(pullPath) ? File.ReadAllText(pullPath) : "";
        throw new TimeoutException(
            $"File {path} did not appear within {timeoutMs} ms. Files: {files}. Summary: {summary}. Pull: {pull}");
    }

    private static async Task WaitForModeWriteAsync(FakeBackendHarness backend, string project, string mode, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (backend.ModeWrites.Any(w => w.Project == project && w.Mode == mode)) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Mode write {project} -> {mode} did not happen within {timeoutMs} ms");
    }
}
