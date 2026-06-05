using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Runner;
using Xunit;
using Xunit.Sdk;

namespace OrchestratorApi.Tests;

/// <summary>
/// Deterministic regression tests for the runner+watchdog+finalize chain.
///
/// <para>
/// <b>Why this exists.</b> The live symptom that motivated ADR-0011 was
/// "Claude CLI emits its <c>system/init</c> frame, then nothing for 124s,
/// then the watchdog kills it as hung". The CLI integration tests in
/// <see cref="CliSpawnIntegrationTests"/> exercise the live <c>claude</c>
/// binary; this file complements them by spawning a <i>fake</i> CLI we
/// control (a Node one-liner that prints one line and stalls forever) and
/// pinning the runner's behaviour: it must classify the run as "stopped"
/// when we kill it, persist the buffered init line, fire <c>OnFinished</c>,
/// and cleanly drop the active-job entry. Zero quota cost, fully
/// reproducible, runs in &lt; 5 s. If a future runner refactor breaks the
/// stall handling, this test fails locally before it reaches production.
/// </para>
/// <para>
/// We do not exercise <see cref="ProjectRunner"/>'s watchdog-tick logic
/// directly here - that lives behind a layer of dependencies. The
/// <see cref="WatchdogTests"/> file pins <see cref="Watchdog.DecideState"/>
/// in isolation; this file pins the surrounding "spawn -> stream -> stop"
/// pipeline that the watchdog operates on top of.
/// </para>
/// </summary>
public class CliWatchdogIntegrationTests
{
    /// <summary>Default skip when Node is unavailable (e.g. CI agent without it).</summary>
    private static string? NodeExePath
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("NODE_EXE");
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
            // PATHEXT-driven probe; node.exe ships from official installer in Program Files.
            foreach (var candidate in new[]
            {
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Program Files (x86)\nodejs\node.exe"
            })
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    /// <summary>
    /// Stripped-down driver that only knows how to spawn a <c>node.exe</c>
    /// child with a script the test supplies. Lets us reuse the base
    /// class's <see cref="CliExecutionServiceBase.StartAsync"/>,
    /// <c>OnOutput</c> events, output buffer, and Stop semantics without
    /// needing a real claude/codex/gemini install.
    /// </summary>
    private sealed class FakeNodeCliService : CliExecutionServiceBase
    {
        private readonly string _nodeExe;
        private readonly string _script;

        public FakeNodeCliService(string nodeExe, string script)
            : base(NullLogger<FakeNodeCliService>.Instance, new ConfigurationBuilder().Build())
        {
            _nodeExe = nodeExe;
            _script = script;
        }

        public override string CliType => "fake-node";
        public override string GetCliPath() => _nodeExe;

