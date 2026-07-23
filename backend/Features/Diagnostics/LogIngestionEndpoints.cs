using System.Text;

namespace AgentStudio.Diagnostics;

/// <summary>
/// Runner → Server log-ingestion API under <c>/api/runner/logs</c> (Step 6).
/// A Runner — local today, remote tomorrow — ships the output lines it produced
/// for a task and the server appends them to that task's durable
/// <c>logs/cli-output.log</c>, in the same <c>[HH:mm:ss.fff] [stream] text</c>
/// format the in-process consolidation writes, so the existing projection and
/// the ~7 durable-log readers consume it unchanged.
///
/// <para>
/// File-backed by design: the durable log is the source of truth for history
/// and cross-machine reads, while a run in progress is still read locally and
/// directly (see RunLogStore / per-stream files). The append is FileShare-safe
/// so a concurrent reader never trips the Windows "file in use" failure.
/// </para>
/// </summary>
public static class LogIngestionEndpoints
{
    public static void MapLogIngestionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/runner/logs", (
            LogIngestRequest req,
            HttpContext context,
            ITaskScanner scanner,
            RunLeaseService leases,
            AttemptAuthorityService authority) =>
        {
            if (!RunnerLeaseAuthorization.IsCurrent(context, leases, req.TaskKey, req.RunnerId, req.LeaseId, req.FencingToken))
                return Results.Conflict(new LogIngestResponse(req.TaskKey, 0, "The authenticated Runner does not hold the current fenced lease."));
            if (req.Lines is null || req.Lines.Count == 0)
                return Results.Ok(new LogIngestResponse(req.TaskKey, 0, "no lines"));

            var folder = ResolveFolder(scanner, req.TaskKey);
            if (folder is null)
                return Results.NotFound(new LogIngestResponse(req.TaskKey, 0, $"No task '{req.TaskKey}'."));

            var logsDir = Path.Combine(folder, "logs");
            var logPath = Path.Combine(logsDir, "cli-output.log");

            var rendered = string.Join(Environment.NewLine,
                req.Lines.Select(l => $"[{l.Timestamp:HH:mm:ss.fff}] [{l.Stream}] {CredentialRedactor.Redact(AnsiText.Strip(l.Text))}"));

            try
            {
                void Append()
                {
                    Directory.CreateDirectory(logsDir);
                    var hasContent = File.Exists(logPath) && new FileInfo(logPath).Length > 0;
                    var payload = (hasContent ? Environment.NewLine : string.Empty) + rendered;
                    // FileShare.ReadWrite: the durable log is read concurrently by the
                    // projection + activity-log endpoint; an exclusive open would 500 them.
                    using var fs = new FileStream(
                        logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                var authorityResult = Authorize(req, authority, Append);
                if (authorityResult is not null)
                {
                    if (authorityResult.Status == AttemptWriteStatus.Duplicate)
                        return Results.Ok(new LogIngestResponse(req.TaskKey, 0, "duplicate delivery"));
                    if (authorityResult.Status != AttemptWriteStatus.Accepted)
                        return Results.Conflict(authorityResult);
                }
                else
                {
                    Append();
                }
            }
            catch (Exception ex)
            {
                return Results.Problem(CredentialRedactor.Redact($"Failed to ingest logs for '{req.TaskKey}': {ex.Message}"));
            }

            return Results.Ok(new LogIngestResponse(req.TaskKey, req.Lines.Count));
        });
    }

    private static AttemptWriteResult? Authorize(
        LogIngestRequest req,
        AttemptAuthorityService authority,
        Action append)
    {
        var projection = authority.GetTaskProjection(req.TaskKey);
        if (string.IsNullOrWhiteSpace(req.AttemptId) || !req.Fence.HasValue
            || !req.AuthorityEpoch.HasValue || string.IsNullOrWhiteSpace(req.IdempotencyKey))
        {
            return projection.LegacyTask
                ? null
                : new AttemptWriteResult(AttemptWriteStatus.Invalid, req.AttemptId ?? string.Empty,
                    "Canonical runner writes require AttemptId, Fence, AuthorityEpoch, and IdempotencyKey.");
        }
        return authority.ExecuteRunWrite(
            new AttemptWriteReference(
                req.AttemptId, req.Fence.Value, req.AuthorityEpoch.Value, req.IdempotencyKey),
            "log",
            req.TaskKey,
            append);
    }

    private static string? ResolveFolder(ITaskScanner scanner, string taskKey)
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return null;
        var task = scanner.ScanAllJobs().FirstOrDefault(t =>
            string.Equals(t.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Id, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Key, taskKey, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(task?.FolderPath) ? null : task!.FolderPath;
    }
}
