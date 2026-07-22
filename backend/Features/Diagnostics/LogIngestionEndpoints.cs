using System.Collections.Concurrent;
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
    internal const int CliOutputLogCapBytes = 8 * 1024 * 1024;
    private const int DeliveryReceiptTailBytes = 256 * 1024;
    private static readonly ConcurrentDictionary<string, object> LogWriteLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static void MapLogIngestionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/runner/logs", (
            LogIngestRequest req,
            HttpContext context,
            ITaskScanner scanner,
            RunLeaseService leases,
            AttemptAuthorityService authority,
            AgentStudio.Docs.WikiAgentReadService wikiReads) =>
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
            var deliveryReceipt = DeliveryReceipt(req);

            try
            {
                void Append()
                {
                    Directory.CreateDirectory(logsDir);
                    var receiptLine = deliveryReceipt is null
                        ? string.Empty
                        : Environment.NewLine
                          + $"[{req.Lines[^1].Timestamp:HH:mm:ss.fff}] [system] {deliveryReceipt}";
                    AppendBounded(
                        logPath,
                        rendered + receiptLine,
                        deliveryReceipt,
                        req.Lines[^1].Timestamp);
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

            // Remote runs do not flow through the in-process CliRouter. Attribute
            // their tool-use lines only after the durable, fenced append succeeds.
            try
            {
                wikiReads.ProcessOutput(req.TaskKey, req.Lines.Select(line => new AgentStudio.Cli.CliOutputLine
                {
                    Timestamp = line.Timestamp,
                    Stream = line.Stream,
                    Text = line.Text,
                }));
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "WikiAgentReadService: remote log attribution failed.");
            }

            return Results.Ok(new LogIngestResponse(req.TaskKey, req.Lines.Count));
        });
    }

    private static AttemptWriteResult? Authorize(
        LogIngestRequest req,
        AttemptAuthorityService authority,
        Action append)
    {
        if (string.IsNullOrWhiteSpace(req.AttemptId) || !req.Fence.HasValue
            || !req.AuthorityEpoch.HasValue || string.IsNullOrWhiteSpace(req.IdempotencyKey))
        {
            // Log ingest is best-effort diagnostic, not an authoritative write: append even
            // without canonical write authority so a runner on an older agent-host protocol
            // (no AttemptId/Fence/AuthorityEpoch/IdempotencyKey) does not 409-storm and stall
            // its runs. Durable authoritative state lives elsewhere; a log line needs no fencing.
            return null;
        }
        return authority.ExecuteRunWrite(
            new AttemptWriteReference(
                req.AttemptId, req.Fence.Value, req.AuthorityEpoch.Value, req.IdempotencyKey),
            "log",
            req.TaskKey,
            append);
    }

    private static string? DeliveryReceipt(LogIngestRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.AttemptId)
            || string.IsNullOrWhiteSpace(req.IdempotencyKey))
        {
            return null;
        }

        var digest = AttemptAuthorityService.Hash(
            $"log\n{req.AttemptId.Trim()}\n{req.IdempotencyKey.Trim()}");
        return $"[runner-log-delivery:{digest}]";
    }

    private static bool ContainsDeliveryReceipt(string logPath, string receipt)
    {
        if (!File.Exists(logPath)) return false;
        var tail = TaskScannerService.ReadTailUtf8(logPath, DeliveryReceiptTailBytes);
        foreach (var line in tail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.EndsWith($"[system] {receipt}", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Appends one rendered delivery while keeping the durable CLI log below
    /// the server-side 8 MiB cap. Rotation retains the newest complete UTF-8
    /// lines and writes an explicit marker before them. Per-path locking keeps
    /// concurrent deliveries from racing the size check and rewrite.
    /// </summary>
    internal static bool AppendBounded(
        string logPath,
        string renderedPayload,
        string? deliveryReceipt,
        DateTime markerTimestamp)
    {
        var gate = LogWriteLocks.GetOrAdd(Path.GetFullPath(logPath), static _ => new object());
        lock (gate)
        {
            if (deliveryReceipt is not null
                && ContainsDeliveryReceipt(logPath, deliveryReceipt))
            {
                return false;
            }

            var hasContent = File.Exists(logPath) && new FileInfo(logPath).Length > 0;
            var payload = (hasContent ? Environment.NewLine : string.Empty) + renderedPayload;
            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            var existingLength = hasContent ? new FileInfo(logPath).Length : 0;

            if (existingLength + payloadBytes.Length <= CliOutputLogCapBytes)
            {
                Write(logPath, FileMode.Append, payloadBytes);
                return true;
            }

            var marker =
                $"[{markerTimestamp:HH:mm:ss.fff}] [system] [cli-output-rotated] "
                + $"Server retained the newest log tail after the {CliOutputLogCapBytes / (1024 * 1024)} MiB cap was reached."
                + Environment.NewLine;
            var markerBytes = Encoding.UTF8.GetBytes(marker);
            var tailBudget = Math.Max(0, CliOutputLogCapBytes - markerBytes.Length);
            var existingTailBudget = Math.Max(0, tailBudget - Math.Min(tailBudget, payloadBytes.Length));
            var existingTail = existingTailBudget == 0
                ? string.Empty
                : TaskScannerService.ReadTailUtf8(logPath, existingTailBudget);
            var retained = FitUtf8Tail(existingTail + payload, tailBudget);
            var retainedBytes = Encoding.UTF8.GetBytes(retained);
            var rotated = new byte[markerBytes.Length + retainedBytes.Length];
            Buffer.BlockCopy(markerBytes, 0, rotated, 0, markerBytes.Length);
            Buffer.BlockCopy(retainedBytes, 0, rotated, markerBytes.Length, retainedBytes.Length);
            Write(logPath, FileMode.Create, rotated);
            return true;
        }
    }

    private static string FitUtf8Tail(string content, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrEmpty(content)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length <= maxBytes) return content;

        var start = bytes.Length - maxBytes;
        var decoded = Encoding.UTF8.GetString(bytes, start, maxBytes);
        var newline = decoded.IndexOf('\n');
        if (newline >= 0)
            decoded = decoded[(newline + 1)..];

        // A replacement rune at a cut UTF-8 boundary can re-encode to more
        // bytes than the source slice. Trim characters until the hard cap is
        // guaranteed.
        while (decoded.Length > 0 && Encoding.UTF8.GetByteCount(decoded) > maxBytes)
            decoded = decoded[1..];
        return decoded;
    }

    private static void Write(string logPath, FileMode mode, byte[] bytes)
    {
        // FileShare.ReadWrite: projection and activity-log readers stay live
        // while append or bounded rotation is in progress.
        using var stream = new FileStream(
            logPath, mode, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
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
