using System.Text;
using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// Shared append-only persistence for plan frames received from the local CLI
/// router or the remote runner log-ingestion boundary.
/// </summary>
internal static class PlanSnapshotLog
{
    public static bool Append(
        string jobFolder,
        CliRunEvent.PlanUpdated plan,
        DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(jobFolder) || plan.Items.Count == 0) return false;

        var logsDir = Path.Combine(jobFolder, "logs");
        try { Directory.CreateDirectory(logsDir); }
        catch { return false; }
        var path = Path.Combine(logsDir, "plan-snapshots.jsonl");

        var seenActive = false;
        var items = new List<object>(plan.Items.Count);
        var signature = new StringBuilder();
        foreach (var item in plan.Items)
        {
            var status = PlanItemStatus.Normalize(item.Status);
            if (status == "active")
            {
                if (seenActive) status = "pending";
                else seenActive = true;
            }
            items.Add(new { id = item.Id, title = item.Title, status });
            signature.Append(item.Id).Append('=').Append(status).Append(';');
        }

        var seq = 1;
        try
        {
            if (File.Exists(path))
            {
                string? lastLine = null;
                foreach (var raw in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    seq++;
                    lastLine = raw;
                }
                if (lastLine != null && SnapshotSignature(lastLine) == signature.ToString()) return false;
            }
        }
        catch (Exception ex)
        {
            // A torn prior snapshot must not prevent the current frame from
            // becoming visible. Append with the best available sequence.
            SilentCatch.Note(ex, "PlanSnapshotLog: ignore torn prior snapshot");
        }

        var record = new { ts = timestamp.ToUniversalTime(), seq, source = plan.Source, items };
        try
        {
            File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SnapshotSignature(string jsonLine)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonLine.TrimStart('﻿'));
            if (!document.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var signature = new StringBuilder();
            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                var status = item.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
                signature.Append(id).Append('=').Append(status).Append(';');
            }
            return signature.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}
