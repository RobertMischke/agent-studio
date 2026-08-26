using System.Text.Json;
using AgentStudio.Persistence;

namespace AgentStudio.Cli;

public sealed class LocalCliRepairJournal
{
    private readonly IConfiguration _configuration;
    private readonly IJsonlAppender _appender;
    private readonly ILogger<LocalCliRepairJournal> _logger;

    public LocalCliRepairJournal(
        IConfiguration configuration,
        IJsonlAppender appender,
        ILogger<LocalCliRepairJournal> logger)
    {
        _configuration = configuration;
        _appender = appender;
        _logger = logger;
    }

    public async Task AppendAsync(LocalCliRepairJournalRecord record, CancellationToken ct)
    {
        var path = ResolvePath();
        if (path is null)
        {
            throw new InvalidOperationException(
                "TaskRepository is unset; refusing an unjournalled CLI repair attempt");
        }
        await _appender.AppendAsync(path, record, ct: ct);
    }

    public IReadOnlyList<LocalCliRepairJournalRecord> Read()
    {
        var path = ResolvePath();
        if (path is null || !File.Exists(path)) return [];
        var records = new List<LocalCliRepairJournalRecord>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<LocalCliRepairJournalRecord>(line,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (record is not null) records.Add(record);
                }
                catch (JsonException ex)
                {
                    SilentCatch.Note(ex, "LocalCliRepairJournal: skip malformed historical row");
                    // A malformed historical row does not erase later receipts.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read CLI repair journal {Path}", path);
        }
        return records;
    }

    public string? ResolvePath()
    {
        var root = _configuration["TaskRepository"];
        return string.IsNullOrWhiteSpace(root)
            ? null
            : Path.Combine(root, "logs", "cli-repairs.jsonl");
    }
}
