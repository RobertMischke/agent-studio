using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the cross-slug infra circuit breaker contract from the loop
/// inventory's <c>pickup.cross-slug-infra-circuit-breaker</c> entry:
/// after N distinct slugs spawn-fail for the same <c>cliType</c> within the
/// rolling window, the breaker trips and the runner halts pickup. See
/// <see cref="CrossSlugInfraCircuitBreaker"/> for the design rationale and
/// the 2026-05-06 incident motivation.
///
/// <para>
/// The tests drive synthetic dead-letter events directly into the breaker.
/// The integration with <see cref="ProjectRunner"/> (mode flip, banner
/// chat note, infra-halts.jsonl row, picker halts mid-iteration) is
/// covered in <c>PickupLoopStrictIterationTests.CrossSlug_*</c>.
/// </para>
/// </summary>
public sealed class CrossSlugInfraCircuitBreakerTests : IDisposable
{
    private readonly string _workspaceRoot;
    private const string Project = "demo";
    private const string Claude = "claude";
    private const string Codex = "codex";

    public CrossSlugInfraCircuitBreakerTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-infra-breaker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ===== Trip contract =====

    [Fact]
    public void TwoDistinctSlugsWithinWindow_Trips()
    {
        var breaker = Build();
        var t = DateTime.UtcNow;

        var first = breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t);
        Assert.Null(first); // 1 of 2, not tripped

