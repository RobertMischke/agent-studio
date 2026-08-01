using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

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
    public void Retire_PersistsAcrossRestart_AndCompletesOnlyAfterActiveWorkEnds()
    {
        var config = BuildConfig();
        var store = BuildStore(config);
        var runner = store.Register(new RegisterClientRequest { DisplayName = "agent-runner-01", Kind = ClientIdentityKinds.Service });

        store.RecordRunnerActivity(runner.Id, activeSlots: 1, availableSlots: 1, claimed: true);
        var draining = store.RequestDrain(runner.Id, retireAfterDrain: true);
        Assert.NotNull(draining?.RetireRequestedAt);
        Assert.Equal(ClientIdentityKind.Service, draining!.Kind);

        store.RecordRunnerActivity(runner.Id, activeSlots: 0, availableSlots: 2, claimed: false);
        var afterRestart = BuildStore(config).Find(runner.Id);
        Assert.Equal(ClientIdentityKind.Retired, afterRestart?.Kind);
        Assert.False(BuildStore(config).IsRegistered(runner.Id));
    }

    [Fact]
    public void Retire_DoesNotCompleteWhenClaimPollOmitsTelemetry()
    {
        var config = BuildConfig();
        var store = BuildStore(config);
        var runner = store.Register(new RegisterClientRequest { DisplayName = "telemetry-gap-runner", Kind = ClientIdentityKinds.Service });

        store.RecordRunnerActivity(runner.Id, activeSlots: 1, availableSlots: 1, claimed: true);
        store.RequestDrain(runner.Id, retireAfterDrain: true);

        var duringTelemetryGap = store.RecordRunnerActivity(
            runner.Id, activeSlots: null, availableSlots: 1, claimed: false);

        Assert.Equal(ClientIdentityKind.Service, duringTelemetryGap?.Kind);
        Assert.Equal(1, duringTelemetryGap?.RunnerActiveSlots);
        Assert.Equal(ClientIdentityKind.Service, BuildStore(config).Find(runner.Id)?.Kind);
    }

    [Fact]
    public void HostCapacity_IsSeededOnFirstContact_AndThenOwnedByTheOperator()
    {
        var config = BuildConfig();
        var store = BuildStore(config);
        var runner = store.Register(new RegisterClientRequest
        {
            DisplayName = "capacity-runner",
            Kind = ClientIdentityKinds.Service,
        });

        var seeded = store.RecordRunnerActivity(
            runner.Id, activeSlots: 0, availableSlots: 1, claimed: false, seedMaxParallelism: 6);
        Assert.Equal(6, seeded?.RunnerDesiredMaxParallelism);
        Assert.Equal(HostCapacityPolicy.DefaultTargetLoadPercent, seeded?.RunnerTargetLoadPercent);
        Assert.Equal(RunnerRampStrategies.Balanced, seeded?.RunnerRampStrategy);

        var operatorSet = store.SetRunnerCapacity(runner.Id, 12, 85, "conservative");
        Assert.Equal(12, operatorSet?.RunnerDesiredMaxParallelism);
        Assert.Equal(85, operatorSet?.RunnerTargetLoadPercent);
        Assert.Equal(RunnerRampStrategies.Conservative, operatorSet?.RunnerRampStrategy);
        Assert.NotNull(operatorSet?.RunnerCapacityUpdatedAt);

        // A later daemon poll must not push its own bootstrap value back over
        // the operator's ceiling.
        var afterPoll = store.RecordRunnerActivity(
            runner.Id, activeSlots: 2, availableSlots: 99, claimed: false, seedMaxParallelism: 2);
        Assert.Equal(12, afterPoll?.RunnerDesiredMaxParallelism);
        Assert.Equal(12, BuildStore(config).Find(runner.Id)?.RunnerDesiredMaxParallelism);
    }

    [Fact]
    public void SlotLedger_DerivesFreeSlotsFromTheCeiling_NotFromTheDaemonReport()
    {
        var store = BuildStore(BuildConfig());
        var runner = store.Register(new RegisterClientRequest
        {
            DisplayName = "ledger-runner",
            Kind = ClientIdentityKinds.Service,
        });
        store.SetRunnerCapacity(runner.Id, 8, 80, RunnerRampStrategies.Balanced);

        // The daemon's breathing "1 free" used to become the ledger total
        // (active + 1); free must follow the ceiling instead.
        var busy = store.RecordRunnerActivity(
            runner.Id, activeSlots: 7, availableSlots: 1, claimed: true);
        Assert.Equal(7, busy?.RunnerActiveSlots);
        Assert.Equal(1, busy?.RunnerAvailableSlots);

        var quieter = store.RecordRunnerActivity(
            runner.Id, activeSlots: 2, availableSlots: 1, claimed: false);
        Assert.Equal(2, quieter?.RunnerActiveSlots);
        Assert.Equal(6, quieter?.RunnerAvailableSlots);
    }

    [Fact]
    public void HostCapacity_RecordsWhichCeilingTheDaemonAdopted()
    {
        var store = BuildStore(BuildConfig());
        var runner = store.Register(new RegisterClientRequest
        {
            DisplayName = "adoption-runner",
            Kind = ClientIdentityKinds.Service,
        });
        store.SetRunnerCapacity(runner.Id, 10, 80, RunnerRampStrategies.Balanced);

        var appliedAt = new DateTime(2026, 7, 27, 9, 30, 0, DateTimeKind.Utc);
        var reported = store.RecordRunnerActivity(
            runner.Id,
            activeSlots: 1,
            availableSlots: 0,
            claimed: false,
            effectiveMaxParallelism: 4,
            effectiveMaxParallelismAppliedAt: appliedAt);

        Assert.Equal(10, reported?.RunnerDesiredMaxParallelism);
        Assert.Equal(4, reported?.RunnerEffectiveMaxParallelism);
        Assert.Equal(appliedAt, reported?.RunnerEffectiveMaxParallelismAppliedAt);
    }

    [Fact]
    public void SetRunnerCapacity_ClampsOutOfRangeTargets_AndRefusesUnknownHosts()
    {
        var store = BuildStore(BuildConfig());
        var runner = store.Register(new RegisterClientRequest
        {
            DisplayName = "clamp-runner",
            Kind = ClientIdentityKinds.Service,
        });

        var clamped = store.SetRunnerCapacity(runner.Id, 9999, 10, "nonsense");
        Assert.Equal(256, clamped?.RunnerDesiredMaxParallelism);
        Assert.Equal(50, clamped?.RunnerTargetLoadPercent);
        Assert.Equal(RunnerRampStrategies.Balanced, clamped?.RunnerRampStrategy);
        Assert.Null(store.SetRunnerCapacity("no-such-host", 4, 80, "balanced"));
    }

    [Fact]
    public void SetRunnerCapacity_WithoutAMaxParallelism_KeepsTheCeiling_AndNeverInventsOne()
    {
        var store = BuildStore(BuildConfig());
        var undeclared = store.Register(new RegisterClientRequest
        {
            DisplayName = "undeclared-runner",
            Kind = ClientIdentityKinds.Service,
        });

        // Changing only the ramp on a host that never declared a capacity must
        // not conjure a ceiling: the server would start enforcing a cap nobody
        // asked for.
        var rampOnly = store.SetRunnerCapacity(undeclared.Id, null, null, "conservative");
        Assert.Null(rampOnly?.RunnerDesiredMaxParallelism);
        Assert.Equal(RunnerRampStrategies.Conservative, rampOnly?.RunnerRampStrategy);
        Assert.Equal(HostCapacityPolicy.DefaultTargetLoadPercent, rampOnly?.RunnerTargetLoadPercent);

        // With a ceiling in place, an omitted value keeps it unchanged.
        store.SetRunnerCapacity(undeclared.Id, 5, null, null);
        var loadOnly = store.SetRunnerCapacity(undeclared.Id, null, 90, null);
        Assert.Equal(5, loadOnly?.RunnerDesiredMaxParallelism);
        Assert.Equal(90, loadOnly?.RunnerTargetLoadPercent);
    }

    [Fact]
    public void RetiredHost_CanBeRevived_ThenPermanentlyDeleted()
    {
        var config = BuildConfig();
        var store = BuildStore(config);
        var runner = store.Register(new RegisterClientRequest { DisplayName = "revivable-runner", Kind = ClientIdentityKinds.Service });
        var retired = store.RequestDrain(runner.Id, retireAfterDrain: true);
        Assert.Equal(ClientIdentityKind.Retired, retired?.Kind);

        var revived = store.Revive(runner.Id);
        Assert.Equal(ClientIdentityKind.Service, revived?.Kind);
        Assert.True(store.IsRegistered(runner.Id));

        Assert.True(store.SoftDelete(runner.Id));
        Assert.True(store.PermanentlyDelete(runner.Id));
        Assert.Null(BuildStore(config).Find(runner.Id));
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
