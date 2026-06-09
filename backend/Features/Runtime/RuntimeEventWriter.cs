using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runtime;

/// <summary>
/// Append-only writer for <see cref="ProductRuntimeEvent"/> JSONL files.
/// Adapter-style: callers that ingest events from any source (stdout sniffer,
/// file tail, Playwright console capture) hand a validated event in and the
/// writer routes it to the right job- or workspace-scoped day file.
/// </summary>
/// <remarks>
/// Writes are serialised per-file via a <see cref="SemaphoreSlim"/> so two
/// adapters writing into the same scope cannot interleave bytes inside one
/// JSONL line. Embedded newlines in serialised JSON are stripped before
/// append; the schema bans them and breaking the one-event-per-line invariant
/// would silently corrupt every reader, including <see cref="RuntimeEventReader"/>.
/// </remarks>
public sealed class RuntimeEventWriter
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    public Task AppendToJobAsync(string jobFolderPath, ProductRuntimeEvent evt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolderPath);
        var path = RuntimeEventPaths.TaskDayFile(jobFolderPath, evt.Timestamp);
        return AppendAsync(path, evt, ct);
    }

    public Task AppendToWorkspaceAsync(string workspaceRoot, string? project, ProductRuntimeEvent evt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var path = RuntimeEventPaths.WorkspaceDayFile(workspaceRoot, project, evt.Timestamp);
        return AppendAsync(path, evt, ct);
    }

    public async Task AppendAsync(string path, ProductRuntimeEvent evt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(evt);
        if (!RuntimeEventValidator.TryValidate(evt, out var error))
            throw new InvalidOperationException($"ProductRuntimeEvent rejected: {error}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var line = JsonSerializer.Serialize(evt, RuntimeEventReader.JsonOptions);
        if (line.Contains('\n')) line = line.Replace("\r", string.Empty).Replace("\n", " ");
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        var sem = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Records a parse warning for a malformed input line into the sidecar
    /// <c>&lt;path&gt;.warnings.jsonl</c> so producers can be debugged
    /// without rerunning the failing scenario. The sidecar shape is
    /// intentionally simple JSON (sourcePath, lineNumber, reason, rawLine)
    /// and not a <see cref="ProductRuntimeEvent"/>: a malformed event is
    /// not itself an event.
    /// </summary>
    public async Task AppendWarningAsync(string runtimeJsonlPath, RuntimeEventParseWarning warning, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeJsonlPath);
        ArgumentNullException.ThrowIfNull(warning);

        var sidecar = RuntimeEventPaths.WarningsFile(runtimeJsonlPath);
        Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);

        var record = new
        {
            sourcePath = warning.SourcePath,
            lineNumber = warning.LineNumber,
            reason = warning.Reason,
            rawLine = warning.RawLine,
            recordedAt = DateTime.UtcNow,
        };
        var line = JsonSerializer.Serialize(record, RuntimeEventReader.JsonOptions);
        if (line.Contains('\n')) line = line.Replace("\r", string.Empty).Replace("\n", " ");
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        var sem = _fileLocks.GetOrAdd(sidecar, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                sidecar, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }
}