        var second = breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-b", t.AddSeconds(20));
        Assert.NotNull(second);
        Assert.Equal(Project, second!.ProjectName);
        Assert.Equal(Claude, second.CliType);
        Assert.Equal(new[] { "slug-a", "slug-b" }, second.Slugs);
        Assert.Equal(2, second.Limit);
        Assert.True(breaker.IsTripped(Project, Claude));
    }

    [Fact]
    public void OneDistinctSlugRepeated_DoesNotTrip()
    {
        // The per-slug breaker already handles "this one job is broken". The
        // cross-slug breaker must distinguish that from "the CLI itself is
        // broken" - re-counting the same slug must NOT trip.
        var breaker = Build();
        var t = DateTime.UtcNow;

        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t);
        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t.AddSeconds(30));
        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t.AddSeconds(60));

        Assert.False(breaker.IsTripped(Project, Claude));
        Assert.Equal(1, breaker.GetEntryCount(Project, Claude));
    }

    [Fact]
    public void TwoDistinctSlugsOutsideWindow_DoesNotTrip()
    {
        var breaker = Build();
        var t = DateTime.UtcNow;

        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t);
        // 11 minutes later: outside the 10-minute window, prior entry expires.
        var result = breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-b", t.AddMinutes(11));

        Assert.Null(result);
        Assert.False(breaker.IsTripped(Project, Claude));
        Assert.Equal(1, breaker.GetEntryCount(Project, Claude));
    }

    [Fact]
    public void SecondTripWithinWindow_IsSuppressed()
    {
        // Once the breaker is tripped, the runner is already manual; a second
        // dead-letter within the same window must not raise a second banner.
        var breaker = Build();
        var t = DateTime.UtcNow;

        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t);
        var firstTrip = breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-b", t.AddSeconds(20));
        Assert.NotNull(firstTrip);

        var secondTrip = breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-c", t.AddSeconds(40));
        Assert.Null(secondTrip);
        Assert.True(breaker.IsTripped(Project, Claude));
    }

    // ===== Reset contracts =====

    [Fact]
    public void Reset_OnProductivePickup_ClearsCounter()
    {
        var breaker = Build();
        var t = DateTime.UtcNow;

        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t);
        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-b", t.AddSeconds(20));
        Assert.True(breaker.IsTripped(Project, Claude));

        breaker.OnProductivePickup(Project, Claude);

        Assert.False(breaker.IsTripped(Project, Claude));
        Assert.Equal(0, breaker.GetEntryCount(Project, Claude));
    }

    [Fact]
    public void Reset_OnOperatorResumeAuto_ClearsCounter()
    {
        var breaker = Build();
        var t = DateTime.UtcNow;

        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t);
        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-b", t.AddSeconds(20));
        Assert.True(breaker.IsTripped(Project, Claude));

        breaker.OnOperatorResumeAuto(Project);

        Assert.False(breaker.IsTripped(Project, Claude));
        Assert.Equal(0, breaker.GetEntryCount(Project, Claude));
    }

    [Fact]
    public void Reset_OnOperatorResumeAuto_ClearsAllCliTypesForProject()
    {
        var breaker = Build();
        var t = DateTime.UtcNow;

        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t);
        breaker.RecordSpawnFailedDeadLetter(Project, Codex, "slug-b", t);

        breaker.OnOperatorResumeAuto(Project);

        Assert.Equal(0, breaker.GetEntryCount(Project, Claude));
        Assert.Equal(0, breaker.GetEntryCount(Project, Codex));
    }

    // ===== Per-CLI isolation =====

    [Fact]
    public void DifferentCliTypes_DoNotShareCounter()
    {
        // The breaker keys on (projectName, cliType) - a spawn-fail on
        // claude must not feed the codex counter.
        var breaker = Build();
        var t = DateTime.UtcNow;

        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t);
        var result = breaker.RecordSpawnFailedDeadLetter(Project, Codex, "slug-b", t.AddSeconds(20));

        Assert.Null(result);
        Assert.False(breaker.IsTripped(Project, Claude));
        Assert.False(breaker.IsTripped(Project, Codex));
        Assert.Equal(1, breaker.GetEntryCount(Project, Claude));
        Assert.Equal(1, breaker.GetEntryCount(Project, Codex));
    }

    [Fact]
    public void DifferentProjects_DoNotShareCounter()
    {
        var breaker = Build();
        var t = DateTime.UtcNow;

        breaker.RecordSpawnFailedDeadLetter("project-a", Claude, "slug-a", t);
        var result = breaker.RecordSpawnFailedDeadLetter("project-b", Claude, "slug-b", t.AddSeconds(20));

        Assert.Null(result);
        Assert.Equal(1, breaker.GetEntryCount("project-a", Claude));
        Assert.Equal(1, breaker.GetEntryCount("project-b", Claude));
    }

    // ===== Persistence to infra-halts.jsonl =====

    [Fact]
    public void Trip_WritesInfraHaltsJsonlRow()
    {
        var breaker = Build();
        var t = DateTime.UtcNow;

        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t);
        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-b", t.AddSeconds(20));

        var jsonl = Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl");
        Assert.True(File.Exists(jsonl));
        var lines = File.ReadAllLines(jsonl).Where(l => l.Length > 0).ToList();
        Assert.Single(lines);
        var row = lines[0];

        Assert.Contains("\"kind\":\"cross-slug-spawn-failed-cascade\"", row);
        Assert.Contains("\"projectName\":\"demo\"", row);
        Assert.Contains("\"cliType\":\"claude\"", row);
        Assert.Contains("\"slugs\":[\"slug-a\",\"slug-b\"]", row);
        Assert.Contains("\"windowMinutes\":10", row);
        Assert.Contains("\"limit\":2", row);
        Assert.Contains("\"reason\":\"", row);
    }

    // ===== Config overrides =====

    [Fact]
    public void ConfiguredLimitOfThree_DoesNotTripAtTwo()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspaceRoot,
            ["Supervisor:CrossSlugInfraSilentLimit"] = "3"
        });
        var haltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var breaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, haltLog);
        var t = DateTime.UtcNow;

        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t);
        var second = breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-b", t.AddSeconds(20));
        Assert.Null(second);

        var third = breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-c", t.AddSeconds(40));
        Assert.NotNull(third);
        Assert.Equal(3, third!.Limit);
    }

    [Fact]
    public void ConfiguredWindowOfTwoMinutes_ExpiresOlderEntries()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspaceRoot,
            ["Supervisor:CrossSlugInfraSilentWindowMinutes"] = "2"
        });
        var haltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var breaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, haltLog);
        var t = DateTime.UtcNow;

        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t);
        // 3 minutes later: outside the 2-minute window.
        var second = breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-b", t.AddMinutes(3));

        Assert.Null(second);
        Assert.False(breaker.IsTripped(Project, Claude));
        Assert.Equal(1, breaker.GetEntryCount(Project, Claude));
    }

    // ===== Banner copy contract =====

    [Fact]
    public void TripOutcome_BuildSupervisorChatMessage_IsPlainText()
    {
        var breaker = Build();
        var t = DateTime.UtcNow;
        breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-a", t);
        var trip = breaker.RecordSpawnFailedDeadLetter(Project, Claude, "slug-b", t.AddSeconds(20));
        Assert.NotNull(trip);

        var msg = trip!.BuildSupervisorChatMessage();
        // Plain text: no HTML tags, no markdown emphasis that the chat
        // renderer might pick up as immediate-tooltips.
        Assert.DoesNotContain("<", msg);
        Assert.DoesNotContain(">", msg);
        Assert.Contains("slug-a", msg);
        Assert.Contains("slug-b", msg);
        Assert.Contains("claude", msg);
        Assert.Contains("manual mode", msg);
        Assert.Contains("tools/check-cli-shims.sh", msg);
    }

    // ===== Helpers =====

    private CrossSlugInfraCircuitBreaker Build()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspaceRoot
        });
        var haltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        return new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, haltLog);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> kv)
        => new ConfigurationBuilder().AddInMemoryCollection(kv).Build();
}
