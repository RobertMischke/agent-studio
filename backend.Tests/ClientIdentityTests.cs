using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the registration boundary contract: round-trip register/get/list/soft-delete,
/// the bootstrap default identity is created on first load, and legacy task.json
/// without ownerClientId is migrated to <see cref="DefaultClientIdentity.Id"/>
/// on first scan.
/// </summary>
public class ClientIdentityTests : IDisposable
{
    private readonly string _root;
    private readonly string _watchPath;

    public ClientIdentityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agent-taskboard-clients-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "watch");
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private IConfiguration BuildConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["Environment:DefaultIdentityName"] = "Test Default",
                ["Environment:DefaultIdentityEmoji"] = "🦊",
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
    }

    private ClientIdentityStore BuildStore(IConfiguration config)
        => new(config, NullLogger<ClientIdentityStore>.Instance);

    private TaskScannerService BuildScanner(IConfiguration config)
    {
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    [Fact]
    public void EnsureLoaded_CreatesBootstrapDefault()
    {
        var store = BuildStore(BuildConfig());
        store.EnsureLoaded();

        var defaults = store.Find(DefaultClientIdentity.Id);
        Assert.NotNull(defaults);
        Assert.Equal("Test Default", defaults!.DisplayName);
        Assert.Equal("🦊", defaults.Emoji);
        Assert.True(File.Exists(Path.Combine(_root, "identities", DefaultClientIdentity.Id + ".json")));
    }

    [Fact]
    public void Register_AssignsSlug_AndIsIdempotentOnDisplayName()
    {
        var store = BuildStore(BuildConfig());

        var first = store.Register(new RegisterClientRequest
        {
            DisplayName = "Robert's Workshop",
            Emoji = "🛠️",
            Kind = ClientIdentityKinds.Human
        });
        Assert.Equal("robert-s-workshop", first.Id);
        Assert.Equal(ClientIdentityKind.Human, first.Kind);

        var second = store.Register(new RegisterClientRequest
        {
            DisplayName = "Robert's Workshop",
            Emoji = "🦊"
        });
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("🦊", second.Emoji); // refreshed
    }

    [Fact]
    public void Register_DuplicateSlug_AppendsCounter()
    {
        var store = BuildStore(BuildConfig());
        var a = store.Register(new RegisterClientRequest { DisplayName = "Alpha" });
        var b = store.Register(new RegisterClientRequest { DisplayName = "alpha!" }); // sanitises to alpha
        Assert.Equal("alpha", a.Id);
        Assert.Equal("alpha-2", b.Id);
    }

    [Fact]
    public void IsRegistered_RejectsRetiredAndUnknown()
    {
        var store = BuildStore(BuildConfig());
        var live = store.Register(new RegisterClientRequest { DisplayName = "Live Client" });
        Assert.True(store.IsRegistered(live.Id));
        Assert.False(store.IsRegistered("nope-not-here"));

        var changed = store.SoftDelete(live.Id);
        Assert.True(changed);
        Assert.False(store.IsRegistered(live.Id));
        var afterDelete = store.Find(live.Id);
        Assert.NotNull(afterDelete);
        Assert.Equal(ClientIdentityKind.Retired, afterDelete!.Kind);
    }

    [Fact]
    public void ListAll_ReturnsBootstrapPlusRegistered()
    {
        var store = BuildStore(BuildConfig());
        store.Register(new RegisterClientRequest { DisplayName = "One" });
        store.Register(new RegisterClientRequest { DisplayName = "Two" });

        var list = store.ListAll();
        Assert.Equal(3, list.Count); // bootstrap + 2 registered
        Assert.Contains(list, c => c.Id == DefaultClientIdentity.Id);
        Assert.Contains(list, c => c.DisplayName == "One");
        Assert.Contains(list, c => c.DisplayName == "Two");
    }

    [Fact]
    public void Store_SurvivesRoundTripAcrossInstances()
    {
        var config = BuildConfig();
        var store1 = BuildStore(config);
        store1.Register(new RegisterClientRequest { DisplayName = "Persisted" });

        var store2 = BuildStore(config);
        var found = store2.Find("persisted");
        Assert.NotNull(found);
        Assert.Equal("Persisted", found!.DisplayName);
    }

    [Fact]
    public void Scanner_MigratesLegacyJobToLocalDefault()
    {
        var config = BuildConfig();
        var scanner = BuildScanner(config);

        var dir = Path.Combine(_watchPath, TaskStates.Ready, "legacy-job");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            "{\"id\":\"legacy-job\",\"title\":\"Legacy\",\"state\":\"2-ready\",\"order\":1,\"agent\":\"copilot\"}");

        var jobs = scanner.ScanAllJobs();
        var legacy = Assert.Single(jobs);
        Assert.Equal(DefaultClientIdentity.Id, legacy.OwnerClientId);

        // The migration is sticky: the file on disk now contains the field.
        var rewritten = File.ReadAllText(Path.Combine(dir, "task.json"));
        Assert.Contains("\"ownerClientId\"", rewritten);
        Assert.Contains(DefaultClientIdentity.Id, rewritten);
    }

    [Fact]
    public void Scanner_KeepsExplicitOwnerClientId()
    {
        var config = BuildConfig();
        var scanner = BuildScanner(config);

        var dir = Path.Combine(_watchPath, TaskStates.Ready, "owned-job");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            "{\"id\":\"owned-job\",\"title\":\"Owned\",\"state\":\"2-ready\",\"order\":1,\"agent\":\"copilot\",\"ownerClientId\":\"layer-3-review\"}");

        var jobs = scanner.ScanAllJobs();
        var owned = Assert.Single(jobs);
        Assert.Equal("layer-3-review", owned.OwnerClientId);
    }

    [Fact]
    public void DefaultIdentityCannotBeSoftDeletedTwice()
    {
        var store = BuildStore(BuildConfig());
        store.EnsureLoaded();
        // The endpoint blocks default identity deletion; the store itself
        // allows it for tests / direct use, but the second call returns false
        // because the kind is already retired.
        Assert.True(store.SoftDelete(DefaultClientIdentity.Id));
        Assert.False(store.SoftDelete(DefaultClientIdentity.Id));
    }
}
