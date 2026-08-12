using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using CodingAgentRunner.Model;

namespace AgentRunner;

/// <summary>
/// CAR-backed execution boundary for model-assisted Remote Review commands.
/// The review workspace owns the immutable subject and evidence; this type
/// only drives one read-only CLI request through the canonical execution layer.
/// </summary>
internal static class RemoteReviewAgentExecutor
{
    public static async Task<CommandExecution> RunAsync(
        RunnerOptions options,
        ReviewCommandDto command,
        string workingDirectory,
        string executionRoot,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var requestedCliType = command.CliType?.Trim();
        var normalizedCliType = AgentCliProcess.NormalizeCliType(requestedCliType);
        var cliType = normalizedCliType ?? AgentCliProcess.ConfiguredCliType(options);
        if (!string.IsNullOrWhiteSpace(requestedCliType) && normalizedCliType is null)
        {
            return Failure(
                command,
                started,
                requestedCliType,
                127,
                $"Unsupported review CLI '{requestedCliType}'. The remote agent path supports codex and claude.");
        }
        var cliBinary = ResolveBinary(options, cliType);
        if (string.IsNullOrWhiteSpace(command.Prompt))
            return Failure(command, started, cliType, 2, "Agent review command has no prompt.");
        if (string.IsNullOrWhiteSpace(cliBinary))
            return Failure(command, started, cliType, 127, $"No {cliType} binary is configured.");

        ct.ThrowIfCancellationRequested();
        var runId = $"review-{SafeSegment(command.StepId)}-{Guid.NewGuid():N}";
        var workerDirectory = Path.Combine(executionRoot, runId);
        Directory.CreateDirectory(workerDirectory);
        var spec = new DetachedJobSpec(
            cliBinary,
            [],
            workingDirectory,
            command.Prompt!,
            workerDirectory,
            Math.Clamp(command.TimeoutSeconds, 1, 7200),
            CliType: cliType,
            Model: command.Model,
            ThinkingLevel: command.ThinkingLevel,
            PermissionMode: CliPermissionModes.ReadOnly,
            ContextMode: CliContextModes.Shared,
            Engine: RunnerOptions.ExecEngineCar,
            RunId: runId);

        try
        {
            var (raw, timedOut, launchFailed) = await CarWorkerExecution.RunAsync(
                spec,
                workerDirectory,
                (_, _) => { });
            ct.ThrowIfCancellationRequested();
            if (launchFailed)
                return Failure(command, started, cliType, 127, raw.StdErr);
            var parsed = RemoteAgentStepOutput.Parse(raw, cliType, command.Model);
            return new CommandExecution(
                new ProcessResult(raw.ExitCode, parsed.Reply, raw.StdErr),
                started,
                DateTime.UtcNow,
                timedOut ? "timeout" : null,
                parsed.Usage,
                cliType,
                parsed.Model ?? command.Model);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure(command, started, cliType, 127, exception.Message);
        }
        finally
        {
            try { if (Directory.Exists(workerDirectory)) Directory.Delete(workerDirectory, recursive: true); }
            catch { /* Per-command CAR logs are already represented in review evidence. */ }
        }
    }

    private static CommandExecution Failure(
        ReviewCommandDto command,
        DateTime started,
        string cliType,
        int exitCode,
        string detail)
        => new(
            new ProcessResult(
                exitCode,
                string.Empty,
                $"Agent review step '{command.StepId}' could not run: {detail}"),
            started,
            DateTime.UtcNow,
            null,
            CliType: cliType,
            Model: command.Model);

    internal static string ResolveBinary(RunnerOptions options, string cliType)
    {
        var configured = AgentCliProcess.ConfiguredCliType(options);
        if (string.Equals(cliType, configured, StringComparison.Ordinal)) return options.CliBin;
        return string.Equals(cliType, AgentCliProcess.CodexCli, StringComparison.Ordinal)
            ? options.CodexCliBin
            : options.ClaudeCliBin;
    }

    internal static string CommandBinary(RunnerOptions options, ReviewCommandDto command)
    {
        var cliType = AgentCliProcess.NormalizeCliType(command.CliType);
        return cliType is null
            ? command.CliType ?? command.FileName
            : ResolveBinary(options, cliType);
    }

    private static string SafeSegment(string value)
        => new(value.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
}

internal sealed record RemoteAgentStepResult(
    string Reply,
    string? Model,
    ReviewTokenUsageDto? Usage);

internal static class RemoteAgentStepOutput
{
    public static RemoteAgentStepResult Parse(
        ProcessResult process,
        string cliType,
        string? requestedModel)
    {
        if (string.Equals(cliType, AgentCliProcess.CodexCli, StringComparison.Ordinal))
        {
            var parsed = RemoteProjectChatRunner.ParseCodex(process, requestedModel ?? "unknown");
            var usage = parsed.TokenUsage is null
                ? null
                : new ReviewTokenUsageDto(
                    parsed.TokenUsage.Model ?? requestedModel ?? "unknown",
                    parsed.TokenUsage.InputTokens,
                    parsed.TokenUsage.OutputTokens,
                    parsed.TokenUsage.CacheReadTokens,
                    parsed.TokenUsage.CacheCreationTokens);
            return new RemoteAgentStepResult(
                parsed.ReplyText,
                parsed.TokenUsage?.Model ?? requestedModel,
                usage);
        }

        try
        {
            using var document = JsonDocument.Parse(process.StdOut);
            var root = document.RootElement;
            var reply = root.TryGetProperty("result", out var result)
                ? result.GetString() ?? string.Empty
                : process.StdOut;
            var model = root.TryGetProperty("model", out var modelNode)
                ? modelNode.GetString() ?? requestedModel
                : requestedModel;
            ReviewTokenUsageDto? usage = null;
            if (root.TryGetProperty("usage", out var usageNode)
                && usageNode.ValueKind == JsonValueKind.Object)
            {
                usage = new ReviewTokenUsageDto(
                    model ?? "unknown",
                    ReadLong(usageNode, "input_tokens"),
                    ReadLong(usageNode, "output_tokens"),
                    ReadLong(usageNode, "cache_read_input_tokens"),
                    ReadLong(usageNode, "cache_creation_input_tokens"));
            }
            return new RemoteAgentStepResult(reply.Trim(), model, usage);
        }
        catch (JsonException)
        {
            return new RemoteAgentStepResult(process.StdOut.Trim(), requestedModel, null);
        }
    }

    private static long ReadLong(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.TryGetInt64(out var parsed)
            ? Math.Max(0, parsed)
            : 0;
}
