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

    private ClientIdentityStore BuildStore(
        IConfiguration config,
        IAtomicJsonFileWriter? fileWriter = null,
        ILogger<ClientIdentityStore>? logger = null)
        => new(config, logger ?? NullLogger<ClientIdentityStore>.Instance, fileWriter);

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
    public void EnsureLoaded_NulIdentityBecomesVisibleDiagnostic_WithoutHidingHealthyIdentities()
    {
        var config = BuildConfig();
        var seeded = BuildStore(config);
        var healthy = seeded.Register(new RegisterClientRequest { DisplayName = "Healthy Runner" });
        var corrupt = seeded.Register(new RegisterClientRequest { DisplayName = "agent-runner-01" });
        File.WriteAllBytes(
            Path.Combine(_root, "identities", corrupt.Id + ".json"),
            new byte[4481]);
        var logger = new IdentityLogger();

        var reloaded = BuildStore(config, logger: logger);
        reloaded.EnsureLoaded();

        Assert.NotNull(reloaded.Find(healthy.Id));
        Assert.Null(reloaded.Find(corrupt.Id));
        var diagnostic = Assert.Single(reloaded.ListDiagnostics());
        Assert.Equal("agent-runner-01.json", diagnostic.FileName);
        Assert.Equal("identity file corrupt: agent-runner-01.json", diagnostic.Message);
        Assert.Contains("POST /api/clients/register", diagnostic.RestoreHint);
        Assert.Contains("idempotent on displayName", diagnostic.RestoreHint);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error
            && entry.Message.Contains("Client identity file corrupt", StringComparison.Ordinal)
            && entry.Message.Contains("agent-runner-01.json", StringComparison.Ordinal));
    }

    [Fact]
    public void ExternalRestore_IsReloadedWithoutBackendRestart()
    {
        var config = BuildConfig();
        var seeded = BuildStore(config);
        var runner = seeded.Register(new RegisterClientRequest
        {
            DisplayName = "agent-runner-01",
            Kind = ClientIdentityKinds.Service,
        });
        var path = Path.Combine(_root, "identities", runner.Id + ".json");
        var knownGood = File.ReadAllText(path);
        File.WriteAllBytes(path, new byte[4481]);
        var runningStore = BuildStore(config);

        runningStore.EnsureLoaded();
        Assert.Null(runningStore.Find(runner.Id));
        Assert.Single(runningStore.ListDiagnostics());

        File.WriteAllText(path, knownGood);

        Assert.Equal(runner.Id, runningStore.Find(runner.Id)?.Id);
        Assert.Empty(runningStore.ListDiagnostics());
        Assert.True(runningStore.IsRegistered(runner.Id));
    }

    [Fact]
    public void InterruptedAtomicWrite_LeavesAuthoritativeIdentityParseable()
    {
        var config = BuildConfig();
        var seeded = BuildStore(config);
        var runner = seeded.Register(new RegisterClientRequest { DisplayName = "Atomic Runner" });
        var path = Path.Combine(_root, "identities", runner.Id + ".json");
        var before = File.ReadAllText(path);
        var interrupted = new InterruptedIdentityWriter();
        var store = BuildStore(config, interrupted);
        Assert.NotNull(store.Find(runner.Id));
        interrupted.Interrupt = true;

        Assert.Throws<IOException>(() =>
            store.SetDefaults(runner.Id, "codex", "gpt-5", defaultThinkingLevel: "high"));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.NotNull(JsonSerializer.Deserialize<ClientIdentity>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        }));
        Assert.True(File.Exists(path + ".simulated-torn.tmp"));
    }

    [Fact]
    public void HighFrequencySeenAndUnchangedRunnerPolls_AreDebounced()
    {
        var writer = new ControllableAtomicJsonFileWriter();
        var store = BuildStore(BuildConfig(), writer);
        var runner = store.Register(new RegisterClientRequest
        {
            DisplayName = "Debounced Runner",
            Kind = ClientIdentityKinds.Service,
        });
        var path = Path.Combine(_root, "identities", runner.Id + ".json");

        store.RecordSeen(runner.Id);
        store.RecordRunnerActivity(runner.Id, activeSlots: 0, availableSlots: 2, claimed: false);
        var afterMaterialChange = writer.WritesFor(path);
        for (var i = 0; i < 20; i++)
        {
            store.RecordSeen(runner.Id);
            store.RecordRunnerActivity(runner.Id, activeSlots: 0, availableSlots: 2, claimed: false);
        }

        Assert.Equal(afterMaterialChange, writer.WritesFor(path));
        Assert.True(store.Find(runner.Id)!.LastSeenAt > runner.LastSeenAt);
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

    private sealed class InterruptedIdentityWriter : IAtomicJsonFileWriter
    {
        private readonly AtomicJsonFileWriter _inner = new();
        public bool Interrupt { get; set; }

        public void Write(string path, string content)
        {
            if (!Interrupt)
            {
                _inner.Write(path, content);
                return;
            }

            File.WriteAllBytes(path + ".simulated-torn.tmp", new byte[4481]);
            throw new IOException("Simulated process loss after the temp-file write and before rename.");
        }
    }

    private sealed class IdentityLogger : ILogger<ClientIdentityStore>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
