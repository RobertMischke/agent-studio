using System.Collections.Concurrent;

namespace AgentStudio.Runtime;

/// <summary>
/// File-backed, in-memory projection of Product Runtime Observability events
/// for the project surface. Disk is the source of truth: many small JSONL
/// files under <c>{workspace}/logs/runtime/{project|_workspace}/{yyyy-mm-dd}.jsonl</c>.
/// Reads are served from a per-(workspace, project) snapshot so UI-polled
/// endpoints never trigger a full rescan, matching the "no full log rescans
/// on poll" rule from the capture contract.
/// </summary>
/// <remarks>
/// <para>
/// The store loads a project lazily on first access. <see cref="InvalidateProjection"/>
/// drops the cache so external writers or tests planting fixture files become
/// visible without a backend restart.
/// </para>
/// <para>
/// Parse warnings come from <see cref="RuntimeEventReader"/> and are surfaced
/// alongside events so the UI can render a malformed-data state without the
/// reviewer having to open the JSONL by hand.
/// </para>
/// </remarks>
public sealed class ProductRuntimeEventStore
{
    private readonly ConcurrentDictionary<ProjectionKey, Projection> _projections = new();
    private readonly RuntimeEventReader _reader = new();

    public void InvalidateProjection(string workspaceRoot, string? project)
    {
        _projections.TryRemove(new ProjectionKey(workspaceRoot, project), out _);
    }

    public RuntimeEventSnapshot GetSnapshot(string workspaceRoot, string? project, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var projection = _projections.GetOrAdd(
            new ProjectionKey(workspaceRoot, project),
            k => Load(k, ct));
        return new RuntimeEventSnapshot(projection.Events, projection.Warnings);
    }

    private Projection Load(ProjectionKey key, CancellationToken ct)
    {
        var events = new List<ProductRuntimeEvent>();
        var warnings = new List<RuntimeEventParseWarning>();

        var dir = RuntimeEventPaths.WorkspaceProjectDir(key.WorkspaceRoot, key.Project);
        if (Directory.Exists(dir))
        {
            // Sort by file name so dates load in order. Within a file, the
            // reader keeps file order.
            var files = Directory.EnumerateFiles(dir, "*.jsonl")
                .Where(f => !f.EndsWith(".warnings.jsonl", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var result = _reader.Read(file);
                events.AddRange(result.Events);
                warnings.AddRange(result.Warnings);
            }
        }

        // Stable order: timestamp ascending. Producers writing in clock order
        // and lexical filename order already converge on this; sorting once
        // here makes the contract explicit for readers that mix days.
        events.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));

        return new Projection(events, warnings);
    }

    private readonly record struct ProjectionKey(string WorkspaceRoot, string? Project);

    private sealed record Projection(
        IReadOnlyList<ProductRuntimeEvent> Events,
        IReadOnlyList<RuntimeEventParseWarning> Warnings);
}

/// <summary>
/// Read-side bundle returned to the UI: a chronological event list plus the
/// parse-warning list collected while loading the projection. The UI uses
/// the warning list to render a malformed-data badge without having to open
/// the source JSONL by hand.
/// </summary>
public sealed record RuntimeEventSnapshot(
    IReadOnlyList<ProductRuntimeEvent> Events,
    IReadOnlyList<RuntimeEventParseWarning> Warnings);
