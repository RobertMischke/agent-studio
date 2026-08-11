using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteTaskTokenReceiptServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "remote-token-receipt-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Record_CodexCompletion_PersistsCategorizedIdempotentReceipt()
    {
        var folder = Path.Combine(_root, "tasks", "000", "TE-41");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), """
            {"id":"TE-41","key":"TE-41","state":"7-archive","cliType":"codex","model":"gpt-5.6-sol"}
            """);

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "Token Economy",
                ["WatchPaths:0:Path"] = _root,
                ["WatchPaths:0:RootPath"] = _root,
                ["WatchPaths:0:RepositoryPath"] = _root,
            }).Build();
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            config);
        var scanner = new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            summary);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var service = new RemoteTaskTokenReceiptService(
            new CliUsageParserRegistry([new CodexUsageParser()]),
            new CliModelRegistry(),
            mutations,
            NullLogger<RemoteTaskTokenReceiptService>.Instance);
        var task = new TaskInfo
        {
            Id = "TE-41",
            Key = "TE-41",
            TaskKey = $"{_root}::TE-41",
            ProjectName = "Token Economy",
            WatchPath = _root,
            FolderPath = folder,
            CliType = CliTypes.Codex,
            Model = "gpt-5.6-sol",
        };
        var at = new DateTime(2026, 8, 11, 12, 24, 33, DateTimeKind.Utc);
        var usage = new CliOutputLine
        {
            Timestamp = at,
            Stream = "stdout",
            Text = "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":14556790,\"cached_input_tokens\":14283776,\"output_tokens\":28490,\"reasoning_output_tokens\":9726}}",
        };

        var first = service.Record(task, [usage]);
        var replay = service.Record(task, [usage]);
        var logPath = Path.Combine(folder, "logs", "cli-output.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(
            logPath,
            $"[{at:HH:mm:ss.fff}] [stdout] {usage.Text}");
        File.SetLastWriteTimeUtc(logPath, at.AddDays(1));
        var nextDayLogReplay = service.RecordFromTaskLog(task);

        Assert.True(first.Written);
        Assert.Equal(1, first.AddedCalls);
        Assert.False(replay.Written);
        Assert.Equal(0, replay.AddedCalls);
        Assert.False(nextDayLogReplay.Written);
        Assert.Equal(0, nextDayLogReplay.AddedCalls);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "task.json")));
        var receipt = document.RootElement.GetProperty("tokenSummary");
        var persisted = receipt.Deserialize<TaskTokenSummary>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(persisted);
        Assert.Equal(1, persisted!.Calls);
        Assert.Equal(14_556_790, persisted.InputTokens);
        Assert.Equal(28_490, persisted.OutputTokens);
        Assert.Equal(14_283_776, persisted.CacheReadTokens);
        Assert.Equal(0, persisted.CacheCreationTokens);
        Assert.Equal(28_869_056, persisted.TotalTokens);
        Assert.True(persisted.AllModelsPriced);
        Assert.True(persisted.EstimatedApiCostUsd > 0);
        Assert.Equal("agent:codex", Assert.Single(persisted.Entries).ParticipantId);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
