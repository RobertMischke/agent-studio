using AgentStudio.Persistence;
using AgentStudio.Projection;

namespace AgentStudio.Diagnostics;

/// <summary>
/// Durable Agent Studio sink for typed lifecycle and diagnostic events received
/// from a standalone runner. The endpoint authenticates and fences the write;
/// this service owns the bounded task-folder persistence used by replay reads.
/// </summary>
public sealed class RunnerEventJournal
{
    private readonly IJsonlAppender _appender;

    public RunnerEventJournal(IJsonlAppender appender) => _appender = appender;

    public Task AppendAsync(TaskInfo task, RunnerRecordedEvent recorded, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(recorded);
        if (string.IsNullOrWhiteSpace(task.FolderPath))
            throw new InvalidOperationException("The task has no folder for runner event persistence.");
        if (RunnerEventSource.NormalizeKind(recorded.Kind) is null)
            throw new ArgumentException("The runner event kind is not supported.", nameof(recorded));

        var path = Path.Combine(task.FolderPath, RunnerEventSource.RelativePath);
        return _appender.AppendAsync(path, recorded, options: null, ct);
    }
}
