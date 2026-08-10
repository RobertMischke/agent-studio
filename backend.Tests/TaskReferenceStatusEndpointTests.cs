using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskReferenceStatusEndpointTests : IDisposable
{
    private readonly string _watchPath = Path.Combine(Path.GetTempPath(), "task-ref-status-" + Guid.NewGuid().ToString("N"));

    public TaskReferenceStatusEndpointTests()
    {
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
        // Use a terminal lane (6-completed) rather than 3-progress: booting the full
        // host activates the runner's stale-progress recovery, which correctly demotes
        // an in-progress job that has no live process/heartbeat. A completed, graded
        // task is both stable across boot and a representative reference microcard.
        var dir = Path.Combine(_watchPath, TaskStates.Completed, "living-reference");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), JsonSerializer.Serialize(new
        {
            id = "living-reference",
            key = "ATP-42",
            title = "Living reference",
            state = TaskStates.Completed,
            noBranchExpected = true,
            order = 1,
            agent = "codex",
            tags = new[] { "code-review:grade-a" }
        }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { }
    }

    [Fact]
    public async Task Batch_ReturnsLiveAndGhostKeys_AndDropsUnknownShortCodes()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // TaskRepository makes the ProjectRegistry persistent so the boot-time
                // RegistryBootstrap discovery pass runs and derives the "ATP" short code
                // from the "Agent Task Processor" watch path. Without it the registry is
                // in-memory-only, bootstrap is skipped, no short codes are known, and the
                // endpoint (correctly) drops every requested key.
                ["TaskRepository"] = _watchPath,
                ["WatchPaths:0:Name"] = "Agent Task Processor",
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            }));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        var response = await client.PostAsJsonAsync("/api/tasks/reference-status", new
        {
            keys = new[] { "ATP-42", "ATP-999", "NOPE-1", "ATP-42" }
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TaskReferenceStatusResponse>();

        Assert.NotNull(body);
        Assert.Equal(2, body!.Items.Count);
        var live = Assert.Single(body.Items, item => item.Key == "ATP-42");
        Assert.True(live.Exists);
        Assert.Equal(TaskStates.Completed, live.Lane);
        Assert.Equal("A", live.ReviewGrade);
        var ghost = Assert.Single(body.Items, item => item.Key == "ATP-999");
        Assert.False(ghost.Exists);
        Assert.Null(ghost.TaskKey);
    }
}
