using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the per-client default-CLI / default-model persistence layer
/// introduced for F17. The orchestrator's per-turn USER PREFERENCES block
/// reads from these fields; a regression here would silently degrade the
/// orchestrator back to a hardcoded "claude" fallback. The endpoint test
/// also pins that empty-string clears the side while null leaves it alone,
/// so a partial PUT can't accidentally wipe the other field.
/// </summary>
public class ClientDefaultsStoreTests : IDisposable
{
    private readonly string _root;

    public ClientDefaultsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ats-f17-defaults-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private IConfiguration BuildConfig()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root
            })
            .Build();

    [Fact]
    public void SetDefaults_RoundTripsThroughDisk()
    {
        var store = new ClientIdentityStore(BuildConfig(), NullLogger<ClientIdentityStore>.Instance);
        store.EnsureLoaded();

        var alice = store.Register(new RegisterClientRequest { DisplayName = "Alice" });
        var updated = store.SetDefaults(alice.Id, "claude", "claude-opus-4-7");
        Assert.NotNull(updated);
        Assert.Equal("claude", updated!.DefaultCliType);
        Assert.Equal("claude-opus-4-7", updated.DefaultModel);

        var withThinking = store.SetDefaults(alice.Id, null, null, defaultThinkingLevel: "xhigh");
        Assert.Equal("xhigh", withThinking!.DefaultThinkingLevel);

        // Re-load from disk and verify the change survived.
        var freshStore = new ClientIdentityStore(BuildConfig(), NullLogger<ClientIdentityStore>.Instance);
        freshStore.EnsureLoaded();
        var reloaded = freshStore.Find(alice.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("claude", reloaded!.DefaultCliType);
        Assert.Equal("claude-opus-4-7", reloaded.DefaultModel);
        Assert.Equal("xhigh", reloaded.DefaultThinkingLevel);
    }

    [Fact]
    public void SetDefaults_PartialUpdatePreservesOtherSide()
    {
        var store = new ClientIdentityStore(BuildConfig(), NullLogger<ClientIdentityStore>.Instance);
        store.EnsureLoaded();
        var alice = store.Register(new RegisterClientRequest { DisplayName = "Alice" });
        store.SetDefaults(alice.Id, "claude", "claude-opus-4-7");

        // Pass null for cli (leave alone), new model.
        var updated = store.SetDefaults(alice.Id, defaultCliType: null, defaultModel: "claude-haiku-4-5-20251001");
        Assert.Equal("claude", updated!.DefaultCliType);
        Assert.Equal("claude-haiku-4-5-20251001", updated.DefaultModel);
        Assert.Null(updated.DefaultThinkingLevel);
    }

    [Fact]
    public void SetDefaults_ClearFlagsWipeSelectively()
    {
        var store = new ClientIdentityStore(BuildConfig(), NullLogger<ClientIdentityStore>.Instance);
        store.EnsureLoaded();
        var alice = store.Register(new RegisterClientRequest { DisplayName = "Alice" });
        store.SetDefaults(alice.Id, "claude", "claude-opus-4-7");

        // clearModel = true wipes only the model; cli stays.
        var updated = store.SetDefaults(alice.Id, defaultCliType: null, defaultModel: null, clearCli: false, clearModel: true);
        Assert.Equal("claude", updated!.DefaultCliType);
        Assert.Null(updated.DefaultModel);

        // clearCli = true now wipes the cli side too.
        var bothCleared = store.SetDefaults(alice.Id, defaultCliType: null, defaultModel: null, clearCli: true, clearModel: false);
        Assert.Null(bothCleared!.DefaultCliType);
        Assert.Null(bothCleared.DefaultModel);

        var thinking = store.SetDefaults(alice.Id, defaultCliType: null, defaultModel: null, defaultThinkingLevel: "high");
        Assert.Equal("high", thinking!.DefaultThinkingLevel);
        var thinkingCleared = store.SetDefaults(alice.Id, defaultCliType: null, defaultModel: null, clearThinkingLevel: true);
        Assert.Null(thinkingCleared!.DefaultThinkingLevel);
    }

    [Fact]
    public void SetDefaults_UnknownClient_ReturnsNull()
    {
        var store = new ClientIdentityStore(BuildConfig(), NullLogger<ClientIdentityStore>.Instance);
        store.EnsureLoaded();
        Assert.Null(store.SetDefaults("nope", "claude", "claude-opus-4-7"));
    }

    [Fact]
    public void RunnerGitCapability_RoundTripsThroughDiskAndSummary()
    {
        var config = BuildConfig();
        var store = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        var runner = store.Register(new RegisterClientRequest { DisplayName = "agent-runner-01", Kind = "service" });
        var checkedAt = new DateTime(2026, 7, 11, 20, 0, 0, DateTimeKind.Utc);

        var updated = store.SetRunnerGitCapability(
            runner.Id,
            "ready-no-workflow-scope",
            "contents ready; workflow permission missing",
            checkedAt);
        var reloaded = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance).Find(runner.Id);
        var summary = ClientSummary.From(reloaded!);

        Assert.Equal("ready-no-workflow-scope", updated!.RunnerGitStatus);
        Assert.Equal("ready-no-workflow-scope", summary.RunnerGitStatus);
        Assert.Equal("contents ready; workflow permission missing", summary.RunnerGitDetail);
        Assert.Equal(checkedAt, summary.RunnerGitCheckedAt);
    }

    [Fact]
    public void ClientSummary_ExposesDefaults()
    {
        var identity = new ClientIdentity
        {
            Id = "x",
            DisplayName = "X",
            Kind = ClientIdentityKind.Human,
            RegisteredAt = DateTime.UtcNow,
            DefaultCliType = "codex",
            DefaultModel = "gpt-5",
            DefaultThinkingLevel = "medium"
        };
        var s = ClientSummary.From(identity);
        Assert.Equal("codex", s.DefaultCliType);
        Assert.Equal("gpt-5", s.DefaultModel);
        Assert.Equal("medium", s.DefaultThinkingLevel);
    }
}
