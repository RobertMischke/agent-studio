using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OrchestratorApi.Models;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class TaskFilesEndpointCharsetTests : IDisposable
{
    private readonly string _watchPath;

    public TaskFilesEndpointCharsetTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-charset-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ReadJobFile_ReturnsMarkdownAsUtf8WithCharset()
    {
        var dir = WriteJobRoot("umlaut-prompt");
        const string prompt = "Lücken / gehört / für / „Anführung\"";
        File.WriteAllText(Path.Combine(dir, "prompt.md"), prompt, Encoding.UTF8);

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["WatchPaths:0:Name"] = "charset",
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                    });
                });
            });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/api/tasks/umlaut-prompt/files/prompt.md?watchPath={Uri.EscapeDataString(_watchPath)}");

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet?.ToLowerInvariant());
        Assert.Equal(prompt, await response.Content.ReadAsStringAsync());
    }

    private string WriteJobRoot(string slug)
    {
        var dir = Path.Combine(_watchPath, TaskStates.Backlog, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{TaskStates.Backlog}\",\"order\":1,\"agent\":\"copilot\"}}",
            Encoding.UTF8);
        return dir;
    }
}
