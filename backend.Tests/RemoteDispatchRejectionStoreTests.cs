using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteDispatchRejectionStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "remote-dispatch-rejection-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Record_PersistsDiagnosticAndKeepsRepeatedPollIdempotent()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "task.json"), "{}");
        var store = new RemoteDispatchRejectionStore(
            NullLogger<RemoteDispatchRejectionStore>.Instance);
        var task = new TaskInfo
        {
            Id = "task-1",
            Key = "AGT-1",
            ProjectName = "demo",
            FolderPath = _folder,
            EnteredLaneAt = new DateTime(2026, 8, 8, 9, 0, 0, DateTimeKind.Utc),
        };

        var first = store.Record(
            task,
            "runner-01",
            "Runner 01",
            "repository-url-missing",
            "project has no repositoryUrl",
            new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc));
        var repeated = store.Record(
            task,
            "runner-01",
            "Runner 01",
            "repository-url-missing",
            "project has no repositoryUrl",
            new DateTime(2026, 8, 8, 11, 0, 0, DateTimeKind.Utc));

        Assert.Equal(first.RejectedAtUtc, repeated.RejectedAtUtc);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "task.json")));
        var persisted = json.RootElement.GetProperty(RemoteDispatchRejectionStore.FieldName);
        Assert.Equal("runner-01", persisted.GetProperty("runnerId").GetString());
        Assert.Equal("project has no repositoryUrl", persisted.GetProperty("reason").GetString());
    }

    [Fact]
    public void Record_RefreshesTheSameReasonForANewReadyLaneGeneration()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "task.json"), "{}");
        var store = new RemoteDispatchRejectionStore(
            NullLogger<RemoteDispatchRejectionStore>.Instance);
        var firstReadyAt = new DateTime(2026, 8, 8, 9, 0, 0, DateTimeKind.Utc);
        var firstTask = new TaskInfo
        {
            Id = "task-1",
            Key = "AGT-1",
            ProjectName = "demo",
            FolderPath = _folder,
            EnteredLaneAt = firstReadyAt,
        };
        store.Record(
            firstTask,
            "runner-01",
            "Runner 01",
            "repository-url-missing",
            "project has no repositoryUrl",
            firstReadyAt.AddMinutes(1));

        var secondReadyAt = firstReadyAt.AddHours(2);
        TaskJsonFile.UpdateFieldOrThrow(_folder, "enteredLaneAt", secondReadyAt);
        var refreshed = store.Record(
            firstTask with { EnteredLaneAt = secondReadyAt },
            "runner-01",
            "Runner 01",
            "repository-url-missing",
            "project has no repositoryUrl",
            secondReadyAt.AddMinutes(1));

        Assert.Equal(secondReadyAt.AddMinutes(1), refreshed.RejectedAtUtc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
