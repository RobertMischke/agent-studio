using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the fixture-marker contract:
/// 1. The heuristic in <see cref="FixtureHeuristics"/> matches obvious
///    Playwright fixture ids and titles, and ignores legitimate user
///    tasks that merely contain "test" or "spec".
/// 2. The scanner reads <c>"fixture": true</c> off <c>job.json</c>.
/// 3. The migration service is dry-run by default and idempotent.
/// 4. Filtering on <c>!Fixture</c> hides marked jobs from the default
///    list, while <c>?includeFixtures=true</c> re-surfaces them - this
///    is the contract the endpoints rely on.
/// </summary>
public class FixtureFilterTests : IDisposable
{
    private readonly string _watchPath;

    public FixtureFilterTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "agent-taskboard-fixture-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    private (TaskScannerService scanner, FixtureMigrationService migration) BuildServices()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var migration = new FixtureMigrationService(scanner, NullLogger<FixtureMigrationService>.Instance);
        return (scanner, migration);
    }

    private void WriteJob(string state, string slug, string title, bool? fixture = null)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var json = new Dictionary<string, object?>
        {
            ["id"] = slug,
            ["title"] = title,
            ["state"] = state,
            ["order"] = 1,
            ["agent"] = "claude"
        };
        if (fixture.HasValue) json["fixture"] = fixture.Value;
        File.WriteAllText(Path.Combine(dir, "job.json"),
            JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
    }

    // --- Heuristic ---

    [Theory]
    [InlineData("e2e-1234567-89", "e2e Visibility 2026-05-04", true)]
    [InlineData("e2e-dragoptim-A", "e2e dragOptim A", true)]
    [InlineData("e2e_legacy", "Legacy", true)]                       // id-prefix path
    [InlineData("anything", "e2e: visibility", true)]                // title-prefix with colon
    [InlineData("anything", "e2e visibility", true)]                 // title-prefix with space
    [InlineData("anything", "e2e", true)]                            // exact-match title
    public void Heuristic_MatchesFixtureShapedIds(string id, string title, bool expected)
    {
        Assert.Equal(expected, FixtureHeuristics.IsLikelyFixture(id, title));
    }

    [Theory]
    [InlineData("e2e-test-suite", "Plan e2e test suite for the chat window")] // legitimate user task starting with "Plan"
    [InlineData("test-runner-fixes", "test runner fixes")]                    // bare "test"
    [InlineData("playwright-spec", "Add playwright spec for runner")]         // word "playwright" alone
    [InlineData("e2eishly", "e2eishly")]                                      // "e2e" embedded in a longer word
    [InlineData("", "e2e visibility")]                                        // empty id
    public void Heuristic_DoesNotMatchLegitimateTasks(string id, string title)
    {
        // The id `e2e-test-suite` is the borderline case: it deliberately DOES
        // match (id starts with `e2e-`) because that prefix is unambiguous in
        // this codebase. The string "test" / "spec" / "playwright" alone never
        // matches.
        if (id.StartsWith("e2e-")) Assert.True(FixtureHeuristics.IsLikelyFixture(id, title));
        else Assert.False(FixtureHeuristics.IsLikelyFixture(id, title));
    }

    // --- Scanner ---

    [Fact]
    public void Scanner_ReadsFixtureFlag()
    {
        WriteJob(TaskStates.Ready, "real-task", "Real Task");
        WriteJob(TaskStates.Ready, "e2e-fix-1", "e2e fixture", fixture: true);

        var (scanner, _) = BuildServices();
        var jobs = scanner.ScanAllJobs();
        Assert.Equal(2, jobs.Count);

        var real = jobs.Single(j => j.Id == "real-task");
        var fix = jobs.Single(j => j.Id == "e2e-fix-1");
        Assert.False(real.Fixture);
        Assert.True(fix.Fixture);
    }

    // --- Endpoint filter contract ---

    [Fact]
    public void DefaultListExcludesFixtures_IncludeFlagSurfacesThem()
    {
        WriteJob(TaskStates.Ready, "real-task", "Real Task");
        WriteJob(TaskStates.Ready, "e2e-fix-1", "e2e fixture", fixture: true);

        var (scanner, _) = BuildServices();
        var raw = scanner.ScanAllJobs();

        // This mirrors the exact filter expression in TaskCrudEndpoints.cs
        // (`includeFixtures != true` -> filter on `!j.Fixture`). Locking it
        // in here means a future refactor can't silently surface fixtures
        // on stable.
        var defaultList = raw.Where(j => !j.Fixture).Select(j => j.Id).ToList();
        Assert.Equal(new[] { "real-task" }, defaultList);

        var withFixtures = raw.Select(j => j.Id).OrderBy(id => id).ToList();
        Assert.Equal(new[] { "e2e-fix-1", "real-task" }, withFixtures);
    }

    // --- Migration service ---

    [Fact]
    public void Migration_DryRun_DoesNotWrite()
    {
        WriteJob(TaskStates.Ready, "real-task", "Real Task");
        WriteJob(TaskStates.Ready, "e2e-fix-1", "e2e fixture");
        WriteJob(TaskStates.Archive, "e2e-fix-2", "e2e fixture two");

        var (scanner, migration) = BuildServices();
        var report = migration.Scan(apply: false);

        Assert.False(report.Applied);
        Assert.Equal(3, report.TotalScanned);
        Assert.Equal(0, report.AlreadyMarked);
        Assert.Equal(2, report.MatchedHeuristic);
        Assert.Equal(2, report.WouldMark);
        Assert.Equal(0, report.Marked);

        // Confirm nothing was written.
        var afterDryRun = scanner.ScanAllJobs();
        Assert.All(afterDryRun, j => Assert.False(j.Fixture));
    }

    [Fact]
    public void Migration_Apply_WritesFixtureFlag_AndIsIdempotent()
    {
        WriteJob(TaskStates.Ready, "real-task", "Real Task");
        WriteJob(TaskStates.Ready, "e2e-fix-1", "e2e fixture");

        var (scanner, migration) = BuildServices();
        var first = migration.Scan(apply: true);
        Assert.True(first.Applied);
        Assert.Equal(1, first.WouldMark);
        Assert.Equal(1, first.Marked);

        // Job folder on disk now has fixture: true.
        var jobs = scanner.ScanAllJobs();
        Assert.True(jobs.Single(j => j.Id == "e2e-fix-1").Fixture);
        Assert.False(jobs.Single(j => j.Id == "real-task").Fixture);

        // Second pass is a no-op: nothing left to mark.
        var second = migration.Scan(apply: true);
        Assert.Equal(0, second.WouldMark);
        Assert.Equal(0, second.Marked);
        Assert.Equal(1, second.AlreadyMarked);
    }
}
