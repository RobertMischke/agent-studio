using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Integration regression tests for the CLI-spawn boundary.
///
/// <para>
/// <b>Why this file exists.</b> The runner-spawned <c>claude.exe</c> kept
/// hanging after its first <c>system/init</c> frame: stream-json output
/// would emit the init line, then nothing for ~120s until the watchdog
/// killed the process. The shell's <c>echo prompt | claude -p ...</c> works
/// fine for the same args, so the bug lives in <i>how</i> the .NET runner
/// spawns the child. This test file pins each viable spawn shape so we can
/// (1) reproduce the hang on the broken path and (2) prove a chosen fix
/// produces a stream of frames, end-to-end, against the live CLI.
/// </para>
/// <para>
/// <b>Cost.</b> These tests spawn the real CLI binary and cost real quota.
/// They are skipped unless the <c>RUN_CLI_INTEGRATION</c> environment
/// variable is set, so the default <c>dotnet test</c> run stays free and
/// fast. CI sets the variable on a nightly job; locally an agent runs them
/// when investigating spawn issues. Prompts are kept tiny ("say hi") and
/// targeted at the cheapest model (Haiku) to keep the cost minimal.
/// </para>
/// <para>
/// <b>Per-CLI matrix (planned).</b> Claude is implemented first because it
/// surfaced the hang. Codex / Copilot / Gemini get parallel test cases as
/// the test infrastructure stabilises - the hang root cause (Node stdout
/// block-buffering on a redirected pipe) is shared by Claude/Codex/Gemini
/// (all Node-based) and the same fix should hold for all three.
/// </para>
/// </summary>
public class CliSpawnIntegrationTests
{
    /// <summary>
    /// Skip these tests unless the user opted in. The flag is intentionally
    /// noisy on local dev: setting it once costs measurable Anthropic quota.
    /// </summary>
    public static bool IntegrationEnabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RUN_CLI_INTEGRATION"));

    private const string SkipReason =
        "Integration test, opt-in via RUN_CLI_INTEGRATION=1 (spawns real CLI, burns quota).";

    private const string ClaudeExePath =
        @"C:\Users\rmisc\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe";

    private const string ClaudeCmdPath =
        @"C:\Users\rmisc\AppData\Roaming\npm\claude.CMD";

    private const string TinyPrompt = "Reply with exactly four words and nothing else: ready set go now";

    /// <summary>
    /// 8 KB-ish prompt to reproduce the "live runner" pressure: production
    /// task prompts run 5-10 KB and ship a structured spec, not a one-liner.
    /// The hang we hunted only manifested with this scale of payload.
    /// </summary>
    private static string FatPrompt() =>
        TinyPrompt + "\n\n" + new string('.', 8000) + "\n\nNow reply with the four words.";

    private static readonly string DevRoot =
        @"C:\Projects\agent-taskboard-devspace\agent-taskboard-dev";

    private static readonly string DevAgentRules =
        Path.Combine(DevRoot, "agent-rules", "core.md");