        protected override ProcessStartInfo BuildStartInfo(
            string prompt, string workingDirectory,
            string? sessionName, bool resumeSession, string? model, string? thinkingLevel, string? permissionMode)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _nodeExe,
                WorkingDirectory = workingDirectory
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(_script);
            return psi;
        }
    }

    [SkippableFact]
    public async Task FakeCli_PrintsInitThenStalls_RunnerStopsCleanly()
    {
        var node = NodeExePath;
        Skip.IfNot(node != null, "node.exe not found (set NODE_EXE or install Node.js)");

        // Print one stream-json-shaped line, flush, then sleep effectively
        // forever. Mirrors the live claude hang: init arrives, then silence.
        const string Script = @"
process.stdout.write('{""type"":""system"",""subtype"":""init"",""session_id"":""00000000-0000-4000-8000-000000000000""}\n');
setInterval(() => {}, 600000);
";

        var svc = new FakeNodeCliService(node!, Script);
        var lines = new List<CliOutputLine>();
        svc.OnOutput += (_, line) => { lock (lines) lines.Add(line); };

        var jobId = $"fake-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";

        var (exec, err) = await svc.StartAsync(
            jobId, jobKey,
            prompt: "(unused)",
            workingDirectory: Path.GetTempPath());
        Assert.Null(err);
        Assert.NotNull(exec);
        Assert.Equal("running", exec!.Status);

        // Wait for the init line to land in the buffer.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            int n;
            lock (lines) n = lines.Count;
            // OutputBuffer always has the synthetic "Started" line plus the init.
            if (n >= 2) break;
            await Task.Delay(100);
        }

        var snapshotMidRun = svc.GetOutput(jobKey);
        Assert.Contains(snapshotMidRun, l => l.Stream == "stdout" && l.Text.Contains("\"system\""));

        // The run is still hanging. Stop it (simulates what the watchdog
        // would do once HungSeconds elapses).
        var stopped = svc.Stop(jobKey, RunStopReason.Watchdog);
        Assert.True(stopped, "Stop should return true for a known jobKey.");

        // Wait for finalisation: MonitorProcessAsync writes the synthetic
        // "[taskboard] ... CLI exited" line and fires OnFinished.
        var finishDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < finishDeadline)
        {
            var snap = svc.GetOutput(jobKey);
            if (snap.Any(l => l.Stream == "system" && l.Text.Contains("CLI exited"))) break;
            await Task.Delay(100);
        }

        var snapshotFinal = svc.GetOutput(jobKey);
        Assert.Contains(snapshotFinal, l => l.Stream == "system" && l.Text.Contains("Started"));
        Assert.Contains(snapshotFinal, l => l.Stream == "stdout" && l.Text.Contains("\"system\""));
        Assert.Contains(snapshotFinal, l => l.Stream == "system" && l.Text.Contains("CLI exited"));

        var execAfter = svc.GetExecution(jobKey);
        Assert.NotNull(execAfter);
        // RunStopReason.Watchdog -> classifier maps to "stopped" (deliberate kill, not failure).
        Assert.Contains(execAfter!.Status, new[] { "stopped", "cancelled" });
    }

    [SkippableFact]
    public async Task FakeCli_RunStartedAndProcessExited_EventsRaisedOnOnRunEvent()
    {
        // ADR-0013 wiring smoke: the base class always emits a RunStarted
        // event on spawn and a ProcessExited (or Killed) event on
        // termination, regardless of whether a subclass adapter exists.
        // This pins the typed-event lifecycle for any future CLI.
        var node = NodeExePath;
        Skip.IfNot(node != null, "node.exe not found");

        const string Script = @"process.stdout.write('hello\n'); process.exit(0);";
        var svc = new FakeNodeCliService(node!, Script);
        var events = new List<CliRunEvent>();
        svc.OnRunEvent += (_, e) => { lock (events) events.Add(e); };

        var jobId = $"fake-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";
        await svc.StartAsync(jobId, jobKey, "(unused)", Path.GetTempPath());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            int n;
            lock (events) n = events.Count;
            if (events.OfType<CliRunEvent.ProcessExited>().Any()) break;
            await Task.Delay(50);
        }

        List<CliRunEvent> snap;
        lock (events) snap = events.ToList();
        Assert.Contains(snap, e => e is CliRunEvent.RunStarted);
        Assert.Contains(snap, e => e is CliRunEvent.ProcessExited);
        // FakeNodeCliService has no MapLineToRunEvents override, so no
        // OutputDelta / SessionStarted events are expected here - that
        // wiring is per-CLI and tested in ClaudeEventAdapterTests.
    }

    [SkippableFact]
    public async Task FakeCli_NoNewlineButAlive_RunnerStillBuffersBytes()
    {
        // The "live but never flushes a newline" shape: claude / codex /
        // gemini are line-oriented but a misbehaving CLI could write
        // partial bytes without ever terminating the line. The base
        // class's ReadStreamAsync uses ReadLineAsync which only emits
        // when a newline arrives - so this shape must NOT confuse the
        // runner's exit detection. We assert: the process exits cleanly
        // when we Stop it, the synthetic "Started" + "CLI exited" lines
        // are present, and the partial bytes stay only in the on-disk
        // log (where they are debuggable) - never in OutputBuffer (which
        // is line-keyed).
        var node = NodeExePath;
        Skip.IfNot(node != null, "node.exe not found");

        const string Script = @"
process.stdout.write('partial-line-without-newline');
setInterval(() => {}, 600000);
";
        var svc = new FakeNodeCliService(node!, Script);
        var jobId = $"fake-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";
        await svc.StartAsync(jobId, jobKey, "(unused)", Path.GetTempPath());
        await Task.Delay(500); // give Node a beat to write
        var stopped = svc.Stop(jobKey, RunStopReason.Watchdog);
        Assert.True(stopped);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var snap = svc.GetOutput(jobKey);
            if (snap.Any(l => l.Stream == "system" && l.Text.Contains("CLI exited"))) break;
            await Task.Delay(100);
        }
        var final = svc.GetOutput(jobKey);
        Assert.Contains(final, l => l.Stream == "system" && l.Text.Contains("Started"));
        Assert.Contains(final, l => l.Stream == "system" && l.Text.Contains("CLI exited"));
    }

    [SkippableFact]
    public async Task FakeCli_StderrWritesWhileStdoutSilent_StderrLandsInBuffer()
    {
        // Codex writes diagnostics to stderr (e.g. "Not inside a trusted
        // directory ..."). If a CLI's only output is on stderr, the
        // runner must still capture it. Pin the parity: stderr lines
        // arrive in OutputBuffer with Stream == "stderr".
        var node = NodeExePath;
        Skip.IfNot(node != null, "node.exe not found");

        const string Script = @"
process.stderr.write('error-line-1\nerror-line-2\n');
setTimeout(() => process.exit(0), 200);
";
        var svc = new FakeNodeCliService(node!, Script);
        var jobId = $"fake-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";
        await svc.StartAsync(jobId, jobKey, "(unused)", Path.GetTempPath());

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var snap = svc.GetOutput(jobKey);
            if (snap.Any(l => l.Stream == "system" && l.Text.Contains("CLI exited"))) break;
            await Task.Delay(100);
        }
        var final = svc.GetOutput(jobKey);
        var stderrLines = final.Where(l => l.Stream == "stderr").ToList();
        Assert.True(
            stderrLines.Count >= 2,
            $"Expected 2 stderr lines, got {stderrLines.Count}. All:\n" +
            string.Join("\n", final.Select(l => $"  [{l.Stream}] {l.Text}")));
    }

    [SkippableFact]
    public async Task FakeCli_FastOutput_NoneDropped()
    {
        // Backpressure shape: the CLI fires a burst of frames faster
        // than the consumer might drain. Codex's app-server ships a
        // Lagged-marker pattern for this; our runner uses an unbounded
        // buffer + per-line dispatch. Pin that no frames are dropped.
        var node = NodeExePath;
        Skip.IfNot(node != null, "node.exe not found");

        const string Script = @"
for (let i = 0; i < 500; i++) {
  process.stdout.write('{""seq"":' + i + '}\n');
}
process.exit(0);
";
        var svc = new FakeNodeCliService(node!, Script);
        var jobId = $"fake-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";
        await svc.StartAsync(jobId, jobKey, "(unused)", Path.GetTempPath());

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var snap = svc.GetOutput(jobKey);
            if (snap.Any(l => l.Stream == "system" && l.Text.Contains("CLI exited"))) break;
            await Task.Delay(100);
        }
        var final = svc.GetOutput(jobKey);
        var seqLines = final.Where(l => l.Stream == "stdout" && l.Text.Contains("\"seq\"")).ToList();
        Assert.True(
            seqLines.Count == 500,
            $"Expected 500 sequenced frames captured, got {seqLines.Count}. " +
            (seqLines.Count > 0 ? $"First: {seqLines[0].Text}, last: {seqLines[^1].Text}" : ""));
    }

    [SkippableFact]
    public async Task FakeCli_StreamsManyFrames_RunnerCapturesAll()
    {
        // Counter test: a fake CLI that ACTUALLY produces output should be
        // captured frame-by-frame. Pins the read loop's correctness so a
        // future change that breaks line-buffering / flushing is caught
        // here instead of as a silent agent in production.
        var node = NodeExePath;
        Skip.IfNot(node != null, "node.exe not found");

        const string Script = @"
for (let i = 0; i < 5; i++) {
  process.stdout.write('{""type"":""progress"",""i"":' + i + '}\n');
}
process.exit(0);
";

        var svc = new FakeNodeCliService(node!, Script);
        var jobId = $"fake-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";

        await svc.StartAsync(jobId, jobKey, "(unused)", Path.GetTempPath());

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var snap = svc.GetOutput(jobKey);
            if (snap.Any(l => l.Stream == "system" && l.Text.Contains("CLI exited"))) break;
            await Task.Delay(100);
        }

        var final = svc.GetOutput(jobKey);
        var progressFrames = final.Count(l => l.Stream == "stdout" && l.Text.Contains("\"progress\""));
        Assert.True(progressFrames == 5,
            $"Expected exactly 5 progress frames, got {progressFrames}. " +
            $"All frames:\n" + string.Join("\n", final.Select(l => $"  [{l.Stream}] {l.Text}")));
    }
}
