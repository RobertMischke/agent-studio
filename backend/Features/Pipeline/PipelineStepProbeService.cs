namespace AgentStudio.Pipeline;

public sealed record PipelineStepProbeResult(
    string StepId,
    string Status,
    bool Applicable,
    int? ExitCode,
    long DurationMs,
    string Output,
    long QueueWaitMs);

/// <summary>
/// Runs one catalogue step's effective shell command without creating or
/// moving tasks. Command execution is delegated to the build/test gate runner,
/// so probes share its process semaphore and cross-process machine lock.
/// </summary>
public sealed class PipelineStepProbeService
{
    private readonly IBuildTestGateRunner _gate;
    private readonly ProjectSettingsService _settings;
    private readonly IConfiguration _configuration;

    public PipelineStepProbeService(
        IBuildTestGateRunner gate,
        ProjectSettingsService settings,
        IConfiguration configuration)
    {
        _gate = gate;
        _settings = settings;
        _configuration = configuration;
    }

    public async Task<PipelineStepProbeResult> RunAsync(
        string projectName,
        string repositoryPath,
        PipelineStep step,
        CancellationToken ct)
    {
        var stacks = ProjectStackDetector.Detect(repositoryPath);
        var applicable = ProjectStackDetector.Applies(step.AppliesTo, stacks);
        if (!applicable)
        {
            return new PipelineStepProbeResult(
                step.Id, "not-applicable", false, null, 0,
                $"Not applicable. This step requires {step.AppliesTo}; detected: {DetectedLabel(stacks)}.", 0);
        }

        var settings = _settings.Get(projectName);
        var execution = PipelineStepExecutionResolver.Resolve(step, repositoryPath, settings);
        if (execution.Commands.Count == 0)
        {
            var output = execution.ExecutionKind == "internal"
                ? "No standalone shell command. This step runs inside the task pipeline and needs task/run context."
                : "No effective command could be derived from the project repository.";
            return new PipelineStepProbeResult(step.Id, "unavailable", true, null, 0, output, 0);
        }

        var timeoutSeconds = Math.Clamp(
            _configuration.GetValue($"PipelineStepProbe:{step.Id}:TimeoutSeconds", 600), 10, 3600);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        BuildProfile? profile;
        if (step.Id.Equals(PipelineCatalogue.BuildTestGateStepId, StringComparison.OrdinalIgnoreCase))
        {
            profile = settings.BuildProfile;
        }
        else
        {
            profile = new BuildProfile
            {
                BuildCmds = execution.Commands.Select(RenderShellCommand).ToArray(),
            };
        }

        var result = await _gate.RunAsync(
            new BuildTestGateRequest(repositoryPath, null, "pipeline-step-probe", RequireExactSubject: false)
            {
                GateId = step.Id,
                QueueWaitTimeout = timeout + TimeSpan.FromMinutes(2),
            },
            changedFiles: null,
            profile,
            PostStepMode.Fail,
            timeout,
            ct).ConfigureAwait(false);

        var status = result.Verdict switch
        {
            BuildTestGateVerdict.Ok => "passed",
            BuildTestGateVerdict.Warn => "failed",
            BuildTestGateVerdict.Fail => "failed",
            _ => "skipped",
        };
        var outputText = string.IsNullOrWhiteSpace(result.Output)
            ? result.Reason
            : $"{result.Output.TrimEnd()}\n\n{result.Reason}";
        return new PipelineStepProbeResult(
            step.Id, status, true, result.ExitCode, result.DurationMs,
            outputText, result.GateQueueWaitMs);
    }

    private static string RenderShellCommand(EffectivePipelineCommand command)
        => string.IsNullOrWhiteSpace(command.WorkingSubdir)
            ? command.Command
            : $"cd {QuoteShellArg(command.WorkingSubdir)} && {command.Command}";

    private static string QuoteShellArg(string value)
        => $"'{value.Replace("'", "'\"'\"'")}'";

    private static string DetectedLabel(IReadOnlyCollection<string> stacks)
        => stacks.Count == 0 ? "none" : string.Join(", ", stacks);
}
