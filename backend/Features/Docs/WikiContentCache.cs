using System.Collections.Concurrent;

namespace AgentStudio.Docs;

/// <summary>
/// Central in-memory projection of one project's complete wiki read model.
/// A fill performs the filesystem work once; every wiki endpoint reads the
/// published stable snapshot without validating it through another docs/
/// walk. Invalidations rebuild synchronously before returning, so a reader
/// arriving after a watcher event or API mutation never pays a cold fill and
/// never observes the pre-mutation snapshot.
/// </summary>
public sealed class WikiContentCache
{
    private readonly Func<string, WikiContentSnapshot?> _build;
    private readonly Func<string, string> _normalizeProjectKey;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, CacheSlot> _slots =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class CacheSlot
    {
        public readonly Lock Gate = new();
        public WikiContentSnapshot? Snapshot;
    }

    public long Hits;
    public long Fills;
    public long WatcherInvalidations;
    public long MutationInvalidations;

    public WikiContentCache(ProjectDocsService docs, ILogger<WikiContentCache> logger)
        : this(docs.BuildWikiContentSnapshotRaw, logger, docs.ResolveWikiCacheKey)
    {
    }

    internal WikiContentCache(
        Func<string, WikiContentSnapshot?> build,
        ILogger? logger = null,
        Func<string, string>? normalizeProjectKey = null)
    {
        _build = build;
        _logger = logger;
        _normalizeProjectKey = normalizeProjectKey ?? (projectName => projectName);
    }

    internal WikiContentSnapshot? GetSnapshot(string projectName)
    {
        projectName = _normalizeProjectKey(projectName);
        var slot = _slots.GetOrAdd(projectName, _ => new CacheSlot());
        lock (slot.Gate)
        {
            if (slot.Snapshot != null)
            {
                Interlocked.Increment(ref Hits);
                return slot.Snapshot;
            }

            return FillLocked(projectName, slot, "cold");
        }
    }

    /// <summary>Fills a project during startup or project registration.</summary>
    public bool Preload(string projectName) => GetSnapshot(projectName) != null;

    /// <summary>
    /// Rebuilds immediately and publishes only after the complete replacement
    /// snapshot is ready. The old snapshot remains unreachable once this method
    /// returns, which is the cache's read-after-write boundary.
    /// </summary>
    public void Invalidate(
        string projectName,
        InvalidationSource source = InvalidationSource.Mutation)
    {
        projectName = _normalizeProjectKey(projectName);
        if (source == InvalidationSource.Watcher)
            Interlocked.Increment(ref WatcherInvalidations);
        else
            Interlocked.Increment(ref MutationInvalidations);

        var slot = _slots.GetOrAdd(projectName, _ => new CacheSlot());
        lock (slot.Gate)
            _ = FillLocked(projectName, slot, source == InvalidationSource.Watcher ? "watcher" : "mutation");
    }

    internal void InvalidateAll()
    {
        foreach (var projectName in _slots.Keys)
            Invalidate(projectName, InvalidationSource.Mutation);
    }

    private WikiContentSnapshot? FillLocked(string projectName, CacheSlot slot, string reason)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var snapshot = _build(projectName);
            slot.Snapshot = snapshot;
            Interlocked.Increment(ref Fills);
            _logger?.LogInformation(
                "wiki-cache-fill project={Project} reason={Reason} elapsedMs={ElapsedMs} files={Files} folders={Folders}",
                projectName,
                reason,
                started.ElapsedMilliseconds,
                snapshot?.Files.Count ?? 0,
                snapshot?.Folders.Count ?? 0);
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "wiki-cache-fill-failed project={Project} reason={Reason}", projectName, reason);
            throw;
        }
    }

    public enum InvalidationSource
    {
        Watcher,
        Mutation,
    }
}

internal sealed record WikiContentSnapshot(
    string ProjectName,
    WikiSourceContext Source,
    string WikiDir,
    string Signature,
    WikiTreeResult TreeResult,
    List<WikiFileEntry> Files,
    IReadOnlyDictionary<string, WikiFileEntry> FilesByRelPath,
    IReadOnlyDictionary<string, WikiTreeMetadata> MetadataByRelPath,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FolderOrderByParent,
    IReadOnlyDictionary<string, WikiFolderView> Folders,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FolderDescendantPages,
    IReadOnlyDictionary<string, string?> TaskKeysByRelPath,
    WikiHomeView Home,
    WikiPulseLifecycle Lifecycle,
    WikiPulseInbox Inbox,
    WikiPulseCritical Critical,
    WikiPulseWarnings Warnings,
    WorkbenchCatalogue? Workbenches);
