using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.DemoReplayRunner;

/// <summary>Result of one completed cycle, used for logging and for the exit contract.</summary>
public sealed record ReplayCycleReport(long Epoch, int Emitted, int Denied);

/// <summary>
/// Drives cycles of the fixed trace. A denial never stops the service: the
/// server is authoritative and a public demo that keeps serving a stale scene is
/// better than one that dies. A transport failure is retried on the next cycle.
/// </summary>
public sealed class ReplaySession
{
    private readonly ReplayClient _client;
    private readonly DemoReplaySignedTrace _trace;
    private readonly ReplayOptions _options;
    private readonly Action<string> _log;

    public ReplaySession(ReplayClient client, DemoReplaySignedTrace trace, ReplayOptions options, Action<string> log)
    {
        _client = client;
        _trace = trace;
        _options = options;
        _log = log;
    }

    public async Task<ReplayCycleReport> RunCycleAsync(long epoch, DateTime cycleStartUtc, CancellationToken ct)
    {
        var plan = ReplayCycle.Plan(_trace, epoch, _options.SpeedFactor, cycleStartUtc);
        var started = DateTime.UtcNow;
        var emitted = 0;
        var denied = 0;
        foreach (var step in plan)
        {
            var due = started + step.Delay;
            var wait = due - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

            ReplayPostOutcome outcome;
            try
            {
                outcome = await _client.PostFrameAsync(step.Request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                denied++;
                _log($"frame sequence={step.Request.Frame.Sequence} transport-failed reason={ex.Message}");
                continue;
            }

            if (outcome.Accepted)
            {
                emitted++;
            }
            else
            {
                denied++;
                _log($"frame sequence={step.Request.Frame.Sequence} denied status={outcome.StatusCode} code={outcome.DenialCode ?? "unknown"}");
            }
        }
        return new ReplayCycleReport(epoch, emitted, denied);
    }

    public async Task RunAsync(bool once, CancellationToken ct)
    {
        var epoch = _options.StartEpoch;
        var cycleLength = ReplayCycle.Duration(_trace, _options.SpeedFactor);
        _log($"replay starting trace={_trace.Trace.TraceId} digest={_trace.Digest[..12]} frames={_trace.Trace.Frames.Count} cycle-seconds={cycleLength.TotalSeconds:F0}");
        while (!ct.IsCancellationRequested)
        {
            var report = await RunCycleAsync(epoch, DateTime.UtcNow, ct);
            _log($"cycle complete epoch={report.Epoch} emitted={report.Emitted} denied={report.Denied}");
            if (once) return;
            epoch++;
            if (_options.CyclePauseSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(_options.CyclePauseSeconds), ct);
        }
    }
}
