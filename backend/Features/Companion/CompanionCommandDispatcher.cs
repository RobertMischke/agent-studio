using System.Text.Json;

namespace AgentStudio.Companion;

/// <summary>
/// Translates one relay command into the matching in-process service call.
/// Each command kind dispatches through an existing service so the runner
/// remains the single state-machine authority (ADR-0017 / ADR-0018).
/// </summary>
public sealed class CompanionCommandDispatcher
{
    private readonly TaskRunnerService _runner;
    private readonly TaskMutationService _mutations;
    private readonly ILogger<CompanionCommandDispatcher> _log;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public CompanionCommandDispatcher(
        TaskRunnerService runner,
        TaskMutationService mutations,
        ILogger<CompanionCommandDispatcher> log)
    {
        _runner = runner;
        _mutations = mutations;
        _log = log;
    }

    public async Task<DispatchResult> DispatchAsync(CompanionRelayCommand cmd, CancellationToken ct)
    {
        try
        {
            switch (cmd.Kind)
            {
                case "decision-answer":
                    return await DispatchDecisionAnswer(cmd, ct);
                case "new-task":
                    return DispatchNewTask(cmd);
                case "start-job":
                    return await DispatchStartJob(cmd, ct);
                default:
                    return DispatchResult.Reject($"unknown kind '{cmd.Kind}'");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Companion command {Kind} ({Id}) failed", cmd.Kind, cmd.Id);
            return DispatchResult.Reject(ex.Message);
        }
    }

    private async Task<DispatchResult> DispatchDecisionAnswer(CompanionRelayCommand cmd, CancellationToken ct)
    {
        var p = cmd.Payload.Deserialize<DecisionAnswerPayload>(JsonOpts);
        if (p is null || string.IsNullOrWhiteSpace(p.JobId) || string.IsNullOrWhiteSpace(p.Text))
            return DispatchResult.Reject("decision-answer requires jobId and text");

        await _runner.ContinueJobAsync(p.JobId, p.Text, p.WatchPath, mode: p.Mode ?? "continue", ct: ct);
        return DispatchResult.Accept($"continued job {p.JobId}");
    }

    private DispatchResult DispatchNewTask(CompanionRelayCommand cmd)
    {
        var p = cmd.Payload.Deserialize<NewTaskPayload>(JsonOpts);
        if (p is null || string.IsNullOrWhiteSpace(p.WatchPath) || string.IsNullOrWhiteSpace(p.Title))
            return DispatchResult.Reject("new-task requires watchPath and title");

        var req = new CreateJobRequest
        {
            Id = SlugFromTitle(p.Title),
            Title = p.Title,
            Agent = string.IsNullOrEmpty(p.Agent) ? "claude" : p.Agent,
            CliType = p.CliType,
            Model = p.Model,
            WatchPath = p.WatchPath,
            PromptMarkdown = p.Prompt,
            TargetState = TaskStates.Ready,
        };
        var id = _mutations.CreateJob(req);
        return id is null
            ? DispatchResult.Reject("CreateJob returned null")
            : DispatchResult.Accept($"created job {id}");
    }

    private async Task<DispatchResult> DispatchStartJob(CompanionRelayCommand cmd, CancellationToken ct)
    {
        var p = cmd.Payload.Deserialize<StartJobPayload>(JsonOpts);
        if (p is null || string.IsNullOrWhiteSpace(p.JobId))
            return DispatchResult.Reject("start-job requires jobId");

        await _runner.StartJobAsync(p.JobId, p.WatchPath, ct: ct);
        return DispatchResult.Accept($"started job {p.JobId}");
    }

    internal static string SlugFromTitle(string title)
    {
        var trimmed = (title ?? "").Trim().ToLowerInvariant();
        var chars = trimmed.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? $"task-{DateTime.UtcNow:yyyyMMddHHmmss}" : slug;
    }
}

public readonly record struct DispatchResult(bool Applied, string Message)
{
    public static DispatchResult Accept(string message) => new(true, message);
    public static DispatchResult Reject(string message) => new(false, message);
}
