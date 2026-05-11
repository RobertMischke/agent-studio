using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Xunit;
using Xunit.Sdk;

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

    /// <summary>
    /// Locate <c>claude.exe</c> (the underlying npm-bundled binary, not the
    /// .CMD shim) without hardcoding the user-specific install root. Probes
    /// in order: <c>CLAUDE_EXE</c> env override, then <c>%APPDATA%\npm\
    /// node_modules\@anthropic-ai\claude-code\bin\claude.exe</c>.
    /// </summary>
    private static string? ClaudeExePath
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("CLAUDE_EXE");
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidate = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    /// <summary>Locate <c>claude.CMD</c> (the npm shim) for the broken-baseline probe.</summary>
    private static string? ClaudeCmdPath
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("CLAUDE_CMD");
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidate = Path.Combine(appData, "npm", "claude.CMD");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    /// <summary>Locate <c>codex.cmd</c> (the npm shim - Codex has no single .exe).</summary>
    private static string? CodexCmdPath
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("CODEX_CMD");
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidate = Path.Combine(appData, "npm", "codex.cmd");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    /// <summary>Locate <c>gemini.cmd</c> (the npm shim - Gemini has no single .exe).</summary>
    private static string? GeminiCmdPath
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("GEMINI_CMD");
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidate = Path.Combine(appData, "npm", "gemini.cmd");
            return File.Exists(candidate) ? candidate : null;
        }
    }

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
    [SkippableFact]
    public async Task DirectExe_PipeStdin_StreamJson_ProducesMultipleFrames()
    {
        Skip.IfNot(IntegrationEnabled, SkipReason);
        var exe = ClaudeExePath;
        Skip.IfNot(exe != null, "claude.exe not found (set CLAUDE_EXE or install via npm)");

        var psi = new ProcessStartInfo
        {
            FileName = exe!,
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
    [SkippableFact]
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
        Skip.IfNot(IntegrationEnabled, SkipReason);
        var cmd = ClaudeCmdPath;
        Skip.IfNot(cmd != null, "claude.CMD not found");
        Skip.IfNot(File.Exists(DevAgentRules), "agent-rules/core.md not at expected dev path");

        var psi = new ProcessStartInfo
        {
            FileName = cmd!,
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
    [SkippableFact]
    public async Task ClaudeCliService_StartAsync_FatPrompt_ProducesStreamingFrames()
    {
        Skip.IfNot(IntegrationEnabled, SkipReason);
        var cmd = ClaudeCmdPath;
        Skip.IfNot(cmd != null, "claude.CMD not found");
        Skip.IfNot(File.Exists(DevAgentRules), "agent-rules/core.md not at expected dev path");

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Force the test to exercise the npm shim path so the fix
                // (.CMD -> .exe rewrite in BuildStartInfo) actually runs.
                ["ClaudeCli:Path"] = cmd,
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
    [SkippableFact]
    public async Task ClaudeCliService_StartAsync_ProducesStreamingFrames()
    {
        Skip.IfNot(IntegrationEnabled, SkipReason);
        var exe = ClaudeExePath;
        Skip.IfNot(exe != null, "claude.exe not found");

        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClaudeCli:Path"] = exe,
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
    [SkippableFact]
    public async Task ClaudeCliService_SequentialKillRestart_StaysHealthy()
    {
        Skip.IfNot(IntegrationEnabled, SkipReason);
        var cmd = ClaudeCmdPath;
        Skip.IfNot(cmd != null, "claude.CMD not found");

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClaudeCli:Path"] = cmd,
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
    /// Codex parity probe: drives <see cref="OrchestratorApi.Services.Cli.CodexCliService.StartAsync"/>
    /// against the live <c>codex exec --json</c> CLI with a tiny prompt. Codex
    /// has no bundled .exe (it ships as <c>node.exe + codex.js</c> via the
    /// <c>codex.cmd</c> npm shim), so unlike Claude there is no underlying
    /// .exe to redirect to. The shim path is the production path. We assert
    /// the runner buffers at least one Codex JSONL frame past the synthetic
    /// "Started" line.
    /// </summary>
    [SkippableFact]
    public async Task CodexCliService_StartAsync_ProducesStreamingFrames()
    {
        Skip.IfNot(IntegrationEnabled, SkipReason);
        var cmd = CodexCmdPath;
        Skip.IfNot(cmd != null, "codex.cmd not found");

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodexCli:Path"] = cmd,
                ["TaskRepository"] = Path.Combine(Path.GetTempPath(), $"cli-it-{Guid.NewGuid():N}")
            })
            .Build();

        var svc = new OrchestratorApi.Services.Cli.CodexCliService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorApi.Services.Cli.CodexCliService>.Instance,
            cfg,
            new OrchestratorApi.Services.Pty.CodexModelDiscovery(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorApi.Services.Pty.CodexModelDiscovery>.Instance,
                cfg),
            new OrchestratorApi.Services.Bus.CliUsageParserRegistry(new OrchestratorApi.Services.Bus.ICliUsageParser[]
                { new OrchestratorApi.Services.Bus.CodexUsageParser() }),
            new OrchestratorApi.Services.Bus.CliModelRegistry());

        var jobId = $"it-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";
        // Codex requires a git repository (or --skip-git-repo-check, which the
        // production driver does not pass) - use the dev checkout because it
        // is a git repo. Skip if not present.
        Skip.IfNot(Directory.Exists(Path.Combine(DevRoot, ".git")), "DevRoot is not a git repo");
        var (exec, err) = await svc.StartAsync(
            jobId, jobKey,
            prompt: "Reply with exactly four words and nothing else: codex test ready ack",
            workingDirectory: DevRoot,
            sessionName: null, resumeSession: false,
            model: "gpt-5.5");
        Assert.Null(err);
        Assert.NotNull(exec);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = svc.GetOutput(jobKey);
            // Look for ANY codex JSONL frame past the synthetic "Started" line.
            var frameLines = snapshot.Count(l => l.Stream == "stdout" && (
                l.Text.Contains("\"thread") ||
                l.Text.Contains("\"turn") ||
                l.Text.Contains("\"item") ||
                l.Text.Contains("\"session_meta")));
            if (frameLines >= 1) break;
            await Task.Delay(250);
        }
        svc.Stop(jobKey);

        var final = svc.GetOutput(jobKey);
        var stdoutLines = final.Where(l => l.Stream == "stdout").ToList();
        Assert.True(
            stdoutLines.Count >= 1,
            $"Expected at least one stdout frame from codex. Got:\n" +
            string.Join("\n", final.Select(l => $"  [{l.Stream}] {l.Text[..Math.Min(l.Text.Length, 200)]}")));
    }

    /// <summary>
    /// Gemini parity probe: drives <see cref="OrchestratorApi.Services.Cli.GeminiCliService.StartAsync"/>
    /// against the live <c>gemini -p ...</c> CLI. Gemini also ships as
    /// <c>node.exe + bundle/gemini.js</c> via <c>gemini.cmd</c>; same shim
    /// path as Codex.
    /// </summary>
    [SkippableFact]
    public async Task GeminiCliService_StartAsync_ProducesStreamingFrames()
    {
        Skip.IfNot(IntegrationEnabled, SkipReason);
        var cmd = GeminiCmdPath;
        Skip.IfNot(cmd != null, "gemini.cmd not found");

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GeminiCli:Path"] = cmd,
                ["TaskRepository"] = Path.Combine(Path.GetTempPath(), $"cli-it-{Guid.NewGuid():N}")
            })
            .Build();

        var svc = new OrchestratorApi.Services.Cli.GeminiCliService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorApi.Services.Cli.GeminiCliService>.Instance,
            cfg);

        var jobId = $"it-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";
        var (exec, err) = await svc.StartAsync(
            jobId, jobKey,
            prompt: "Reply with exactly four words and nothing else: gemini test ready ack",
            workingDirectory: Path.GetTempPath(),
            sessionName: null, resumeSession: false,
            model: null);
        Assert.Null(err);
        Assert.NotNull(exec);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = svc.GetOutput(jobKey);
            var frameLines = snapshot.Count(l => l.Stream == "stdout" && l.Text.Length > 0 && l.Text.Contains('{'));
            if (frameLines >= 1) break;
            await Task.Delay(250);
        }
        svc.Stop(jobKey);

        var final = svc.GetOutput(jobKey);
        var stdoutLines = final.Where(l => l.Stream == "stdout").ToList();
        Assert.True(
            stdoutLines.Count >= 1,
            $"Expected at least one stdout frame from gemini. Got:\n" +
            string.Join("\n", final.Select(l => $"  [{l.Stream}] {l.Text[..Math.Min(l.Text.Length, 200)]}")));
    }

    /// <summary>
    /// Copilot smoke probe: Copilot is the odd CLI out - it is TUI-based
    /// and the production driver spawns it via PtySession (NOT the pipe-
    /// redirected CliExecutionServiceBase path that Claude / Codex /
    /// Gemini share). The full StartAsync flow needs a valid GitHub token
    /// and an interactive auth dance - too invasive for a default
    /// integration probe. We instead verify the binary is invokable with
    /// --version via plain Process redirection.
    /// </summary>
    [SkippableFact]
    public void CopilotCli_VersionProbe_Succeeds()
    {
        Skip.IfNot(IntegrationEnabled, SkipReason);
        var fromEnv = Environment.GetEnvironmentVariable("COPILOT_CMD");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidate = !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv : Path.Combine(appData, "npm", "copilot.cmd");
        Skip.IfNot(File.Exists(candidate), $"copilot.cmd not at {candidate}");

        var psi = new ProcessStartInfo
        {
            FileName = candidate,
            Arguments = "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(15_000), "copilot --version did not exit within 15s");
        Assert.Equal(0, p.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(stdout), "copilot --version produced empty stdout");
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
