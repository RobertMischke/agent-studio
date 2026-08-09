using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Projection;

/// <summary>
/// Coordinator. Asks each <see cref="IConversationEventSource"/> for raw
/// events, renders bodies through <see cref="IMarkdownRenderer"/>, sorts
/// by timestamp, caches the result against the source mtime tuple, and -
/// optionally - broadcasts deltas over <see cref="TaskHub"/> so live
/// listeners do not need to poll.
///
/// The projector is the only component aware of <see cref="TaskInfo"/>.
/// Sources see <see cref="TaskInfo"/> too but only to find their files on
/// disk; the renderer and cache stay pure.
/// </summary>
public sealed class ConversationProjector
{
    private readonly IReadOnlyList<IConversationEventSource> _sources;
    private readonly IMarkdownRenderer _renderer;
    private readonly ConversationCache _cache;
    private readonly TaskScannerService _scanner;
    private readonly IHubContext<TaskHub>? _hub;
    private readonly ILogger<ConversationProjector> _logger;

    public ConversationProjector(
        IEnumerable<IConversationEventSource> sources,
        IMarkdownRenderer renderer,
        ConversationCache cache,
        TaskScannerService scanner,
        IHubContext<TaskHub>? hub,
        ILogger<ConversationProjector> logger)
    {
        _sources = sources.ToList();
        _renderer = renderer;
        _cache = cache;
        _scanner = scanner;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// Full projection for a job. Resolves the job, gathers source events,
    /// renders, sorts, caches. <paramref name="opts"/> may restrict to
    /// events newer than <see cref="ProjectionOptions.SinceUtc"/>.
    /// </summary>
    public async Task<IReadOnlyList<ProjectedEvent>> ProjectAsync(
        string jobId,
        string? watchPath,
        ProjectionOptions opts,
        CancellationToken ct)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info is null) return Array.Empty<ProjectedEvent>();

        var (events, _) = await ProjectInternalAsync(info, ct).ConfigureAwait(false);
        if (opts.SinceUtc is { } since)
        {
            events = events.Where(e => e.TimestampUtc > since).ToList();
        }
        return events;
    }

    /// <summary>
    /// Re-project, compare against the cached snapshot, and broadcast any
    /// new tail events on <see cref="TaskHub"/> so the file watcher can
    /// stream live appends to subscribers without re-fetching.
    /// </summary>
    public async Task ProjectAndBroadcastAsync(TaskInfo info, CancellationToken ct)
    {
        if (_hub is null) return;

        // Snapshot the previously-cached events (if any) BEFORE we re-project,
        // so we can compute a delta against the just-rendered list.
        var previousMTimes = SourceMTimes(info);
        IReadOnlyList<ProjectedEvent>? previous = null;
        if (_cache.TryGet(info.Id, previousMTimes, out var cached))
        {
            previous = cached;
        }

        var (events, _) = await ProjectInternalAsync(info, ct).ConfigureAwait(false);

        if (previous is null || previous.Count == 0)
        {
            // First snapshot we have for this job over the wire. Tell
            // subscribers to refetch the full snapshot rather than pushing a
            // huge event array down the hub. The endpoint will serve from
            // the cache we just populated, so the second fetch is cheap.
            await _hub.Clients.Group(GroupName(info.Id))
                .SendAsync("conversationProjectionInvalidated", info.Id, cancellationToken: ct)
                .ConfigureAwait(false);
            return;
        }

        var lastSeenTs = previous[^1].TimestampUtc;
        var delta = events.Where(e => e.TimestampUtc > lastSeenTs
                                       || !previous.Any(p => p.Id == e.Id)).ToList();
        if (delta.Count == 0) return;

        await _hub.Clients.Group(GroupName(info.Id))
            .SendAsync("conversationEventsAppended", info.Id, delta, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    public void Invalidate(string jobId) => _cache.Invalidate(jobId);

    public static string GroupName(string jobId) => $"conv-{jobId}";

    private async Task<(IReadOnlyList<ProjectedEvent> Events, IReadOnlyDictionary<string, DateTime> MTimes)>
        ProjectInternalAsync(TaskInfo info, CancellationToken ct)
    {
        var mtimes = SourceMTimes(info);
        if (_cache.TryGet(info.Id, mtimes, out var hit))
        {
            return (hit, mtimes);
        }

        var imageCtx = new ImageContext
        {
            JobId = info.Id,
            WatchPath = info.WatchPath
        };

        var collected = new List<RawSourceEvent>();
        foreach (var source in _sources)
        {
            try
            {
                var batch = await source.ReadAsync(info, ct).ConfigureAwait(false);
                collected.AddRange(batch);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Projection source {SourceKind} failed for job {JobId}",
                    source.SourceKind, info.Id);
            }
        }

        var ordered = collected
            .OrderBy(e => e.TimestampUtc)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => Render(e, imageCtx))
            .ToList();

        _cache.Set(info.Id, ordered, mtimes);
        return (ordered, mtimes);
    }

    private IReadOnlyDictionary<string, DateTime> SourceMTimes(TaskInfo info)
    {
        var d = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var s in _sources)
        {
            d[s.SourceKind] = s.GetSourceMTimeUtc(info);
        }
        return d;
    }

    private ProjectedEvent Render(RawSourceEvent raw, ImageContext ctx)
    {
        string html;
        try { html = _renderer.ToHtml(raw.BodyMarkdown, ctx); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Markdown render failed for event {Id}", raw.Id);
            html = System.Net.WebUtility.HtmlEncode(raw.BodyMarkdown);
        }
        return new ProjectedEvent
        {
            Id = raw.Id,
            Kind = raw.Kind,
            TimestampUtc = raw.TimestampUtc,
            SourceKind = raw.SourceKind,
            Role = raw.Role,
            BodyHtml = html,
            Summary = raw.Summary,
            Severity = raw.Severity,
            Refs = raw.Refs,
            Metadata = raw.Metadata
        };
    }
}
