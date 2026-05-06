namespace AgentTaskboard.UpdateService;

/// <summary>
/// Background ticker that keeps the published status fresh: every N seconds
/// it (1) re-reads HEAD, (2) fetches origin/main and computes BehindBy,
/// (3) probes the main backend's healthz. The orchestrator's phase remains
/// untouched here; this service only updates the bookkeeping fields.
/// </summary>
public sealed class PeriodicProbeService : BackgroundService
{
    private readonly UpdateStatusStore _store;
    private readonly GitProbe _git;
    private readonly BackendProbe _backend;
    private readonly UpdateServiceOptions _options;
    private readonly ILogger<PeriodicProbeService> _logger;

    public PeriodicProbeService(
        UpdateStatusStore store,
        GitProbe git,
        BackendProbe backend,
        UpdateServiceOptions options,
        ILogger<PeriodicProbeService> logger)
    {
        _store = store;
        _git = git;
        _backend = backend;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First tick happens shortly after boot so the status surface has
        // populated values when the first /update/status request lands.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var head = _git.HeadShort();
                if (!string.IsNullOrEmpty(head)) _store.SetHead(head);

                var (origin, behindBy) = _git.FetchAndCompare();
                if (!string.IsNullOrEmpty(origin))
                {
                    var pending = behindBy > 0 ? _git.PendingCommits(50) : Array.Empty<CommitInfo>();
                    _store.SetFetchResult(origin, behindBy, pending);
                }

                var healthy = await _backend.IsHealthyAsync(stoppingToken);
                _store.SetBackendReachable(healthy);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "probe tick failed; continuing");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_options.ProbeIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
