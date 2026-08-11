using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Delegation;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;
using AgentStudio.TaskServer.Contracts;
using CarOutputLine = CodingAgentRunner.Model.CliOutputLine;

namespace AgentRunner;

/// <summary>
/// Executes one frozen semantic review command through the central CAR driver.
/// The Review Executor supplies the fenced workspace and review-scoped
/// environment; CAR supplies provider-specific read-only invocation semantics.
/// </summary>
internal static class RemoteAspectCliExecution
{
    public static string ResolveExecutable(RunnerOptions options, ReviewCommandDto command)
        => Resolve(options, command).FileName;

    public static async Task<RemoteAspectCliResult> RunAsync(
        RunnerOptions options,
        string workingDirectory,
        ReviewCommandDto command,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken ct)
    {
        var invocation = Resolve(options, command);
        var cliType = invocation.CliType;
        var logDirectory = Path.Combine(
            Path.GetDirectoryName(workingDirectory) ?? workingDirectory,
            $"car-{Safe(command.StepId)}-{Guid.NewGuid():N}");
        var runner = new CliRunner(
            new CliOptions
            {
                ClaudePath = cliType == AgentCliProcess.ClaudeCli ? invocation.FileName : null,
                CodexPath = cliType == AgentCliProcess.CodexCli ? invocation.FileName : null,
                ClaudePromptTransport = ClaudePromptTransport.Stdin,
                AllowAgentGitMutation = false,
                Delegation = new DelegationOptions { Enabled = false },
            },
            logPaths: new ReviewRunLogPathProvider(logDirectory));
        var driver = runner.Get(cliType);
        var runId = $"review-{Safe(command.StepId)}-{Guid.NewGuid():N}";
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var finished = new TaskCompletionSource<CliRunInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnOutput(string id, CarOutputLine line)
        {
            if (!string.Equals(id, runId, StringComparison.Ordinal)) return;
            if (line.Stream == "stdout") stdout.AppendLine(line.Text);
            else if (line.Stream == "stderr") stderr.AppendLine(line.Text);
        }

        void OnFinished(string id, CliRunInfo info)
        {
            if (string.Equals(id, runId, StringComparison.Ordinal))
                finished.TrySetResult(info);
        }

        var startedAt = DateTime.UtcNow;
        driver.OnOutput += OnOutput;
        driver.OnFinished += OnFinished;
        try
        {
            var extraEnvironment = environment
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
            var (run, error) = await driver.StartAsync(
                new CliRunRequest
                {
                    RunId = runId,
                    Prompt = command.Prompt!,
                    WorkingDirectory = workingDirectory,
                    Model = command.Model,
                    ThinkingLevel = command.ThinkingLevel,
                    PermissionMode = CliPermissionModes.ReadOnly,
                    ContextMode = CliContextModes.Shared,
                    ExtraEnvironment = extraEnvironment,
                },
                ct);
            if (run is null)
            {
                return new RemoteAspectCliResult(
                    new ProcessResult(127, string.Empty, error ?? "CAR review start failed."),
                    startedAt,
                    DateTime.UtcNow,
                    null,
                    invocation.FileName,
                    []);
            }

            using var cancellation = ct.Register(() => driver.Stop(runId, RunStopReason.Watchdog));
            CliRunInfo info;
            try
            {
                info = await finished.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                driver.Stop(runId, RunStopReason.Watchdog);
                await Task.WhenAny(
                    finished.Task,
                    Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
                throw;
            }
            return new RemoteAspectCliResult(
                new ProcessResult(info.ExitCode ?? 1, stdout.ToString(), stderr.ToString()),
                startedAt,
                DateTime.UtcNow,
                null,
                invocation.FileName,
                []);
        }
        finally
        {
            driver.OnOutput -= OnOutput;
            driver.OnFinished -= OnFinished;
            driver.Forget(runId);
            try
            {
                if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, recursive: true);
            }
            catch
            {
                // Review workspace cleanup remains the bounded final backstop.
            }
        }
    }

    private static AgentCliProcess.CliInvocation Resolve(
        RunnerOptions options,
        ReviewCommandDto command)
        => AgentCliProcess.Resolve(
            options,
            new RunSpecDto(command.CliType, command.Model, command.ThinkingLevel));

    private static string Safe(string value)
        => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());

    private sealed class ReviewRunLogPathProvider(string directory) : IRunLogPathProvider
    {
        public string GetRunLogDirectory(string runId) => directory;
        public string GetActiveJobsFile() => Path.Combine(directory, "active-runs.json");
    }
}

internal sealed record RemoteAspectCliResult(
    ProcessResult Process,
    DateTime StartedAt,
    DateTime FinishedAt,
    string? Signal,
    string FileName,
    IReadOnlyList<string> Arguments);
