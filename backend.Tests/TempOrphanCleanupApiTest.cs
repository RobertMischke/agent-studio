using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

public class TaskOrphanFolderDeleteEndpointTests : IDisposable
{
    private readonly string _watchPath;

    public TaskOrphanFolderDeleteEndpointTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-orphan-delete-api-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task DeleteOrphanFolder_RemovesArchiveFolderThroughApi()
    {
        var slug = "archive-orphan";
        var folder = Path.Combine(_watchPath, TaskStates.Archive, slug);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "results"));
        File.WriteAllText(Path.Combine(folder, "results", "evidence.txt"), "leftover");

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["WatchPaths:0:Name"] = "Agent Task Processor",
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath
                    });
                });
            });

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/tasks/orphan-folder")
        {
            Content = JsonContent.Create(new OrphanFolderDeleteRequest
            {
                WatchPath = _watchPath,
                Lane = TaskStates.Archive,
                Folder = slug
            })
        };
        request.Headers.Add("X-Client-Id", "local-default");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.False(Directory.Exists(folder));
    }
}
