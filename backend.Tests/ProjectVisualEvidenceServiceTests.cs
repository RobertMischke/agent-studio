using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectVisualEvidenceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "project-visual-evidence-" + Guid.NewGuid().ToString("N"));

    public ProjectVisualEvidenceServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void BuildAndAcknowledge_ReuseDurableReceipt_AndRetainMissingEvidence()
    {
        var (service, jobFolder, screenshot) = BuildStack();

        var initial = Assert.IsType<ProjectVisualEvidenceQueue>(service.Build("Demo"));
        var unseen = Assert.Single(initial.Items);
        Assert.Equal(1, initial.UnseenCount);
        Assert.Equal("unseen", unseen.ReviewStatus);
        Assert.Equal("delivered-task", unseen.JobId);
        Assert.Equal("results/overview--light--real.png", unseen.RelativePath);

        Parallel.For(0, 8, _ =>
        {
            var acknowledged = service.Acknowledge("Demo", unseen.Id);
            Assert.NotNull(acknowledged);
            Assert.Equal("reviewed", acknowledged!.ReviewStatus);
        });

        var reviewed = Assert.IsType<ProjectVisualEvidenceQueue>(service.Build("Demo"));
        Assert.Equal(0, reviewed.UnseenCount);
        Assert.Equal("reviewed", Assert.Single(reviewed.Items).ReviewStatus);
        Assert.Equal(unseen.Id, Assert.Single(ReviewEvidenceLog.ReadLatestPerId(jobFolder)).Id);

        File.Delete(screenshot);
        var unavailable = Assert.IsType<ProjectVisualEvidenceQueue>(service.Build("Demo"));
        var retained = Assert.Single(unavailable.Items);
        Assert.Equal(unseen.Id, retained.Id);
        Assert.Equal("unavailable", retained.ReviewStatus);
        Assert.Null(retained.Url);
    }

    [Fact]
    public void Build_IsProjectScoped_AndIgnoresUndeliveredCards()
    {
        var (service, _, _) = BuildStack();
        var otherFolder = Path.Combine(_root, "other", TaskStates.Completed, "other-task");
        Directory.CreateDirectory(Path.Combine(otherFolder, "results"));
        WriteTask(otherFolder, "OTHER-1", TaskStates.Completed);
        File.WriteAllBytes(Path.Combine(otherFolder, "results", "other.png"), [1, 2, 3]);

        var readyFolder = Path.Combine(_root, "demo", TaskStates.Ready, "ready-task");
        Directory.CreateDirectory(Path.Combine(readyFolder, "results"));
        WriteTask(readyFolder, "DEMO-READY", TaskStates.Ready);
        File.WriteAllBytes(Path.Combine(readyFolder, "results", "not-delivered.png"), [1, 2, 3]);

        var queue = Assert.IsType<ProjectVisualEvidenceQueue>(service.Build("Demo"));

        Assert.Single(queue.Items);
        Assert.Equal("delivered-task", queue.Items[0].JobId);
        Assert.Null(service.Build("Unknown"));
    }

    private (ProjectVisualEvidenceService Service, string JobFolder, string Screenshot) BuildStack()
    {
        var demo = Path.Combine(_root, "demo");
        var other = Path.Combine(_root, "other");
        var jobFolder = Path.Combine(demo, TaskStates.Completed, "delivered-task");
        var results = Path.Combine(jobFolder, "results");
        Directory.CreateDirectory(results);
        Directory.CreateDirectory(other);
        WriteTask(jobFolder, "DEMO-1", TaskStates.Completed);
        var screenshot = Path.Combine(results, "overview--light--real.png");
        File.WriteAllBytes(screenshot, [1, 2, 3]);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:Path"] = demo,
            ["WatchPaths:1:Name"] = "Other",
            ["WatchPaths:1:Path"] = other,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var screenshots = new ScreenshotIndexService(scanner, NullLogger<ScreenshotIndexService>.Instance);
        var service = new ProjectVisualEvidenceService(
            scanner, screenshots, NullLogger<ProjectVisualEvidenceService>.Instance);
        return (service, jobFolder, screenshot);
    }

    private static void WriteTask(string folder, string id, string state)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            taskKey = id,
            key = id,
            title = id,
            state,
            order = 1,
            agent = "codex",
            createdAt = "2026-07-11T10:00:00Z",
        }));
    }
}
