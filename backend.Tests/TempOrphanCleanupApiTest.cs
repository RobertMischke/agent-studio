using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OrchestratorApi.Models;
using Xunit;

namespace OrchestratorApi.Tests;

public class TempOrphanCleanupApiTest
{
    [Fact]
    public async Task DeleteNamedArchiveOrphanThroughApi()
    {
        var watchPath = @"C:\Projects\agent-taskboard-workspace\projects\agent-taskboard";
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["WatchPaths:0:Name"] = "Agent Task Processor",
                        ["WatchPaths:0:Path"] = watchPath,
                        ["WatchPaths:0:RootPath"] = @"C:\Projects\agent-taskboard-devspace\agent-taskboard-dev"
                    });
                });
            });

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/tasks/orphan-folder")
        {
            Content = JsonContent.Create(new OrphanFolderDeleteRequest
            {
                WatchPath = watchPath,
                Lane = TaskStates.Archive,
                Folder = "kanban-lane-grouping-collapse-empty-2026-05-05"
            })
        };
        request.Headers.Add("X-Client-Id", "local-default");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}