    /// <summary>
    /// Probe 1: Spawn <c>claude.exe</c> directly (no cmd.exe wrapping, no
    /// .CMD shim) with prompt piped through stdin and stdout redirected to
    /// a pipe. Read all stream-json frames until the process exits.
    /// <para>
    /// <b>What this test pins.</b> When the runner uses
    /// <see cref="System.Diagnostics.Process"/> + redirected stdin/stdout,
    /// can we read MORE than just the <c>system/init</c> frame? In
    /// production we observed only the init frame arriving before the
    /// watchdog killed the process. If this test PASSES, the previous
    /// failure was caused by something else (cmd.exe wrapping, .CMD shim
    /// path, or a race in our reader loop), and the fix is to drop the
    /// .CMD wrapper. If this test FAILS, the fix needs to involve a PTY
    /// or a different output format.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DirectExe_PipeStdin_StreamJson_ProducesMultipleFrames()
    {
        if (!IntegrationEnabled) return; // silent skip until opt-in
        if (!File.Exists(ClaudeExePath)) return;

        var psi = new ProcessStartInfo
        {
            FileName = ClaudeExePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath()
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add("claude-haiku-4-5");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--dangerously-skip-permissions");

        psi.Environment["NODE_NO_WARNINGS"] = "1";
        psi.Environment["LC_ALL"] = "C.UTF-8";
        psi.Environment["LANG"] = "C.UTF-8";

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        // Mimic the runner: write prompt to stdin, then close.
        await process.StandardInput.WriteAsync(TinyPrompt);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        var frames = new List<string>();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            var line = await ReadLineWithTimeoutAsync(process.StandardOutput, deadline - DateTime.UtcNow);
            if (line == null) break;
            frames.Add(line);
            // Bail early once we have decisively more than just the init frame.
            if (frames.Count >= 5) break;
        }

        // Drain anything still buffered.
        try
        {
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                var line = await ReadLineWithTimeoutAsync(process.StandardOutput, TimeSpan.FromSeconds(2));
                if (line == null) break;
                frames.Add(line);
            }
        }
        catch { /* ignore drain errors */ }

        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        // The init frame ALWAYS arrives. The whole point of this test is to
        // see whether the model's reply frames make it back through the pipe.
        Assert.NotEmpty(frames);
        Assert.Contains(frames, f => f.Contains("\"type\":\"system\"") && f.Contains("\"subtype\":\"init\""));
        Assert.True(frames.Count >= 2,
            $"Expected at least 2 stream-json frames (init + at least one model output). Got {frames.Count}:\n" +
            string.Join("\n", frames.Select((f, i) => $"  [{i}] {(f.Length > 200 ? f[..200] + "..." : f)}")));
    }

    /// <summary>
    /// Probe 2 (counter-test of probe 1): spawn the npm <c>claude.CMD</c>
    /// shim instead of the underlying <c>claude.exe</c>. .NET wraps the
    /// .CMD invocation in <c>cmd.exe /c "claude.CMD ..."</c>, and that
    /// wrapper interferes with the redirected-stdin pipe inheritance:
    /// claude reads its first <c>system/init</c> frame out, then never
    /// sees the prompt bytes that <see cref="StreamWriter"/> wrote, so it
    /// never produces a model frame and the pipe stays silent until we
    /// give up.
    /// <para>
    /// This test pins the <b>broken</b> shape: we expect
    /// <see cref="GetFrameSetWithinTimeoutAsync"/> to time out with at most
    /// the init frame visible. If a future Windows / .NET / npm change
    /// fixes the underlying inheritance, this test will start failing -
    /// and we will know the <see cref="ClaudeCliService.ResolveCmdShimToExe"/>
    /// workaround can be retired.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CmdShim_PipeStdin_StreamJson_LiveScaleFatPrompt_ShouldStreamPastInit()
    {
        // Reproduction of the live-runner conditions that surfaced the
        // hang. We do NOT assert the broken shape here (that approach was
        // wrong - .CMD on its own produces frames for tiny prompts). We
        // instead exercise the closest-to-prod call and report what we
        // observe so the next pass can refine the hypothesis. With a fat
        // 8 KB prompt, the system-prompt-file flag and the dev cwd, the
        // hang reliably appeared in the live backend; if this test
        // reproduces only the init frame we have the regression captured;
        // if it streams normally the live hang has a different root cause
        // (concurrent claude processes, watchdog timing, etc.) and this
        // test still acts as a parity probe alongside the DirectExe path.
        if (!IntegrationEnabled) return;
        if (!File.Exists(ClaudeCmdPath)) return;
        if (!File.Exists(DevAgentRules)) return;

        var psi = new ProcessStartInfo
        {
            FileName = ClaudeCmdPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = DevRoot
        };
        psi.Arguments =
            "-p --model claude-haiku-4-5 --output-format stream-json --verbose " +
            "--dangerously-skip-permissions " +
            $"--append-system-prompt-file \"{DevAgentRules}\"";
        psi.Environment["NODE_NO_WARNINGS"] = "1";

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        var prompt = FatPrompt();
        await process.StandardInput.WriteAsync(prompt);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        var frames = await CollectFramesAsync(process, TimeSpan.FromSeconds(45), maxFrames: 6);
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }

        Assert.Contains(frames, f => f.Contains("\"type\":\"system\"") && f.Contains("\"subtype\":\"init\""));
        // Document the observed shape via the assertion message, regardless of pass/fail.
        Assert.True(frames.Count >= 2,
            $"CMD shim live-scale path: only {frames.Count} frame(s) within 45s. " +
            $"This is the hang we want the workaround to bypass.\n" +
            string.Join("\n", frames.Select((f, i) => $"  [{i}] {(f.Length > 200 ? f[..200] + "..." : f)}")));
    }

    /// <summary>
    /// Probe 4: production code path with a fat prompt to mirror live
    /// runner conditions. Pins that <see cref="ClaudeCliService.ResolveCmdShimToExe"/>
    /// continues to produce streaming frames at realistic prompt sizes.
    /// </summary>
    [Fact]
    public async Task ClaudeCliService_StartAsync_FatPrompt_ProducesStreamingFrames()
    {
        if (!IntegrationEnabled) return;
        if (!File.Exists(ClaudeExePath)) return;
        if (!File.Exists(DevAgentRules)) return;

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Force the test to exercise the npm shim path so the fix
                // (.CMD -> .exe rewrite in BuildStartInfo) actually runs.
                ["ClaudeCli:Path"] = ClaudeCmdPath,
                ["AgentRules:CorePath"] = DevAgentRules,
                ["TaskRepository"] = Path.Combine(Path.GetTempPath(), $"cli-it-{Guid.NewGuid():N}")
            })
            .Build();

        var svc = new OrchestratorApi.Services.Cli.ClaudeCliService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorApi.Services.Cli.ClaudeCliService>.Instance,
            cfg);

        var jobId = $"it-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";
        var (exec, err) = await svc.StartAsync(
            jobId, jobKey, FatPrompt(),
            workingDirectory: DevRoot,
            sessionName: null, resumeSession: false,
            model: "claude-haiku-4-5");
        Assert.Null(err);
        Assert.NotNull(exec);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = svc.GetOutput(jobKey);
            var modelLines = snapshot.Count(l => l.Stream == "stdout" && !l.Text.Contains("Session init"));
            if (modelLines >= 1) break;
            await Task.Delay(250);
        }
        svc.Stop(jobKey);

        var final = svc.GetOutput(jobKey);
        Assert.Contains(final, l => l.Stream == "stdout" && l.Text.Contains("Session init"));
        Assert.True(
            final.Count(l => l.Stream == "stdout") >= 2,
            $"Live-shape fat-prompt run produced too few stdout lines. Got:\n" +
            string.Join("\n", final.Select(l => $"  [{l.Stream}] {l.Text}")));
    }

    /// <summary>
    /// Probe 3: drive <see cref="ClaudeCliService"/> end-to-end (the
    /// production code path) with a tiny prompt. This is the test the
    /// actual fix needs to make green: the runner writes prompt to stdin,
    /// reads stream-json frames via the base class's
    /// <c>ReadStreamAsync</c> loop, and we assert the line buffer
    /// contains both the init marker and at least one model output frame.
    /// </summary>
    [Fact]
    public async Task ClaudeCliService_StartAsync_ProducesStreamingFrames()
    {
        if (!IntegrationEnabled) return;
        if (!File.Exists(ClaudeExePath)) return;

        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClaudeCli:Path"] = ClaudeExePath,
                ["TaskRepository"] = Path.Combine(Path.GetTempPath(), $"cli-it-{Guid.NewGuid():N}")
            })
            .Build();

        var svc = new OrchestratorApi.Services.Cli.ClaudeCliService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorApi.Services.Cli.ClaudeCliService>.Instance,
            cfg);

        var lines = new List<OrchestratorApi.Models.CliOutputLine>();
        svc.OnOutput += (_, line) => { lock (lines) lines.Add(line); };

        var jobId = $"it-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";
        var (exec, err) = await svc.StartAsync(
            jobId, jobKey, TinyPrompt,
            workingDirectory: Path.GetTempPath(),
            sessionName: null, resumeSession: false,
            model: "claude-haiku-4-5");
        Assert.Null(err);
        Assert.NotNull(exec);

        // Wait until we see at least one model-output line beyond init.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            int n;
            lock (lines) n = lines.Count;
            if (n >= 4) break; // started + init + at least one model frame + some
            await Task.Delay(250);
        }
        svc.Stop(jobKey);

        var snapshot = svc.GetOutput(jobKey);
        Assert.Contains(snapshot, l => l.Stream == "stdout" && l.Text.Contains("Session init"));
        Assert.True(
            snapshot.Count(l => l.Stream == "stdout") >= 2,
            $"Expected at least 2 stdout lines (init + a model frame). Got:\n" +
            string.Join("\n", snapshot.Select(l => $"  [{l.Stream}] {l.Text}")));
    }

    /// <summary>
    /// Probe 5: stress test. Three sequential <see cref="ClaudeCliService.StartAsync"/>
    /// calls in a row, each killed mid-run via <see cref="Stop"/>. The live
    /// runner produced this exact pattern when the watchdog cut the agent off
    /// after silence; subsequent runs went silent themselves. If accumulated
    /// state (claude session DB, .NET pipe handles, port reuse) is the
    /// culprit, this test reproduces it deterministically.
    /// </summary>
    [Fact]
    public async Task ClaudeCliService_SequentialKillRestart_StaysHealthy()
    {
        if (!IntegrationEnabled) return;
        if (!File.Exists(ClaudeExePath)) return;

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClaudeCli:Path"] = ClaudeCmdPath,
                ["AgentRules:CorePath"] = DevAgentRules,
                ["TaskRepository"] = Path.Combine(Path.GetTempPath(), $"cli-it-{Guid.NewGuid():N}")
            })
            .Build();
        var svc = new OrchestratorApi.Services.Cli.ClaudeCliService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorApi.Services.Cli.ClaudeCliService>.Instance,
            cfg);

        var streamingObserved = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var jobId = $"stress-{i}-{Guid.NewGuid():N}";
            var jobKey = $"::{jobId}";
            var (exec, err) = await svc.StartAsync(
                jobId, jobKey, FatPrompt(),
                workingDirectory: DevRoot,
                sessionName: null, resumeSession: false,
                model: "claude-haiku-4-5");
            Assert.Null(err);
            Assert.NotNull(exec);

            // Wait up to 30s for at least one model frame past init.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            int modelFrames = 0;
            while (DateTime.UtcNow < deadline)
            {
                modelFrames = svc.GetOutput(jobKey)
                    .Count(l => l.Stream == "stdout" && !l.Text.Contains("Session init"));
                if (modelFrames >= 1) break;
                await Task.Delay(250);
            }
            streamingObserved.Add(modelFrames);
            svc.Stop(jobKey);
            await Task.Delay(500); // give Stop's MonitorProcessAsync a beat
        }

        Assert.All(streamingObserved.Select((n, i) => (n, i)),
            t => Assert.True(t.n >= 1,
                $"Run #{t.i + 1} produced {t.n} model frames in 30s. " +
                $"Sequence: [{string.Join(", ", streamingObserved)}]. " +
                "If a later run starves while earlier runs streamed, the hang root cause is accumulated state."));
    }

    /// <summary>
    /// Read one line with a per-line timeout so a hung pipe does not stall
    /// the test forever. Returns null on timeout or stream end.
    /// </summary>
    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) return null;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await reader.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Drain stdout for at most <paramref name="overall"/> wall time, returning
    /// up to <paramref name="maxFrames"/> non-empty lines. Stops early when
    /// the process exits or the deadline elapses.
    /// </summary>
    private static async Task<List<string>> CollectFramesAsync(Process p, TimeSpan overall, int maxFrames)
    {
        var frames = new List<string>();
        var deadline = DateTime.UtcNow.Add(overall);
        while (DateTime.UtcNow < deadline && frames.Count < maxFrames)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            // Cap each line read to 5s so long silences don't pin the test.
            var lineTimeout = remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5);
            var line = await ReadLineWithTimeoutAsync(p.StandardOutput, lineTimeout);
            if (line == null)
            {
                if (p.HasExited) break;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(line)) frames.Add(line);
        }
        return frames;
    }
}
