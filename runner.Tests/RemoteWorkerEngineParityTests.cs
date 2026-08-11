using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRunner;
using AgentStudio.TestSupport;
using CodingAgentRunner.Model;
using Xunit;
using CarRunOutcome = CodingAgentRunner.Model.RunOutcome;

namespace AgentRunner.Tests;

/// <summary>
/// T3 (AGT-2372): black-box parity at the detached-worker boundary. Both engines
/// launch the same executable and replay the same recorded protocol bytes. This
/// complements the injected-spawner CAR tests by proving the production worker
/// branch, durable files, timeout kill, and restart/reattach contract together.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class RemoteWorkerEngineParityTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "remote-worker-engine-parity", Guid.NewGuid().ToString("N"));

    // Linux-only: the production launch must execute a Unix-mode fixture wrapper,
    // and the no-orphan proof observes the exact PID through /proc.
    [SkippableTheory]
    [Trait("Category", "MachineBound")]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    [InlineData("happy", "p1-happy-done.claude.fixture", 0, false, CarRunOutcome.Completed)]
    [InlineData("self-crash", "p9-self-crash.claude.fixture", 1, false, CarRunOutcome.Failed)]
    [InlineData("timeout", "p1-happy-done.claude.fixture", 124, true, CarRunOutcome.Stopped)]
    public async Task Legacy_and_car_workers_have_protocol_terminal_result_and_kill_parity(
        string scenario,
        string fixtureName,
        int expectedExitCode,
        bool expectedTimeout,
        CarRunOutcome expectedOutcome)
    {
        PlatformGate.LinuxOnly("the executable fixture wrapper uses Unix file modes and PID liveness reads /proc");
        RequireNode();

        var scenarioRoot = Path.Combine(_root, scenario);
        var worktree = Path.Combine(scenarioRoot, "worktree");
        Directory.CreateDirectory(worktree);
        var wrapper = WriteFixtureWrapper(
            scenarioRoot,
            FixturePath(fixtureName),
            perLineDelayMs: expectedTimeout ? 30_000 : 0);
        var runId = $"remote-worker-parity-{scenario}";

        var legacy = await RunWorkerAsync(
            scenarioRoot, worktree, wrapper, RunnerOptions.ExecEngineLegacy,
            runId, timeoutSeconds: expectedTimeout ? 1 : 20);
        var car = await RunWorkerAsync(
            scenarioRoot, worktree, wrapper, RunnerOptions.ExecEngineCar,
            runId, timeoutSeconds: expectedTimeout ? 1 : 20);

        Assert.Equal(expectedExitCode, legacy.Result.ExitCode);
        Assert.Equal(expectedTimeout, legacy.Result.TimedOut);
        Assert.Equal(legacy.Result.ExitCode, car.Result.ExitCode);
        Assert.Equal(legacy.Result.TimedOut, car.Result.TimedOut);
        Assert.Equal(Normalize(legacy.Result.StdOut), Normalize(car.Result.StdOut));
        Assert.Equal(Normalize(legacy.Result.StdErr), Normalize(car.Result.StdErr));
        Assert.NotEqual(default, legacy.Result.CompletedAtUtc);
        Assert.NotEqual(default, car.Result.CompletedAtUtc);

        var legacyProtocol = ReadProtocolEvents(legacy.WorkerDirectory);
        var carProtocol = ReadProtocolEvents(car.WorkerDirectory);
        if (expectedTimeout) Assert.Empty(legacyProtocol);
        else Assert.NotEmpty(legacyProtocol);
        Assert.Equal(legacyProtocol, carProtocol);

        var legacyTerminal = TerminalFromLegacyResult(legacy.Result);
        var carTerminal = ReadCarTerminal(car.WorkerDirectory);
        Assert.Equal(expectedOutcome, legacyTerminal.Outcome);
        Assert.Equal(legacyTerminal, carTerminal);

        await AssertCliProcessGoneAsync(legacy.ResultsDirectory);
        await AssertCliProcessGoneAsync(car.ResultsDirectory);
    }

    // Linux-only: the deterministic handshake uses an executable shell wrapper,
    // while VerifyLive proves the reattached worker's cwd through /proc/<pid>/cwd.
    [SkippableTheory]
    [Trait("Category", "MachineBound")]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    [InlineData(RunnerOptions.ExecEngineLegacy)]
    [InlineData(RunnerOptions.ExecEngineCar)]
    public async Task Replacement_daemon_reattaches_each_engine_without_output_or_result_duplication(
        string engine)
    {
        PlatformGate.LinuxOnly("the fixture handshake uses a Unix executable and reattach verifies /proc/<pid>/cwd");
        RequireNode();

        var caseRoot = Path.Combine(_root, $"reattach-{engine}");
        var worktree = Path.Combine(caseRoot, "worktree");
        var results = Path.Combine(caseRoot, "results");
        var stateRoot = Path.Combine(caseRoot, "state");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(results);
        var wrapper = WriteFixtureWrapper(
            caseRoot,
            FixturePath("p1-happy-done.claude.fixture"),
            perLineDelayMs: 0,
            waitForRelease: true);
        var options = Options(stateRoot, worktree, wrapper, engine);
        var lease = Lease($"AGT-2372-{engine}");
        var firstStore = new RunnerStateStore(stateRoot);
        var slot = firstStore.Create(
            lease.TaskKey,
            lease,
            worktree,
            runId: $"remote-worker-reattach-{engine}",
            runSpec: new RunSpecDto(
                CliType: AgentCliProcess.ClaudeCli,
                PermissionMode: "read-only",
                ContextMode: "shared"));

        DurableAgentProcess? original = null;
        try
        {
            original = DurableAgentProcess.Start(
                options,
                slot.WorkerDirectory,
                worktree,
                "complete the deterministic fixture",
                results,
                runSpec: slot.RunSpec,
                runId: slot.RunId);
            firstStore.Save(slot with
            {
                ProcessId = original.ProcessId,
                ProcessStartedAtUtc = original.ProcessStartedAtUtc,
                Phase = "running",
            });

            await WaitForFileAsync(Path.Combine(results, "fixture-paused"));
            var beforeReplacement = await WaitForOutputAsync(original, afterSequence: 0);
            Assert.Null(original.ReadResult());

            // Reconstruct both the state store and process handle. The replacement
            // owns no Process object or inherited stream from the launching daemon.
            var replacementStore = new RunnerStateStore(stateRoot);
            var recovered = Assert.Single(replacementStore.LoadAll());
            Assert.True(DurableAgentProcess.VerifyLive(recovered, out var proof), proof);
            var attached = DurableAgentProcess.Attach(recovered);
            var lastSequenceBeforeReplacement = beforeReplacement.Max(line => line.Sequence);

            await File.WriteAllTextAsync(Path.Combine(results, "fixture-release"), "continue");
            var result = await WaitForResultAsync(attached);
            var afterReplacement = attached.ReadAfter(lastSequenceBeforeReplacement);
            var completeOutput = attached.ReadAfter(0);

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.Contains("[[TASK_DONE]]", result.StdOut);
            Assert.NotEmpty(afterReplacement);
            Assert.Equal(lastSequenceBeforeReplacement + 1, afterReplacement[0].Sequence);
            Assert.Contains(afterReplacement, line => line.Text.Contains("[[TASK_DONE]]", StringComparison.Ordinal));
            Assert.Equal(
                Enumerable.Range(1, completeOutput.Count).Select(value => (long)value),
                completeOutput.Select(line => line.Sequence));

            Assert.Single(Directory.EnumerateFiles(slot.WorkerDirectory, "result.json"));
            Assert.Empty(Directory.EnumerateFiles(slot.WorkerDirectory, "result.json.*.tmp"));
            Assert.Equal(result, attached.ReadResult());
        }
        finally
        {
            if (original is not null && original.ReadResult() is null)
                original.Kill();
        }
    }

    private async Task<WorkerRun> RunWorkerAsync(
        string scenarioRoot,
        string worktree,
        string wrapper,
        string engine,
        string runId,
        int timeoutSeconds)
    {
        var workerDirectory = Path.Combine(scenarioRoot, $"worker-{engine}");
        var resultsDirectory = Path.Combine(scenarioRoot, $"results-{engine}");
        Directory.CreateDirectory(workerDirectory);
        Directory.CreateDirectory(resultsDirectory);
        var spec = new DetachedJobSpec(
            FileName: wrapper,
            Arguments: [],
            WorkingDirectory: worktree,
            Prompt: "run the recorded remote parity fixture",
            ResultsDirectory: resultsDirectory,
            TimeoutSeconds: timeoutSeconds,
            CliType: AgentCliProcess.ClaudeCli,
            PermissionMode: "read-only",
            ContextMode: "shared",
            Engine: engine,
            RunId: runId);
        var specPath = Path.Combine(workerDirectory, "spec.json");
        await File.WriteAllTextAsync(specPath, JsonSerializer.Serialize(spec, Json));

        var returnedExitCode = await DurableAgentProcess.RunWorkerAsync(specPath);
        var result = JsonSerializer.Deserialize<DetachedJobResult>(
                         await File.ReadAllTextAsync(Path.Combine(workerDirectory, "result.json")),
                         Json)
                     ?? throw new InvalidDataException("detached worker wrote an empty result");
        Assert.Equal(result.ExitCode, returnedExitCode);
        return new WorkerRun(workerDirectory, resultsDirectory, result);
    }

    private static TerminalSemantics TerminalFromLegacyResult(DetachedJobResult result)
    {
        var reason = result.TimedOut ? RunStopReason.Watchdog : RunStopReason.None;
        return new TerminalSemantics(RunStatusClassifier.Classify(result.ExitCode, reason), reason);
    }

    private static TerminalSemantics ReadCarTerminal(string workerDirectory)
    {
        using var events = new EventDocuments(ReadEvents(workerDirectory));
        var terminal = events.Documents
            .Where(document => document.RootElement.GetProperty("type").GetString() == "RunEnded")
            .Single();
        var payload = terminal.RootElement.GetProperty("event");
        var outcomeElement = payload.GetProperty("outcome");
        var outcome = outcomeElement.ValueKind == JsonValueKind.Number
            ? (CarRunOutcome)outcomeElement.GetInt32()
            : Enum.Parse<CarRunOutcome>(outcomeElement.GetString()!, ignoreCase: true);
        var reasonText = payload.GetProperty("reason").GetString();
        var reason = Enum.TryParse<RunStopReason>(reasonText, ignoreCase: true, out var parsed)
            ? parsed
            : RunStopReason.None;
        return new TerminalSemantics(outcome, reason);
    }

    private static IReadOnlyList<ProtocolEvent> ReadProtocolEvents(string workerDirectory)
    {
        using var events = new EventDocuments(ReadEvents(workerDirectory));
        return events.Documents
            .Select(document => new ProtocolEvent(
                document.RootElement.GetProperty("type").GetString()!,
                CanonicalEventPayload(document.RootElement.GetProperty("event"))))
            .Where(evt => evt.Type is not ("RunStarted" or "RunEnded"))
            .ToList();
    }

    private static string CanonicalEventPayload(JsonElement payload)
    {
        var canonical = JsonNode.Parse(payload.GetRawText())?.AsObject()
                        ?? throw new InvalidDataException("CAR event payload is not a JSON object");
        canonical.Remove("observedAt");
        return canonical.ToJsonString(Json);
    }

    private static List<JsonDocument> ReadEvents(string workerDirectory)
        => File.ReadLines(Path.Combine(workerDirectory, "events.jsonl"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line))
            .ToList();

    private static string WriteFixtureWrapper(
        string directory,
        string fixturePath,
        int perLineDelayMs,
        bool waitForRelease = false)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fake-claude");
        var handshake = waitForRelease
            ? "printf '%s\\n' '{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"reattach-handshake\"}'\n" +
              "touch \"$JOB_RESULTS_DIR/fixture-paused\"\n" +
              "while [ ! -f \"$JOB_RESULTS_DIR/fixture-release\" ]; do sleep 0.02; done\n"
            : string.Empty;
        var script =
            "#!/bin/sh\n" +
            "if [ \"${1:-}\" = \"--version\" ]; then printf '%s\\n' 'fake-claude 1.0'; exit 0; fi\n" +
            "printf '%s\\n' \"$$\" > \"$JOB_RESULTS_DIR/fake-cli.pid\"\n" +
            handshake +
            $"export FAKE_CLI_FIXTURE={ShellQuote(fixturePath)}\n" +
            $"export FAKE_CLI_DELAY_MS={perLineDelayMs}\n" +
            $"exec node {ShellQuote(FakeCliPath())} \"$@\"\n";
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    private static RunnerOptions Options(string stateRoot, string worktree, string wrapper, string engine) => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "remote-worker-parity",
        RunnerName = "remote-worker-parity",
        Hostname = "test-host",
        BackendName = "test",
        WorkDir = worktree,
        StateDir = stateRoot,
        BaseBranch = "main",
        ExecEngine = engine,
        CliBin = wrapper,
        CliArgs = string.Empty,
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        RunTimeoutSeconds = 20,
        HostMaxParallelism = 1,
        PollSeconds = 1,
    };

    private static RunLeaseInfoDto Lease(string taskKey) => new(
        taskKey,
        "remote-worker-parity",
        "remote-worker-parity",
        "test-host",
        Environment.ProcessId,
        "test",
        $"lease-{taskKey}",
        1,
        DateTime.UtcNow,
        DateTime.UtcNow.AddMinutes(2));

    private static async Task<IReadOnlyList<DetachedJobLogLine>> WaitForOutputAsync(
        DurableAgentProcess process,
        long afterSequence)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var output = process.ReadAfter(afterSequence);
            if (output.Count > 0) return output;
            await Task.Delay(20);
        }

        throw new TimeoutException("detached worker did not persist the handshake output");
    }

    private static async Task<DetachedJobResult> WaitForResultAsync(DurableAgentProcess process)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var result = process.ReadResult();
            if (result is not null) return result;
            await Task.Delay(20);
        }

        throw new TimeoutException("detached worker did not persist its terminal result");
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 200 && !File.Exists(path); attempt++)
            await Task.Delay(20);
        Assert.True(File.Exists(path), $"fixture did not create handshake file: {path}");
    }

    private static async Task AssertCliProcessGoneAsync(string resultsDirectory)
    {
        var pidPath = Path.Combine(resultsDirectory, "fake-cli.pid");
        Assert.True(File.Exists(pidPath), $"fake CLI did not record its PID: {pidPath}");
        var pid = int.Parse((await File.ReadAllTextAsync(pidPath)).Trim());
        for (var attempt = 0; attempt < 200 && Directory.Exists($"/proc/{pid}"); attempt++)
            await Task.Delay(20);
        Assert.False(Directory.Exists($"/proc/{pid}"), $"fake CLI PID {pid} survived its worker result");
    }

    private static void RequireNode()
        => Skip.IfNot(NodeAvailable(), "Node.js is required to replay the recorded CLI fixtures.");

    private static bool NodeAvailable()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (probe is null || !probe.WaitForExit(8_000)) return false;
            return probe.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string FixturePath(string name)
        => CliCaptureFixtureLocator.Resolve(RepoRoot(), name);

    private static string FakeCliPath()
        => Path.Combine(RepoRoot(), "testdata", "cli-fixtures", "fake-cli.mjs");

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory })
        {
            var current = start;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
                current = Path.GetDirectoryName(current);
            }
        }

        throw new InvalidOperationException("agent-taskboard.sln not found above the remote parity test.");
    }

    public void Dispose() => ResilientDirectory.TryDelete(_root);

    private sealed record WorkerRun(
        string WorkerDirectory,
        string ResultsDirectory,
        DetachedJobResult Result);

    private sealed record TerminalSemantics(CarRunOutcome Outcome, RunStopReason Reason);

    private sealed record ProtocolEvent(string Type, string Payload);

    private sealed class EventDocuments(List<JsonDocument> documents) : IDisposable
    {
        public IReadOnlyList<JsonDocument> Documents { get; } = documents;

        public void Dispose()
        {
            foreach (var document in Documents) document.Dispose();
        }
    }
}
