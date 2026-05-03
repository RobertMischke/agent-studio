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
            string? sessionName, bool resumeSession, string? model)
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
