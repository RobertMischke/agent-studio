using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using StudioCleanContextPreparer = AgentStudio.Cli.CleanContextPreparer;

namespace AgentStudio.Tests;

/// <summary>
/// Deterministic local execution parity proof for the temporary CAR/legacy
/// rollout boundary. Both observations execute the same fake Codex binary and
/// recorded protocol bytes through <see cref="GenericCliExecutionService"/>;
/// the only changed input is the selected execution engine.
/// </summary>
public sealed class BackendLocalExecutionEngineParityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "backend-local-engine-parity", Guid.NewGuid().ToString("N"));

    public static TheoryData<string, string, string?, string, string, string?> ExecutionScenarios() => new()
    {
        {
            "normal",
            "p1-happy-done.codex.fixture",
            null,
            RunStatuses.Completed,
            TerminalRunOutcomeKinds.Success,
            null
        },
        {
            "self-crash",
            "p9-self-crash.codex.fixture",
            null,
            RunStatuses.Failed,
            TerminalRunOutcomeKinds.Failed,
            "stream disconnected before completion"
        },
        {
            "user-stop",
            "p1-happy-done.codex.fixture",
            nameof(RunStopReason.UserStop),
            RunStatuses.Stopped,
            TerminalRunOutcomeKinds.Interrupted,
            nameof(RunStopReason.UserStop)
        },
        {
            "watchdog-stop",
            "p1-happy-done.codex.fixture",
            nameof(RunStopReason.Watchdog),
            RunStatuses.Stopped,
            TerminalRunOutcomeKinds.Interrupted,
            nameof(RunStopReason.Watchdog)
        },
    };

    [Trait("Category", "MachineBound")]
    [SkippableTheory]
    [MemberData(nameof(ExecutionScenarios))]
    public async Task Legacy_and_car_have_the_same_typed_lifecycle_and_terminal_result(
        string scenario,
        string fixtureName,
        string? stopReasonName,
        string expectedStatus,
        string expectedRunOutcome,
        string? expectedTerminalReason)
    {
        var nodePath = RequireLinuxAndNode();
        var stopReason = stopReasonName == null
            ? RunStopReason.None
            : Enum.Parse<RunStopReason>(stopReasonName);

        var legacy = await RunFixtureAsync(
            scenario,
            CliExecutionEngines.Legacy,
            fixtureName,
            stopReason,
            nodePath);
        var car = await RunFixtureAsync(
            scenario,
            CliExecutionEngines.Car,
            fixtureName,
            stopReason,
            nodePath);

        Assert.Equal(expectedStatus, legacy.Execution.Status);
        Assert.Equal(expectedStatus, car.Execution.Status);
        Assert.Equal(expectedRunOutcome, legacy.Execution.RunOutcome);
        Assert.Equal(expectedRunOutcome, car.Execution.RunOutcome);
        Assert.Equal(expectedTerminalReason, legacy.TerminalReason);
        Assert.Equal(expectedTerminalReason, car.TerminalReason);

        Assert.True(
            legacy.NormalizedEvents.SequenceEqual(car.NormalizedEvents, StringComparer.Ordinal),
            "normalized typed event sequence differs\n"
            + $"legacy:\n  {string.Join("\n  ", legacy.NormalizedEvents)}\n"
            + $"CAR:\n  {string.Join("\n  ", car.NormalizedEvents)}");
        Assert.Equal(legacy.TerminalOutcome, car.TerminalOutcome);
        Assert.Equal(legacy.TerminalReason, car.TerminalReason);
        Assert.Equal(legacy.TerminalExitCode, car.TerminalExitCode);

        Assert.False(IsProcessAlive(legacy.ProcessId),
            $"legacy {scenario} process {legacy.ProcessId} survived finalization");
        Assert.False(IsProcessAlive(car.ProcessId),
            $"CAR {scenario} process {car.ProcessId} survived finalization");
    }

    [Theory]
    [InlineData(CliTypes.Claude)]
    [InlineData(CliTypes.Codex)]
    public void Studio_legacy_and_car_clean_context_recipes_are_equivalent(string cliType)
    {
        var legacy = ExerciseCleanContextRecipe(cliType, CliExecutionEngines.Legacy);
        var car = ExerciseCleanContextRecipe(cliType, CliExecutionEngines.Car);

        Assert.Equal(legacy, car);
        Assert.False(legacy.ExcludedStateImported);
        Assert.Equal("refreshed", legacy.SourceCredentialAfterRefresh);
        Assert.Equal("base-config", legacy.SourceConfigAfterTempMutation);
    }

    private async Task<ExecutionObservation> RunFixtureAsync(
        string scenario,
        string engine,
        string fixtureName,
        RunStopReason stopReason,
        string nodePath)
    {
        var runRoot = Path.Combine(_root, "runs", scenario, engine);
        var worktree = Path.Combine(runRoot, "worktree");
        var jobFolder = Path.Combine(runRoot, "task");
        var rulesPath = Path.Combine(runRoot, "agent-rules.md");
        var capturePath = Path.Combine(runRoot, "capture.json");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(jobFolder);
        await File.WriteAllTextAsync(rulesPath, "Finish with one terminal sentinel.\n");

        var fixturePath = Path.Combine(FixtureDirectory(), fixtureName);
        Assert.True(File.Exists(fixturePath), $"fixture not found: {fixturePath}");
        var wrapperPath = Path.Combine(runRoot, "fixture-codex");
        await WriteFixtureWrapperAsync(
            wrapperPath,
            nodePath,
            fixturePath,
            capturePath,
            stopReason == RunStopReason.None ? 0 : 30_000);

        var service = BuildCodexService(runRoot, rulesPath);
        service.SetCliPath(wrapperPath);
        var events = new ConcurrentQueue<CliRunEvent>();
        var finished = new TaskCompletionSource<CliExecution>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var jobKey = $"local-parity::{scenario}::{engine}";
        service.OnRunEvent += (id, evt) =>
        {
            if (string.Equals(id, jobKey, StringComparison.Ordinal)) events.Enqueue(evt);
        };
        service.OnFinished += (id, execution) =>
        {
            if (string.Equals(id, jobKey, StringComparison.Ordinal))
                finished.TrySetResult(execution);
        };

        var (started, error) = await service.StartAsync(
            jobId: jobKey,
            jobKey: jobKey,
            prompt: "Replay the deterministic parity fixture.",
            workingDirectory: worktree,
            model: "gpt-5.5",
            jobFolderPath: jobFolder,
            permissionMode: CliPermissionModes.Yolo,
            contextMode: CliContextModes.Shared,
            executionEngine: engine);

        Assert.Null(error);
        Assert.NotNull(started);
        var processId = started!.ProcessId;

        if (stopReason != RunStopReason.None)
        {
            await WaitUntilAsync(
                () => File.Exists(capturePath),
                TimeSpan.FromSeconds(10),
                $"{engine} fixture process did not initialize");
            Assert.True(service.Stop(jobKey, stopReason));
        }

        var final = await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(
            () => !IsProcessAlive(processId),
            TimeSpan.FromSeconds(10),
            $"{engine} fixture process {processId} was not reaped");

        var eventSnapshot = events.ToList();
        Assert.IsType<CliRunEvent.RunStarted>(eventSnapshot[0]);
        var ended = Assert.Single(eventSnapshot.OfType<CliRunEvent.RunEnded>());
        Assert.Same(ended, eventSnapshot[^1]);

        return new ExecutionObservation(
            final,
            processId,
            eventSnapshot
                // stdout and stderr are independent OS pipes. CAR surfaces
                // otherwise-unclassified stderr as Unknown/Diagnostic while
                // the rollback adapter leaves it on the raw channel. Those
                // additive diagnostics have no stable position in the typed
                // lifecycle, so compare the ordered lifecycle vocabulary.
                .Where(evt => evt is not CliRunEvent.Unknown
                              and not CliRunEvent.Diagnostic)
                .Select(NormalizeEvent)
                .ToArray(),
            ended.Outcome.ToString(),
            ended.Reason,
            ended.ExitCode);
    }

    private RecipeObservation ExerciseCleanContextRecipe(string cliType, string engine)
    {
        var userHome = Path.Combine(_root, "clean-context", cliType, engine, "home");
        var sourceDirectory = Path.Combine(
            userHome,
            string.Equals(cliType, CliTypes.Claude, StringComparison.Ordinal)
                ? ".claude"
                : ".codex");
        var credentialName = string.Equals(cliType, CliTypes.Claude, StringComparison.Ordinal)
            ? ".credentials.json"
            : "auth.json";
        var configName = string.Equals(cliType, CliTypes.Claude, StringComparison.Ordinal)
            ? "settings.json"
            : "config.toml";
        var environmentName = string.Equals(cliType, CliTypes.Claude, StringComparison.Ordinal)
            ? "CLAUDE_CONFIG_DIR"
            : "CODEX_HOME";

        WriteFile(Path.Combine(sourceDirectory, credentialName), "old-credential");
        WriteFile(Path.Combine(sourceDirectory, configName), "base-config");
        if (string.Equals(cliType, CliTypes.Claude, StringComparison.Ordinal))
        {
            WriteFile(Path.Combine(sourceDirectory, "CLAUDE.md"), "user-memory");
            WriteFile(Path.Combine(sourceDirectory, "history.jsonl"), "history");
            WriteFile(Path.Combine(sourceDirectory, "projects", "repo", "session.jsonl"), "session");
        }
        else
        {
            WriteFile(Path.Combine(sourceDirectory, "history.jsonl"), "history");
            WriteFile(Path.Combine(sourceDirectory, "sessions", "session.jsonl"), "session");
        }

        using var prepared = PrepareCleanContext(cliType, engine, userHome);
        Assert.Equal(prepared.TempHome, prepared.Environment[environmentName]);

        var relativeFiles = Directory
            .EnumerateFiles(prepared.TempHome, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(prepared.TempHome, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var excludedStateImported = relativeFiles.Any(path =>
            path.Contains("history", StringComparison.OrdinalIgnoreCase)
            || path.Contains("session", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("CLAUDE.md", StringComparison.OrdinalIgnoreCase));

        File.WriteAllText(Path.Combine(prepared.TempHome, credentialName), "refreshed");
        File.WriteAllText(Path.Combine(prepared.TempHome, configName), "temp-config-change");

        return new RecipeObservation(
            environmentName,
            string.Join("|", relativeFiles),
            excludedStateImported,
            File.ReadAllText(Path.Combine(sourceDirectory, credentialName)),
            File.ReadAllText(Path.Combine(sourceDirectory, configName)));
    }

    private static PreparedContext PrepareCleanContext(string cliType, string engine, string userHome)
    {
        if (string.Equals(engine, CliExecutionEngines.Legacy, StringComparison.Ordinal))
        {
            var prepared = string.Equals(cliType, CliTypes.Claude, StringComparison.Ordinal)
                ? StudioCleanContextPreparer.PrepareClaude(userHome, NullLogger.Instance)
                : StudioCleanContextPreparer.PrepareCodex(userHome, NullLogger.Instance);
            Assert.NotNull(prepared);
            return new PreparedContext(
                prepared!.TempHome,
                prepared.EnvOverrides,
                prepared);
        }

        // CAR 0.7 keeps the recipe builder internal even though its resulting
        // clean-context handle is part of the public driver contract. Invoke
        // that exact package implementation so this test compares recipes,
        // not a second test-side transcription of CAR's allowlist.
        var preparerType = typeof(CodingAgentRunner.CliRunner).Assembly.GetType(
            "CodingAgentRunner.Execution.CleanContextPreparer",
            throwOnError: true)!;
        var methodName = string.Equals(cliType, CliTypes.Claude, StringComparison.Ordinal)
            ? "PrepareClaude"
            : "PrepareCodex";
        var method = preparerType.GetMethod(
            methodName,
            System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var carPrepared = method!.Invoke(null, [userHome, NullLogger.Instance]);
        Assert.NotNull(carPrepared);
        var resultType = carPrepared!.GetType();
        return new PreparedContext(
            Assert.IsType<string>(resultType.GetProperty("TempHome")!.GetValue(carPrepared)),
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
                resultType.GetProperty("EnvOverrides")!.GetValue(carPrepared)),
            Assert.IsAssignableFrom<IDisposable>(carPrepared));
    }

    private static GenericCliExecutionService BuildCodexService(string taskRepository, string rulesPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = taskRepository,
                ["AgentRules:CorePath"] = rulesPath,
                ["CodexCli:Model"] = "gpt-5.5",
            })
            .Build();
        return GenericCliExecutionService.ForCodex(
            NullLogger<GenericCliExecutionService>.Instance,
            configuration,
            new CodexModelDiscovery(
                NullLogger<CodexModelDiscovery>.Instance,
                configuration),
            new CliUsageParserRegistry([new CodexUsageParser()]),
            new CliModelRegistry());
    }

    private static async Task WriteFixtureWrapperAsync(
        string wrapperPath,
        string nodePath,
        string fixturePath,
        string capturePath,
        int delayMilliseconds)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("the fixture wrapper requires Linux");

        var script = string.Join('\n',
            "#!/bin/sh",
            "if [ \"$1\" = \"--version\" ]; then",
            "  printf '%s\\n' 'fixture-codex 1.0.0'",
            "  exit 0",
            "fi",
            $"export FAKE_CLI_FIXTURE={ShellQuote(fixturePath)}",
            $"export FAKE_CLI_CAPTURE={ShellQuote(capturePath)}",
            $"export FAKE_CLI_DELAY_MS={delayMilliseconds}",
            $"exec {ShellQuote(nodePath)} {ShellQuote(FakeCliPath())} \"$@\"",
            string.Empty);
        await File.WriteAllTextAsync(wrapperPath, script);
        File.SetUnixFileMode(
            wrapperPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string NormalizeEvent(CliRunEvent evt)
    {
        var node = JsonSerializer.SerializeToNode(evt, evt.GetType());
        Assert.NotNull(node);
        RemoveVolatileProperties(node!);
        return $"{evt.GetType().Name}:{node!.ToJsonString()}";
    }

    private static void RemoveVolatileProperties(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if (key is "ObservedAt" or "RunId" or "ProcessId"
                    or "Duration" or "DurationSeconds")
                {
                    obj.Remove(key);
                    continue;
                }
                if (obj[key] is { } child) RemoveVolatileProperties(child);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                if (child != null) RemoveVolatileProperties(child);
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(predicate(), failureMessage);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string RequireLinuxAndNode()
    {
        Skip.IfNot(OperatingSystem.IsLinux(),
            "the paired fixture wrapper currently requires a POSIX executable script");
        var nodePath = FindExecutable("node");
        Skip.If(nodePath == null, "node is not on PATH; paired fixture replay requires Node.js");
        return nodePath!;
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string ShellQuote(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string FakeCliPath()
        => Path.Combine(RepoRoot(), "testdata", "cli-fixtures", "fake-cli.mjs");

    private static string FixtureDirectory()
        => Path.Combine(RepoRoot(), "testdata", "cli-fixtures", "streams");

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
        throw new InvalidOperationException(
            "agent-taskboard.sln was not found above the test source or output directory");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A just-killed fixture process can still be unwinding on Windows.
        }
    }

    private sealed record ExecutionObservation(
        CliExecution Execution,
        int ProcessId,
        IReadOnlyList<string> NormalizedEvents,
        string TerminalOutcome,
        string? TerminalReason,
        int? TerminalExitCode);

    private sealed record RecipeObservation(
        string EnvironmentName,
        string RelativeFiles,
        bool ExcludedStateImported,
        string SourceCredentialAfterRefresh,
        string SourceConfigAfterTempMutation);

    private sealed class PreparedContext(
        string tempHome,
        IReadOnlyDictionary<string, string> environment,
        IDisposable owner) : IDisposable
    {
        public string TempHome { get; } = tempHome;
        public IReadOnlyDictionary<string, string> Environment { get; } = environment;
        public void Dispose() => owner.Dispose();
    }
}
