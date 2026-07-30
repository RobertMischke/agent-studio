using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the session-state stability contract for clean-context runs
/// (MKT-8 / WEB-14 "Codex rollout state loss"): all attempts/recoveries of the
/// same task must reuse ONE isolated config home so the CLI's session state
/// (Codex <c>sessions/rollout-*.jsonl</c>, Claude transcripts) survives a
/// restart between attempts, and the stored session id stays resumable. A
/// fresh home may be cut only at run boundaries. Before this contract, every
/// <c>StartAsync</c> seeded a brand-new CODEX_HOME, which deleted the rollout
/// mid-task and forced each continuation into full-context recovery
/// ("Codex rollout is absent from the new clean-context CODEX_HOME").
/// </summary>
public class CleanContextSessionStabilityTests
{
    /// <summary>
    /// Minimal engine whose behavior supports clean context with a real
    /// on-disk temp home per preparation - no process spawn involved; the
    /// tests drive <see cref="GenericCliExecutionService.AcquireCleanContext"/>
    /// directly.
    /// </summary>
    private sealed class FakeCleanCliService : GenericCliExecutionService
    {
        public FakeCleanCliService()
            : base(BuildBehavior(), NullLogger<FakeCleanCliService>.Instance, new ConfigurationBuilder().Build())
        {
        }

        private static CliBehavior BuildBehavior() => new()
        {
            CliType = "fake-clean",
            GetCliPath = _ => "unused",
            BuildStartInfo = (_, _, workingDirectory, _, _, _, _, _) =>
                new System.Diagnostics.ProcessStartInfo { FileName = "unused", WorkingDirectory = workingDirectory },
            SupportsCleanContext = true,
            PrepareCleanContext = (_, _) =>
            {
                var home = Path.Combine(
                    Path.GetTempPath(), "atp-clean-context", $"fake-clean-{Guid.NewGuid():N}");
                Directory.CreateDirectory(home);
                return new CleanContextPreparation(
                    "fake-clean",
                    home,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["FAKE_HOME"] = home },
                    new List<CliContextSource>());
            },
        };
    }

    [Fact]
    public void AcquireCleanContext_SecondAttemptOfSameTask_ReusesTheHome()
    {
        var svc = new FakeCleanCliService();
        const string jobKey = "proj::task-1";

        var (first, firstReused) = svc.AcquireCleanContext(jobKey, Path.GetTempPath());
        var (second, secondReused) = svc.AcquireCleanContext(jobKey, Path.GetTempPath());
        try
        {
            Assert.NotNull(first);
            Assert.False(firstReused);
            Assert.NotNull(second);
            Assert.True(secondReused);
            // Same preparation object → same home → the rollout written by
            // attempt 1 is still there for attempt 2's resume.
            Assert.Same(first, second);
            Assert.True(Directory.Exists(second!.TempHome));
        }
        finally { first?.Dispose(); }
    }

    [Fact]
    public void AcquireCleanContext_DifferentTasks_GetIsolatedHomes()
    {
        var svc = new FakeCleanCliService();

        var (a, _) = svc.AcquireCleanContext("proj::task-a", Path.GetTempPath());
        var (b, _) = svc.AcquireCleanContext("proj::task-b", Path.GetTempPath());
        try
        {
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.NotEqual(a!.TempHome, b!.TempHome);
        }
        finally
        {
            a?.Dispose();
            b?.Dispose();
        }
    }

    [Fact]
    public void AcquireCleanContext_HomeVanished_CutsAFreshOne()
    {
        var svc = new FakeCleanCliService();
        const string jobKey = "proj::task-2";

        var (first, _) = svc.AcquireCleanContext(jobKey, Path.GetTempPath());
        Assert.NotNull(first);
        Directory.Delete(first!.TempHome, recursive: true);

        var (second, secondReused) = svc.AcquireCleanContext(jobKey, Path.GetTempPath());
        try
        {
            Assert.NotNull(second);
            Assert.False(secondReused);
            Assert.NotEqual(first.TempHome, second!.TempHome);
            Assert.True(Directory.Exists(second.TempHome));
        }
        finally { second?.Dispose(); }
    }

    [Fact]
    public void GetPersistentCleanContextHome_ReflectsLiveRegistration()
    {
        var svc = new FakeCleanCliService();
        const string jobKey = "proj::task-3";

        Assert.Null(svc.GetPersistentCleanContextHome(jobKey));

        var (prep, _) = svc.AcquireCleanContext(jobKey, Path.GetTempPath());
        Assert.NotNull(prep);
        Assert.Equal(prep!.TempHome, svc.GetPersistentCleanContextHome(jobKey));

        // A vanished home must read as "nothing to resume", not a dead path.
        Directory.Delete(prep.TempHome, recursive: true);
        Assert.Null(svc.GetPersistentCleanContextHome(jobKey));
    }

    [Fact]
    public void GetPersistentCleanContextHome_FeedsCodexResumeViability()
    {
        // End-to-end over the two pieces the runner composes: the persistent
        // per-task home plus CodexRolloutStore.CanResume. A rollout written by
        // attempt 1 into the shared task home makes the clean-context resume
        // viable for attempt 2.
        var svc = new FakeCleanCliService();
        const string jobKey = "proj::task-4";
        const string sessionId = "019dee65-7a9b-7843-bfd9-06e555fff02b";

        var (prep, _) = svc.AcquireCleanContext(jobKey, Path.GetTempPath());
        Assert.NotNull(prep);
        try
        {
            var day = Path.Combine(prep!.TempHome, "sessions", "2026", "07", "30");
            Directory.CreateDirectory(day);
            File.WriteAllText(Path.Combine(day, $"rollout-2026-07-30T10-00-00-{sessionId}.jsonl"), "{}\n");

            Assert.True(CodexRolloutStore.CanResume(
                sessionId, "clean",
                sharedHome: null,
                cleanHome: svc.GetPersistentCleanContextHome(jobKey)));
        }
        finally { prep?.Dispose(); }
    }
}
