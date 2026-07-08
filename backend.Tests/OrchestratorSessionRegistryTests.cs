using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestratorSessionRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly IConfiguration _config;

    public OrchestratorSessionRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-orch-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData("global", "global", "global", null, null)]
    [InlineData("project:PROJ-001", "project~3APROJ-001", "project", "PROJ-001", null)]
    [InlineData("task:PROJ-001/AGT-1930", "task~3APROJ-001~2FAGT-1930", "task", "PROJ-001", "AGT-1930")]
    public void GetOrCreate_PersistsSessionJsonAndHistory(string raw, string encoded, string kind, string? projectId, string? taskKey)
    {
        var registry = Build();

        var record = registry.GetOrCreate(raw);

        Assert.Equal(raw, record.ContextKey);
        Assert.Equal(encoded, record.EncodedKey);
        Assert.Equal(kind, record.Kind);
        Assert.Equal(projectId, record.ProjectId);
        Assert.Equal(taskKey, record.TaskKey);
        Assert.Null(record.SessionId);
        Assert.True(File.Exists(Path.Combine(_root, ".metadata", "orchestrator-sessions", encoded, "session.json")));
        Assert.True(File.Exists(Path.Combine(_root, ".metadata", "orchestrator-sessions", encoded, "history.jsonl")));
    }

    [Fact]
    public void GetOrCreate_IsLazyAndIdempotent()
    {
        var registry = Build();
        var first = registry.GetOrCreate("project:PROJ-123");
        var second = registry.GetOrCreate("project:PROJ-123");

        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Single(registry.List(), s => s.ContextKey == "project:PROJ-123");
    }

    [Fact]
    public void List_EnsuresGlobalSessionAndMigratesLegacyGlobalStore()
    {
        var legacy = new GlobalOrchestratorSessionStore(_config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        legacy.Write(new GlobalOrchestratorSession(
            SessionId: "claude-global-1",
            Model: "claude-test",
            BootedAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            BootPromptPreview: "boot",
            BootReplyPreview: "reply",
            CumulativeInputTokens: 11,
            CumulativeOutputTokens: 22,
            CumulativeCacheReadTokens: 33,
            CumulativeCacheCreationTokens: 44,
            Calls: 2,
            LastUsedAt: new DateTime(2026, 1, 3, 3, 4, 5, DateTimeKind.Utc),
            LastError: null));

        var registry = Build();
        var global = Assert.Single(registry.List(), s => s.ContextKey == "global");

        Assert.Equal("claude-global-1", global.SessionId);
        Assert.Equal("claude-test", global.Model);
        Assert.Equal(11, global.CumulativeInputTokens);
        Assert.True(File.Exists(Path.Combine(_root, ".metadata", "orchestrator-sessions", "global", "session.json")));
    }

    [Fact]
    public async Task Endpoints_ListAndGetOrCreateByContextKey()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var created = await client.GetAsync("/api/orchestrator/sessions/task:PROJ-001/AGT-1930");
        created.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
        {
            Assert.Equal("task:PROJ-001/AGT-1930", doc.RootElement.GetProperty("contextKey").GetString());
            Assert.Equal("task~3APROJ-001~2FAGT-1930", doc.RootElement.GetProperty("encodedKey").GetString());
        }

        using var list = await client.GetAsync("/api/orchestrator/sessions");
        list.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            var sessions = doc.RootElement.GetProperty("sessions").EnumerateArray().ToList();
            Assert.Contains(sessions, s => s.GetProperty("contextKey").GetString() == "global");
            Assert.Contains(sessions, s => s.GetProperty("contextKey").GetString() == "task:PROJ-001/AGT-1930");
        }

        using var bad = await client.GetAsync("/api/orchestrator/sessions/task:PROJ-001/bad/path");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private OrchestratorSessionRegistry Build()
    {
        var legacy = new GlobalOrchestratorSessionStore(_config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        return new OrchestratorSessionRegistry(_config, legacy, NullLogger<OrchestratorSessionRegistry>.Instance);
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        var projectRoot = Path.Combine(_root, "projects", "agent-taskboard");
        var codeRoot = Path.Combine(_root, "code");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(codeRoot);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _root,
                        ["WatchPaths:0:Name"] = "agent-taskboard",
                        ["WatchPaths:0:Path"] = projectRoot,
                        ["WatchPaths:0:RootPath"] = codeRoot,
                        ["WatchPaths:0:RepositoryPath"] = codeRoot,
                    });
                });
            });
    }
}
