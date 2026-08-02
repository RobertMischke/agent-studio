using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskListProjectFilterEndpointTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "task-list-project-filter-" + Guid.NewGuid().ToString("N"));
    private readonly string _alphaPath;
    private readonly string _betaPath;

    public TaskListProjectFilterEndpointTests()
    {
        _alphaPath = Path.Combine(_workspace, "alpha");
        _betaPath = Path.Combine(_workspace, "beta");
        SeedTask(_alphaPath, "alpha-task", "Alpha task");
        SeedTask(_betaPath, "beta-task", "Beta task");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task List_ByProjectId_ReturnsFilteredListInsteadOfAnalysisEnvelope()
    {
        await using var factory = BuildFactory();
        using var client = CreateClient(factory);
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var alpha = registry.FindByStorageLocation(_alphaPath);
        Assert.NotNull(alpha);

        using var response = await client.GetAsync(
            $"/api/tasks?project={Uri.EscapeDataString(alpha!.Id)}");

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        var task = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal("alpha-task", task.GetProperty("id").GetString());
    }

    [Fact]
    public async Task List_ProjectScopeComposesWithAnalysisFilters()
    {
        await using var factory = BuildFactory();
        using var client = CreateClient(factory);
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var alpha = registry.FindByStorageLocation(_alphaPath);
        Assert.NotNull(alpha);

        using var response = await client.GetAsync(
            $"/api/tasks?project={Uri.EscapeDataString(alpha!.Id)}&state={TaskStates.Ready}");

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
        var task = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("alpha-task", task.GetProperty("id").GetString());
    }

    [Fact]
    public async Task List_UnknownProject_ReturnsNotFoundInsteadOfWorkspaceTasks()
    {
        await using var factory = BuildFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/tasks?project=PROJ-999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = "Alpha Project",
                    ["WatchPaths:0:Path"] = _alphaPath,
                    ["WatchPaths:0:RootPath"] = _alphaPath,
                    ["WatchPaths:1:Name"] = "Beta Project",
                    ["WatchPaths:1:Path"] = _betaPath,
                    ["WatchPaths:1:RootPath"] = _betaPath,
                }));
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    private static void SeedTask(string watchPath, string id, string title)
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(watchPath, state));

        var taskFolder = Path.Combine(watchPath, TaskStates.Ready, id);
        Directory.CreateDirectory(taskFolder);
        File.WriteAllText(Path.Combine(taskFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            title,
            state = TaskStates.Ready,
            order = 1,
            agent = "codex",
        }));
    }
}
