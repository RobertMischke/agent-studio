using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the GET / PUT roundtrip for the orchestrator config surface:
/// the snapshot reflects what's in <see cref="IConfiguration"/>, an
/// override write merges into a fresh <c>appsettings.Local.json</c>
/// without clobbering unrelated keys, unknown keys are rejected, and
/// type mismatches are rejected.
/// </summary>
public class OrchestratorConfigServiceTests : IDisposable
{
    private readonly string _contentRoot;

    public OrchestratorConfigServiceTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "orch-config-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_contentRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_contentRoot)) Directory.Delete(_contentRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private OrchestratorConfigService Build(
        Dictionary<string, string?>? settings = null,
        string? localFileContent = null)
    {
        if (localFileContent is not null)
        {
            File.WriteAllText(Path.Combine(_contentRoot, "appsettings.Local.json"), localFileContent);
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var env = new TestHostEnvironment(_contentRoot);
        return new OrchestratorConfigService(config, env, NullLogger<OrchestratorConfigService>.Instance);
    }

    [Fact]
    public void Snapshot_DefaultsAndCurrentValuesReflectConfiguration()
    {
        var svc = Build(new Dictionary<string, string?>
        {
            ["Supervisor:MetaCycleEnabled"] = "true",
            ["Supervisor:AutoInterventionRateLimit"] = "5"
        });

        var snap = svc.GetSnapshot();
        Assert.Contains(snap.Options, o => o.Key == "Supervisor:MetaCycleEnabled");
        var meta = snap.Options.Single(o => o.Key == "Supervisor:MetaCycleEnabled");
        Assert.Equal(false, meta.DefaultValue);
        Assert.Equal(true, meta.CurrentValue);
        Assert.True(meta.HasOverride);
        Assert.False(meta.RestartRequired);
        Assert.True(meta.AppliesImmediately);

        var rate = snap.Options.Single(o => o.Key == "Supervisor:AutoInterventionRateLimit");
        Assert.Equal(3, rate.DefaultValue);
        Assert.Equal(5, rate.CurrentValue);
    }

    [Fact]
    public void ApplyOverrides_WritesNestedJson_AndPreservesUnrelatedKeys()
    {
        var existing = """
        {
          "Environment": { "IsDev": true },
          "WatchPaths": [{ "Name": "Foo", "RootPath": "C:/Foo" }]
        }
        """;
        var svc = Build(localFileContent: existing);

        var values = new Dictionary<string, JsonElement>
        {
            ["Supervisor:MetaCycleEnabled"] = ParseElem("true"),
            ["Supervisor:AutoInterventionSeverityThreshold"] = ParseElem("\"Warn\""),
            ["ReviewDecisionOrchestrator:IntervalSeconds"] = ParseElem("45"),
        };
        svc.ApplyOverrides(values);

        var path = Path.Combine(_contentRoot, "appsettings.Local.json");
        var written = JsonDocument.Parse(File.ReadAllText(path)).RootElement;

        Assert.True(written.GetProperty("Environment").GetProperty("IsDev").GetBoolean());
        Assert.Equal("Foo", written.GetProperty("WatchPaths")[0].GetProperty("Name").GetString());

        var supervisor = written.GetProperty("Supervisor");
        Assert.True(supervisor.GetProperty("MetaCycleEnabled").GetBoolean());
        Assert.Equal("Warn", supervisor.GetProperty("AutoInterventionSeverityThreshold").GetString());

        var review = written.GetProperty("ReviewDecisionOrchestrator");
        Assert.Equal(45, review.GetProperty("IntervalSeconds").GetInt32());
    }

    [Fact]
    public void ApplyOverrides_RejectsUnknownKey()
    {
        var svc = Build();
        var values = new Dictionary<string, JsonElement>
        {
            ["Bogus:Flag"] = ParseElem("true")
        };
        Assert.Throws<ArgumentException>(() => svc.ApplyOverrides(values));
        Assert.False(File.Exists(Path.Combine(_contentRoot, "appsettings.Local.json")));
    }

    [Fact]
    public void ApplyOverrides_RejectsTypeMismatch()
    {
        var svc = Build();
        var values = new Dictionary<string, JsonElement>
        {
            ["Supervisor:MetaCycleEnabled"] = ParseElem("\"yes\"")
        };
        Assert.Throws<ArgumentException>(() => svc.ApplyOverrides(values));
    }

    [Fact]
    public void ApplyOverrides_RejectsInvalidEnumValue()
    {
        var svc = Build();
        var values = new Dictionary<string, JsonElement>
        {
            ["Supervisor:AutoInterventionSeverityThreshold"] = ParseElem("\"Bogus\"")
        };
        Assert.Throws<ArgumentException>(() => svc.ApplyOverrides(values));
    }

    [Fact]
    public void GetSnapshot_AfterWrite_PostWriteSnapshotShowsHasOverride()
    {
        // First snapshot uses configuration that does NOT carry the key.
        var svc = Build();
        Assert.False(svc.GetSnapshot().Options.Single(o => o.Key == "Supervisor:MetaCycleEnabled").HasOverride);

        // Apply a write; the JSON file is updated and the service reads
        // appsettings.Local.json back for the post-write snapshot so the
        // UI sees the persisted value immediately.
        svc.ApplyOverrides(new Dictionary<string, JsonElement>
        {
            ["Supervisor:MetaCycleEnabled"] = ParseElem("true")
        });

        var meta = svc.GetSnapshot().Options.Single(o => o.Key == "Supervisor:MetaCycleEnabled");
        Assert.True(meta.HasOverride);
        Assert.Equal(true, meta.CurrentValue);
        Assert.Equal(true, meta.ActiveValue);
    }

    private static JsonElement ParseElem(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
            ContentRootFileProvider = new PhysicalFileProvider(contentRoot);
        }
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "OrchestratorApi.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
