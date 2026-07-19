using AgentStudio.Runner;

namespace AgentStudio.Cli;

/// <summary>Queues support calls during sustained CPU saturation and retries one load-related timeout.</summary>
public sealed class LoadAwareCliOneShot : ICliOneShot
{
    private readonly ICliOneShot _inner;
    private readonly ILoadThrottleGate _load;
    private readonly ILogger<LoadAwareCliOneShot> _logger;

    public LoadAwareCliOneShot(ICliOneShot inner, ILoadThrottleGate load, ILogger<LoadAwareCliOneShot> logger)
    {
        _inner = inner;
        _load = load;
        _logger = logger;
    }

    public string CliType => _inner.CliType;

    public async Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
    {
        var operation = request.Source ?? request.StepId ?? "support-one-shot";
        await _load.WaitUntilReadyAsync(operation, ct).ConfigureAwait(false);
        var loadGuarded = _load.WasRecentlyActive;
        var effective = loadGuarded ? WithLoadTimeout(request) : request;
        var result = await _inner.RunAsync(effective, ct).ConfigureAwait(false);
        if (!IsTimeout(result) || (!loadGuarded && !_load.Current.Throttle)) return result;

        _logger.LogWarning("support_one_shot_retry category=environmental-load operation={Operation} firstError={Error}", operation, result.Error);
        await _load.WaitUntilReadyAsync(operation + ":retry", ct).ConfigureAwait(false);
        var retry = await _inner.RunAsync(WithLoadTimeout(request), ct).ConfigureAwait(false);
        return IsTimeout(retry)
            ? retry with { Error = "environmental-load: " + retry.Error }
            : retry;
    }

    private static CliOneShotRequest WithLoadTimeout(CliOneShotRequest request)
        => request with { Timeout = TimeSpan.FromTicks((request.Timeout ?? TimeSpan.FromMinutes(2)).Ticks * 3) };

    private static bool IsTimeout(CliOneShotResult result)
        => !result.Ok && result.Error?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true;
}
