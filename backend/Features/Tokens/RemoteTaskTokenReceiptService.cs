using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentStudio.Tokens;

/// <summary>
/// Recovers terminal CLI usage frames from the remote runner's durable output
/// and persists the canonical per-task token receipt. Local runs already emit
/// usage through <see cref="AgentMessageBusBridge"/>; remote runs deliberately
/// use task.json because their process and the project bus live on different
/// hosts.
/// </summary>
public sealed class RemoteTaskTokenReceiptService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly CliUsageParserRegistry _parsers;
    private readonly ICliModelRegistry _models;
    private readonly TaskMutationService _mutations;
    private readonly ILogger<RemoteTaskTokenReceiptService> _logger;
    private readonly ConcurrentDictionary<string, object> _folderLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public RemoteTaskTokenReceiptService(
        CliUsageParserRegistry parsers,
        ICliModelRegistry models,
        TaskMutationService mutations,
        ILogger<RemoteTaskTokenReceiptService> logger)
    {
        _parsers = parsers;
        _models = models;
        _mutations = mutations;
        _logger = logger;
    }

    /// <summary>
    /// Parses newly accepted log lines. Replayed batches are harmless because
    /// receipt calls are deduplicated by timestamp, producer, model, and token
    /// dimensions before the atomic task mutation.
    /// </summary>
    public RemoteTaskTokenReceiptResult Record(
        TaskInfo task,
        IEnumerable<CliOutputLine> lines)
        => Record(task, lines, persistedLogReplay: false);

    private RemoteTaskTokenReceiptResult Record(
        TaskInfo task,
        IEnumerable<CliOutputLine> lines,
        bool persistedLogReplay)
    {
        var parser = _parsers.Get(task.CliType ?? task.Agent);
        if (parser is null)
            return new RemoteTaskTokenReceiptResult(0, 0, false, "No usage parser is registered for the task CLI.");

        var calls = new List<TaskTokenCall>();
        foreach (var line in lines)
        {
            if (!string.Equals(line.Stream, "stdout", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }
            if (!line.Text.AsSpan().TrimStart().StartsWith("{".AsSpan(), StringComparison.Ordinal))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line.Text);
                if (!parser.TryParse(document.RootElement, task.Model, _models, out var usage))
                    continue;

                var total = usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;
                if (total <= 0) continue;

                var timestamp = NormalizeUtc(line.Timestamp);
                var model = usage.Model ?? task.Model;
                var price = TokenPricing.Estimate(
                    model,
                    usage.Input,
                    usage.Output,
                    usage.CacheRead,
                    usage.CacheWrite,
                    timestamp);
                calls.Add(new TaskTokenCall
                {
                    Ts = timestamp,
                    Model = TokenModelDisplay.Label(model),
                    ParticipantId = AgentMessageBusBridge.ParticipantForCli(task.CliType ?? task.Agent),
                    InputTokens = usage.Input,
                    OutputTokens = usage.Output,
                    CacheReadTokens = usage.CacheRead,
                    CacheCreationTokens = usage.CacheWrite,
                    EstimatedApiCostUsd = price.Total,
                    ModelPriced = price.ModelKnown,
                });
            }
            catch (JsonException ex)
            {
                // Most CLI lines are ordinary output or non-usage JSON frames.
                SilentCatch.Note(ex, "RemoteTaskTokenReceiptService: non-JSON CLI output is not a usage frame.");
            }
        }

        if (calls.Count == 0)
            return new RemoteTaskTokenReceiptResult(0, 0, false, null);

        lock (_folderLocks.GetOrAdd(task.FolderPath, _ => new object()))
        {
            var existing = ReadExisting(task.FolderPath);
            var merged = Merge(existing, calls, persistedLogReplay, out var added);
            if (added == 0)
                return new RemoteTaskTokenReceiptResult(calls.Count, 0, false, null);

            var written = _mutations.SetTaskTokenSummaryOnFolder(task.FolderPath, merged);
            if (!written)
            {
                const string warning = "The durable task token receipt could not be written.";
                _logger.LogWarning(
                    "remote-token-receipt-write-failed project={Project} task={Task}",
                    task.ProjectName,
                    task.Key ?? task.Id);
                return new RemoteTaskTokenReceiptResult(calls.Count, 0, false, warning);
            }

            return new RemoteTaskTokenReceiptResult(calls.Count, added, true, null);
        }
    }

    /// <summary>
    /// Replays the bounded durable log tail. Used at completion as a safety net
    /// and by the historical attribution sweep for runs completed before live
    /// receipt production was available.
    /// </summary>
    public RemoteTaskTokenReceiptResult RecordFromTaskLog(TaskInfo task)
        => Record(
            task,
            CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(task.FolderPath)),
            persistedLogReplay: true);

    private static TaskTokenSummary? ReadExisting(string folderPath)
    {
        try
        {
            var path = Path.Combine(folderPath, "task.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "tokenSummary", StringComparison.OrdinalIgnoreCase)
                    || property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    continue;
                }
                return property.Value.Deserialize<TaskTokenSummary>(Json);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SilentCatch.Note(ex, "RemoteTaskTokenReceiptService: existing receipt could not be read.");
        }
        return null;
    }

    private static TaskTokenSummary Merge(
        TaskTokenSummary? existing,
        IReadOnlyList<TaskTokenCall> incoming,
        bool persistedLogReplay,
        out int added)
    {
        var calls = ExistingCalls(existing).OrderBy(call => call.Ts).ToList();
        var keys = calls.Select(call => CallKey.From(call, persistedLogReplay)).ToHashSet();
        added = 0;
        foreach (var call in incoming.OrderBy(call => call.Ts))
        {
            if (!keys.Add(CallKey.From(call, persistedLogReplay))) continue;
            calls.Add(call);
            added++;
        }
        calls = calls.OrderBy(call => call.Ts).ToList();

        var lastAgentModel = calls
            .Where(call => TokenModelDisplay.IsAgentParticipant(call.ParticipantId)
                && !string.IsNullOrWhiteSpace(call.Model))
            .LastOrDefault()?.Model;
        var lastAnyModel = calls.LastOrDefault(call => !string.IsNullOrWhiteSpace(call.Model))?.Model;
        return new TaskTokenSummary
        {
            Calls = calls.Count,
            InputTokens = calls.Sum(call => call.InputTokens),
            OutputTokens = calls.Sum(call => call.OutputTokens),
            CacheReadTokens = calls.Sum(call => call.CacheReadTokens),
            CacheCreationTokens = calls.Sum(call => call.CacheCreationTokens),
            TotalTokens = calls.Sum(call => call.InputTokens + call.OutputTokens
                + call.CacheReadTokens + call.CacheCreationTokens),
            EstimatedApiCostUsd = calls.Sum(call => call.EstimatedApiCostUsd),
            AllModelsPriced = calls.Count > 0 && calls.All(call => call.ModelPriced),
            LastModel = lastAgentModel ?? lastAnyModel,
            LastUpdate = calls.LastOrDefault()?.Ts,
            Entries = calls,
        };
    }

    private static IEnumerable<TaskTokenCall> ExistingCalls(TaskTokenSummary? summary)
    {
        if (summary is null) yield break;
        foreach (var call in summary.Entries ?? [])
        {
            if (call.Ts != default) yield return call;
        }

        if ((summary.Entries?.Count ?? 0) > 0 || summary.TotalTokens <= 0) yield break;
        yield return new TaskTokenCall
        {
            Ts = summary.LastUpdate ?? DateTime.UnixEpoch,
            Model = summary.LastModel,
            ParticipantId = "agent:task-receipt",
            InputTokens = summary.InputTokens + Math.Max(
                0,
                summary.TotalTokens - summary.InputTokens - summary.OutputTokens
                    - summary.CacheReadTokens - summary.CacheCreationTokens),
            OutputTokens = summary.OutputTokens,
            CacheReadTokens = summary.CacheReadTokens,
            CacheCreationTokens = summary.CacheCreationTokens,
            EstimatedApiCostUsd = summary.EstimatedApiCostUsd,
            ModelPriced = summary.AllModelsPriced,
        };
    }

    private static DateTime NormalizeUtc(DateTime timestamp)
    {
        var utc = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
        };
        // cli-output.log persists HH:mm:ss.fff. Normalizing accepted wire lines
        // to the same precision makes completion-time log replay idempotent.
        return new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
    }

    private sealed record CallKey(
        long Ticks,
        string Model,
        string Participant,
        long Input,
        long Output,
        long CacheRead,
        long CacheCreation)
    {
        public static CallKey From(TaskTokenCall call, bool persistedLogReplay)
        {
            var timestamp = NormalizeUtc(call.Ts);
            // cli-output.log stores HH:mm:ss.fff without a date. Its parser
            // assigns the file's last-write date to every historical row, so a
            // later completion could otherwise duplicate a live receipt from a
            // previous day. Time-of-day identity is the conservative replay
            // key; accepted wire batches retain the full UTC timestamp.
            var ticks = persistedLogReplay ? timestamp.TimeOfDay.Ticks : timestamp.Ticks;
            return new CallKey(
                ticks,
                (call.Model ?? string.Empty).Trim().ToUpperInvariant(),
                (call.ParticipantId ?? string.Empty).Trim().ToUpperInvariant(),
                call.InputTokens,
                call.OutputTokens,
                call.CacheReadTokens,
                call.CacheCreationTokens);
        }
    }
}

public sealed record RemoteTaskTokenReceiptResult(
    int ParsedCalls,
    int AddedCalls,
    bool Written,
    string? Warning);
