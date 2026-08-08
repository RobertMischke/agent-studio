using System.Text.Json;
using AgentStudio.Persistence;

namespace AgentStudio.Git;

/// <summary>
/// Durable, card-scoped terminal decision for an accepted delivery that must
/// remain visible in the integration queue but no longer represents live
/// conflict work.
/// </summary>
public sealed record IntegrationQueueDisposition
{
    public int Version { get; init; } = 1;
    public string TaskKey { get; init; } = "";
    public string Status { get; init; } = "";
    public string Reason { get; init; } = "";
    public string? EvidenceCommit { get; init; }
    public DateTimeOffset ClassifiedAtUtc { get; init; }
    public string ClassifiedBy { get; init; } = "";
}

public sealed class IntegrationQueueDispositionStore
{
    public const string FileName = "integration-disposition.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IAtomicJsonFileWriter _writer;

    public IntegrationQueueDispositionStore(IAtomicJsonFileWriter? writer = null)
        => _writer = writer ?? new AtomicJsonFileWriter();

    public static string PathFor(string taskFolder)
        => Path.Combine(TaskPaths.LogsDir(taskFolder), FileName);

    public IntegrationQueueDisposition? Read(string taskFolder, string expectedTaskKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTaskKey);
        var path = PathFor(taskFolder);
        if (!File.Exists(path)) return null;

        try
        {
            var value = JsonSerializer.Deserialize<IntegrationQueueDisposition>(File.ReadAllText(path), Json);
            return value is not null
                   && value.Version == 1
                   && string.Equals(value.TaskKey, expectedTaskKey, StringComparison.OrdinalIgnoreCase)
                   && IntegrationQueueStates.IsTerminalDisposition(value.Status)
                   && !string.IsNullOrWhiteSpace(value.Reason)
                   && !string.IsNullOrWhiteSpace(value.ClassifiedBy)
                   && value.ClassifiedAtUtc != default
                ? value
                : null;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "IntegrationQueueDispositionStore: malformed or unreadable disposition");
            return null;
        }
    }

    public void Write(string taskFolder, IntegrationQueueDisposition disposition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskFolder);
        ArgumentNullException.ThrowIfNull(disposition);
        if (string.IsNullOrWhiteSpace(disposition.TaskKey))
            throw new ArgumentException("TaskKey is required.", nameof(disposition));
        if (!IntegrationQueueStates.IsTerminalDisposition(disposition.Status))
            throw new ArgumentException($"Unsupported terminal integration status '{disposition.Status}'.", nameof(disposition));
        if (string.IsNullOrWhiteSpace(disposition.Reason))
            throw new ArgumentException("Reason is required.", nameof(disposition));
        if (string.IsNullOrWhiteSpace(disposition.ClassifiedBy))
            throw new ArgumentException("ClassifiedBy is required.", nameof(disposition));
        if (disposition.ClassifiedAtUtc == default)
            throw new ArgumentException("ClassifiedAtUtc is required.", nameof(disposition));

        _writer.Write(PathFor(taskFolder), JsonSerializer.Serialize(disposition, Json));
    }
}
