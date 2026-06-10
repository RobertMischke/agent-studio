using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the boot-time silent-death classifier — the only layer that can
/// surface a StackOverflow / OOM / native-kill, since those terminate the host
/// before any in-process handler runs. Two layers are exercised: the pure
/// <see cref="CrashForensics.Classify"/> decider, and the
/// <see cref="CrashRecorder"/> marker round-trip that drives it from disk.
/// </summary>
public sealed class CrashForensicsTests
{
    private static readonly DateTime T0 = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Classify_FirstBoot_WhenNoPreviousStartup()
    {
        Assert.Equal(
            PreviousRunVerdict.FirstBoot,
            CrashForensics.Classify(previousStartedAt: null, lastShutdownAt: null, lastCrashAt: null));
    }

    [Fact]
    public void Classify_SilentKill_WhenStartedButNoMarkersFollowed()
    {
        // The previous run started; neither a shutdown nor a crash marker was
        // written after it. That is the silent-disappearance class.
        Assert.Equal(
            PreviousRunVerdict.SilentKill,
            CrashForensics.Classify(previousStartedAt: T0, lastShutdownAt: null, lastCrashAt: null));
    }

    [Fact]
    public void Classify_SilentKill_WhenOnlyStaleMarkersFromAnEarlierRunExist()
    {
        // Markers exist but predate the previous run's start — they belong to
        // an even older run and must not mask a fresh silent death.
        var stale = T0.AddMinutes(-30);
        Assert.Equal(
            PreviousRunVerdict.SilentKill,
            CrashForensics.Classify(previousStartedAt: T0, lastShutdownAt: stale, lastCrashAt: stale));
    }

    [Fact]
    public void Classify_ManagedCrash_WhenCrashMarkerFollowedStartButNoShutdown()
    {
        Assert.Equal(
            PreviousRunVerdict.ManagedCrash,
            CrashForensics.Classify(previousStartedAt: T0, lastShutdownAt: null, lastCrashAt: T0.AddSeconds(40)));
    }

    [Fact]
    public void Classify_GracefulShutdown_WhenShutdownMarkerFollowedStart()
    {
        // A shutdown marker after the start means ProcessExit ran: clean
        // teardown wins even if a (non-terminating) crash was also recorded.
        Assert.Equal(
            PreviousRunVerdict.GracefulShutdown,
            CrashForensics.Classify(previousStartedAt: T0, lastShutdownAt: T0.AddSeconds(50), lastCrashAt: T0.AddSeconds(40)));
    }

    [Fact]
    public void Recorder_ArmsStartupMarker_AndSeesSilentKillOnNextBoot()
    {
        using var temp = new TempDir();
        var options = new BackendFileLoggerOptions { LogDirectory = temp.Path, RetentionDays = 14 };
        using var sink = new BackendFileLogSink(options);
        var recorder = new CrashRecorder(options, sink);

        // Boot 1: first boot — arms startup.json, no prior markers.
        var first = recorder.ClassifyPreviousRunAndArm();
        Assert.Equal(PreviousRunVerdict.FirstBoot, first.Verdict);
        Assert.True(File.Exists(recorder.StartupMarkerPath), "startup.json must be armed");
        Assert.False(File.Exists(recorder.SilentKillMarkerPath));

        // Boot 2: the previous run left only its startup marker (no shutdown,
        // no crash) — exactly what a StackOverflow / OOM / native kill leaves.
        var second = recorder.ClassifyPreviousRunAndArm();
        Assert.Equal(PreviousRunVerdict.SilentKill, second.Verdict);
        Assert.True(File.Exists(recorder.SilentKillMarkerPath), "a silent kill must drop last-silent-kill.json");

        var marker = JsonDocument.Parse(File.ReadAllText(recorder.SilentKillMarkerPath));
        Assert.Equal("SilentKill", marker.RootElement.GetProperty("verdict").GetString());

        var logFile = Path.Combine(temp.Path, $"{DateTime.UtcNow:yyyy-MM-dd}.log");
        Assert.Contains("Backend.Startup", File.ReadAllText(logFile));
        Assert.Contains("SilentKill", File.ReadAllText(logFile));
    }

    [Fact]
    public void Recorder_ClassifiesManagedCrash_FromRecordedMarker()
    {
        using var temp = new TempDir();
        var options = new BackendFileLoggerOptions { LogDirectory = temp.Path, RetentionDays = 14 };
        using var sink = new BackendFileLogSink(options);
        var recorder = new CrashRecorder(options, sink);

        // Boot 1 arms the startup marker.
        recorder.ClassifyPreviousRunAndArm();
        // The run then crashes (managed), recording last-crash.json after start.
        recorder.Record("HostedServiceTest", new InvalidOperationException("boom"), isTerminating: true);

        // Boot 2 sees a crash marker newer than the startup marker, no shutdown.
        var verdict = recorder.ClassifyPreviousRunAndArm();
        Assert.Equal(PreviousRunVerdict.ManagedCrash, verdict.Verdict);
        Assert.False(File.Exists(recorder.SilentKillMarkerPath));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atp-forensics-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
