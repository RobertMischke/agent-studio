using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks in the ASS-1727 paged archive read endpoint
/// (<c>GET /api/tasks/archive</c>). The Archive view was empty despite
/// hundreds of <c>7-archive</c> folders on disk because the cache-backed
/// board scan excludes the terminal lane; this endpoint serves that lane
/// directly from the slim-hydrated archive partition. The guarantees pinned
/// here:
/// <list type="bullet">
///   <item>the endpoint returns the archived tasks the board response omits;</item>
///   <item>paging (offset/limit) slices a stable, newest-first order while
///   <c>total</c> reports the full unpaged count;</item>
///   <item>the text filter narrows by title/key/id;</item>
///   <item>fixtures are hidden by default and opt-in via includeFixtures;</item>
///   <item>the default board response (<c>/grouped</c>) still excludes the
///   archive lane.</item>
/// </list>
/// </summary>
public class TaskArchiveEndpointTests : IDisposable
{
    private readonly string _watchPath;

    public TaskArchiveEndpointTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-archive-endpoint-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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

    private HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    private void SeedArchived(string slug, string title, DateTime enteredLaneAt, bool fixture = false)
    {
        var dir = Path.Combine(_watchPath, TaskStates.Archive, slug);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new
        {
            id = slug,
            title,
            state = TaskStates.Archive,
            order = 1,
            agent = "claude",
            enteredLaneAt = enteredLaneAt.ToString("o"),
            fixture
        });
        File.WriteAllText(Path.Combine(dir, "task.json"), json);
    }

    private void SeedBoard(string slug, string title, string state)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new { id = slug, title, state, order = 1, agent = "claude" });
        File.WriteAllText(Path.Combine(dir, "task.json"), json);
    }

    private void SeedThreeArchivedNewestLast()
    {
        SeedArchived("arch-old", "Oldest archived task", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedArchived("arch-mid", "Middle archived task", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedArchived("arch-new", "Newest archived task", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Archive_ReturnsArchivedTasks_NewestFirst()
    {
        SeedThreeArchivedNewestLast();
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        var body = await client.GetFromJsonAsync<ArchivedTasksResponse>(
            $"/api/tasks/archive?watchPath={Uri.EscapeDataString(_watchPath)}");

        Assert.NotNull(body);
        Assert.Equal(3, body!.Total);
        Assert.Equal(3, body.Items.Count);
        // EnteredLaneAt descending: newest archived first.
        Assert.Equal(new[] { "arch-new", "arch-mid", "arch-old" }, body.Items.Select(i => i.Id).ToArray());
        Assert.All(body.Items, i => Assert.Equal(TaskStates.Archive, i.State));
    }

    [Fact]
    public async Task Archive_Paging_SlicesStableOrder_TotalStaysFull()
    {
        SeedThreeArchivedNewestLast();
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        var baseUrl = $"/api/tasks/archive?watchPath={Uri.EscapeDataString(_watchPath)}";

        var page1 = await client.GetFromJsonAsync<ArchivedTasksResponse>($"{baseUrl}&offset=0&limit=2");
        Assert.NotNull(page1);
        Assert.Equal(3, page1!.Total);
        Assert.Equal(0, page1.Offset);
        Assert.Equal(2, page1.Limit);
        Assert.Equal(new[] { "arch-new", "arch-mid" }, page1.Items.Select(i => i.Id).ToArray());

        var page2 = await client.GetFromJsonAsync<ArchivedTasksResponse>($"{baseUrl}&offset=2&limit=2");
        Assert.NotNull(page2);
        Assert.Equal(3, page2!.Total); // total is the unpaged count
        Assert.Single(page2.Items);
        Assert.Equal("arch-old", page2.Items[0].Id);
    }

    [Fact]
    public async Task Archive_Search_FiltersByTitle()
    {
        SeedThreeArchivedNewestLast();
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        var body = await client.GetFromJsonAsync<ArchivedTasksResponse>(
            $"/api/tasks/archive?watchPath={Uri.EscapeDataString(_watchPath)}&search=Middle");

        Assert.NotNull(body);
        Assert.Equal(1, body!.Total);
        Assert.Single(body.Items);
        Assert.Equal("arch-mid", body.Items[0].Id);
    }

    [Fact]
    public async Task Archive_Search_NoMatch_ReturnsEmptyItems_WithZeroTotal()
    {
        // The Archive view shows its "truly empty" state only on a genuine
        // zero total, so a filter that matches nothing must report total=0
        // (not the unfiltered archive size) and an empty page.
        SeedThreeArchivedNewestLast();
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        var body = await client.GetFromJsonAsync<ArchivedTasksResponse>(
            $"/api/tasks/archive?watchPath={Uri.EscapeDataString(_watchPath)}&search=zzz-no-such-card");

        Assert.NotNull(body);
        Assert.Equal(0, body!.Total);
        Assert.Empty(body.Items);
    }

    [Fact]
    public async Task Archive_Limit_IsClampedToBounds()
    {
        SeedThreeArchivedNewestLast();
        using var factory = BuildFactory();
        using var client = CreateClient(factory);
        var baseUrl = $"/api/tasks/archive?watchPath={Uri.EscapeDataString(_watchPath)}";

        // Above the 200 ceiling clamps to 200 (still returns all three rows).
        var high = await client.GetFromJsonAsync<ArchivedTasksResponse>($"{baseUrl}&limit=5000");
        Assert.NotNull(high);
        Assert.Equal(200, high!.Limit);
        Assert.Equal(3, high.Items.Count);

        // Below the floor clamps to 1 (a single newest-first row).
        var low = await client.GetFromJsonAsync<ArchivedTasksResponse>($"{baseUrl}&limit=0");
        Assert.NotNull(low);
        Assert.Equal(1, low!.Limit);
        Assert.Single(low.Items);
        Assert.Equal("arch-new", low.Items[0].Id);
        Assert.Equal(3, low.Total); // total stays the full unpaged count
    }

    [Fact]
    public async Task Archive_OffsetBeyondTotal_ReturnsEmptyItems_KeepsFullTotal()
    {
        SeedThreeArchivedNewestLast();
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        var body = await client.GetFromJsonAsync<ArchivedTasksResponse>(
            $"/api/tasks/archive?watchPath={Uri.EscapeDataString(_watchPath)}&offset=99&limit=50");

        Assert.NotNull(body);
        Assert.Equal(3, body!.Total);
        Assert.Empty(body.Items);
    }

    [Fact]
    public async Task Archive_HidesFixtures_ByDefault_OptInWithIncludeFixtures()
    {
        SeedThreeArchivedNewestLast();
        SeedArchived("arch-fixture", "Fixture archived task",
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), fixture: true);

        using var factory = BuildFactory();
        using var client = CreateClient(factory);
        var baseUrl = $"/api/tasks/archive?watchPath={Uri.EscapeDataString(_watchPath)}";

        var defaultBody = await client.GetFromJsonAsync<ArchivedTasksResponse>(baseUrl);
        Assert.NotNull(defaultBody);
        Assert.Equal(3, defaultBody!.Total);
        Assert.DoesNotContain(defaultBody.Items, i => i.Id == "arch-fixture");

        var withFixtures = await client.GetFromJsonAsync<ArchivedTasksResponse>($"{baseUrl}&includeFixtures=true");
        Assert.NotNull(withFixtures);
        Assert.Equal(4, withFixtures!.Total);
        Assert.Contains(withFixtures.Items, i => i.Id == "arch-fixture");
    }

    [Fact]
    public async Task GroupedBoard_StillExcludesArchive_EvenWithArchivedFoldersOnDisk()
    {
        SeedThreeArchivedNewestLast();
        SeedBoard("ready-1", "A ready board task", TaskStates.Ready);

        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        using var resp = await client.GetAsync("/api/tasks/grouped");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // The archive bucket is intentionally empty in the board response.
        Assert.Equal(JsonValueKind.Array, root.GetProperty("archive").ValueKind);
        Assert.Equal(0, root.GetProperty("archive").GetArrayLength());

        // ...while the board lane still surfaces the live card we seeded.
        var readyIds = root.GetProperty("ready").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .ToList();
        Assert.Contains("ready-1", readyIds);
    }
}
