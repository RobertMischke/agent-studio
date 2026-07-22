using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

public sealed class EpicGoalDecompositionEndpointsTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectRoot;
    private const string Project = "goal-project";

    public EpicGoalDecompositionEndpointsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "goal-decomposition-api-" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_root, "projects", Project);
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task PostSubTasks_CreatesDependencyGraphWithOperatorProvenance()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var epicId = await CreateEpic(client);

        using var response = await client.PostAsJsonAsync(
            $"/api/epics/{epicId}/sub-tasks?watchPath={Uri.EscapeDataString(_projectRoot)}",
            new
            {
                subTasks = new object[]
                {
                    new { id = "delivery", title = "Deliver goal", promptMarkdown = "deliver", purpose = "delivery", dependsOn = Array.Empty<string>() },
                    new { id = "verify", title = "Verify goal", promptMarkdown = "inspect real evidence", purpose = "verification", dependsOn = new[] { "delivery" } },
                }
            });

        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var created = result.RootElement.GetProperty("created").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Equal(2, created.Length);

        using var deliveryResponse = await client.GetAsync(
            $"/api/tasks/{created[0]}?watchPath={Uri.EscapeDataString(_projectRoot)}");
        using var verificationResponse = await client.GetAsync(
            $"/api/tasks/{created[1]}?watchPath={Uri.EscapeDataString(_projectRoot)}");
        deliveryResponse.EnsureSuccessStatusCode();
        verificationResponse.EnsureSuccessStatusCode();
        using var delivery = JsonDocument.Parse(await deliveryResponse.Content.ReadAsStringAsync());
        using var verification = JsonDocument.Parse(await verificationResponse.Content.ReadAsStringAsync());

        var deliveryKey = delivery.RootElement.GetProperty("info").GetProperty("key").GetString();
        var verificationInfo = verification.RootElement.GetProperty("info");
        Assert.Equal(
            deliveryKey,
            verificationInfo.GetProperty("references").GetProperty("dependsOn")[0].GetString());
        Assert.Equal(
            TaskCreationInitiators.Operator,
            verificationInfo.GetProperty("creationProvenance").GetProperty("initiator").GetString());
        Assert.Equal(
            GoalTaskPurposes.Verification,
            verificationInfo.GetProperty("creationProvenance").GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task PostSubTasks_RejectsCycleWithoutCreatingChildren()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var epicId = await CreateEpic(client);

        using var response = await client.PostAsJsonAsync(
            $"/api/epics/{epicId}/sub-tasks?watchPath={Uri.EscapeDataString(_projectRoot)}",
            new
            {
                subTasks = new object[]
                {
                    new { id = "a", title = "A", dependsOn = new[] { "b" } },
                    new { id = "b", title = "B", dependsOn = new[] { "a" } },
                }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("cycle", body.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        using var rollupResponse = await client.GetAsync(
            $"/api/epics/{epicId}?watchPath={Uri.EscapeDataString(_projectRoot)}");
        rollupResponse.EnsureSuccessStatusCode();
        using var rollup = JsonDocument.Parse(await rollupResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, rollup.RootElement.GetProperty("subTaskTotal").GetInt32());
    }

    private async Task<string> CreateEpic(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/tasks", new
        {
            id = "goal-epic",
            title = "Goal Epic",
            watchPath = _projectRoot,
            kind = "epic",
            targetState = TaskStates.Backlog,
            promptMarkdown = "Deliver and verify the goal."
        });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["Security:Profile"] = "local",
                    ["WatchPaths:0:Name"] = Project,
                    ["WatchPaths:0:Path"] = _projectRoot,
                    ["WatchPaths:0:RootPath"] = _projectRoot,
                    ["WatchPaths:0:RepositoryPath"] = _projectRoot,
                });
            });
        });
}
