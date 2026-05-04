using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.Companion;

/// <summary>
/// Outbound-only companion sync. Every <see cref="CompanionSyncOptions.SyncIntervalSeconds"/>
/// the service builds a snapshot from the same in-process state the desktop UI
/// reads and POSTs it to the relay's <c>/sync</c> endpoint. The response carries
/// the commands the PWA enqueued; each is dispatched through
/// <see cref="CompanionCommandDispatcher"/>, then acknowledged on the next tick
/// so the relay can drop it from its queue.
///
/// Default-off (<see cref="CompanionSyncOptions.Enabled"/> = false in
/// <c>appsettings.json</c>); a fresh checkout never tries to phone home.
/// Architectural rationale lives in
/// <c>docs/companion-app-design.md</c> and ADR-0018.
/// </summary>
public sealed class CompanionSyncService : BackgroundService
{
    private readonly IOptionsMonitor<CompanionSyncOptions> _options;
    private readonly JobScannerService _jobs;
    private readonly TaskRunnerService _runner;
    private readonly OrchestratorApi.Services.Quota.QuotaService _quota;
    private readonly OrchestratorApi.Services.Runner.TokenSummaryService _tokens;
    private readonly CompanionCommandDispatcher _dispatcher;
    private readonly ILogger<CompanionSyncService> _log;
    private readonly IHttpClientFactory _httpFactory;

    private readonly object _ackGate = new();
    private List<string> _pendingAcks = new();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public CompanionSyncService(
        IOptionsMonitor<CompanionSyncOptions> options,
        JobScannerService jobs,
        TaskRunnerService runner,
        OrchestratorApi.Services.Quota.QuotaService quota,
        OrchestratorApi.Services.Runner.TokenSummaryService tokens,
        CompanionCommandDispatcher dispatcher,
        IHttpClientFactory httpFactory,
        ILogger<CompanionSyncService> log)
    {
        _options = options;
        _jobs = jobs;
        _runner = runner;
        _quota = quota;
        _tokens = tokens;
        _dispatcher = dispatcher;
        _httpFactory = httpFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait one beat on cold start so the rest of the host is ready.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.CurrentValue;
            var interval = TimeSpan.FromSeconds(opts.ResolvedIntervalSeconds());

            if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.RelayUrl) || string.IsNullOrWhiteSpace(opts.Token))
            {
                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await TickAsync(opts, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Companion sync tick failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task TickAsync(CompanionSyncOptions opts, CancellationToken ct)
    {
        var snapshot = BuildSnapshot(opts);

        // Drain pending acks atomically so a parallel dispatch round (next
        // tick) does not lose ids. The relay only drops queued commands when
        // it sees their id in ackIds, so it is safe to ack on the tick after
        // the dispatch round that handled them.
        List<string> ackIds;
        lock (_ackGate)
        {
            ackIds = _pendingAcks;
            _pendingAcks = new List<string>();
        }

        var req = new CompanionSyncRequest { Snapshot = snapshot, AckIds = ackIds };

        using var client = CreateClient(opts);
        using var resp = await client.PostAsJsonAsync("/sync", req, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CompanionSyncResponse>(JsonOpts, ct);

        if (body?.Commands is { Count: > 0 } cmds)
        {
            foreach (var cmd in cmds)
            {
                var result = await _dispatcher.DispatchAsync(cmd, ct);
                _log.LogInformation(
                    "Companion command {Kind} ({Id}) -> {Applied}: {Message}",
                    cmd.Kind, cmd.Id, result.Applied ? "applied" : "rejected", result.Message);

                // Even rejected commands must be acked so the relay does not
                // keep re-delivering a poison pill; the dispatcher logged why.
                lock (_ackGate) _pendingAcks.Add(cmd.Id);
            }
        }
    }

    internal CompanionSnapshotEnvelope BuildSnapshot(CompanionSyncOptions opts)
    {
        var jobs = SafeScan();
        var runner = _runner.GetStatus();
        var quota = SafeQuota();
        var tokens = AggregateTokens();
        var host = new CompanionHost
        {
            Name = string.IsNullOrEmpty(opts.HostName) ? Environment.MachineName : opts.HostName,
            IsDev = opts.IsDev,
            Version = ThisAssemblyVersion(),
        };
        return CompanionSnapshotBuilder.Build(jobs, runner, quota, tokens, host, DateTimeOffset.UtcNow);
    }

    private IReadOnlyList<OrchestratorApi.Models.JobInfo> SafeScan()
    {
        try { return _jobs.ScanAllJobs(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Companion: ScanAllJobs failed; sending empty job list");
            return Array.Empty<OrchestratorApi.Models.JobInfo>();
        }
    }

    private OrchestratorApi.Models.QuotaReport? SafeQuota()
    {
        try { return _quota.GetCached(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Companion: GetCached quota failed");
            return null;
        }
    }

    private CompanionTokens AggregateTokens()
    {
        long input = 0, output = 0, cacheRead = 0, cacheCreate = 0;
        int calls = 0;
        try
        {
            foreach (var entry in _jobs.GetWatchPaths())
            {
                var summary = _tokens.Summarize(entry.Name, entry.Path);
                input += summary.TotalInputTokens;
                output += summary.TotalOutputTokens;
                cacheRead += summary.TotalCacheReadTokens;
                cacheCreate += summary.TotalCacheCreationTokens;
                calls += summary.OrchestratorLlmCalls;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Companion: token aggregation failed");
        }
        return new CompanionTokens
        {
            TotalCalls = calls,
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheCreateTokens = cacheCreate,
        };
    }

    private HttpClient CreateClient(CompanionSyncOptions opts)
    {
        var client = _httpFactory.CreateClient("companion-relay");
        client.BaseAddress = new Uri(opts.RelayUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.Token);
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    private static string ThisAssemblyVersion() =>
        typeof(CompanionSyncService).Assembly.GetName().Version?.ToString() ?? "dev";
}
