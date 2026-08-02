using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestratorEngine;

public sealed record OrchestrationStageDecision(
    OrchestrationAction Action,
    string OutputJson);

public interface IOrchestrationStageHandler
{
    OrchestrationStage Stage { get; }
    Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct);
}

public sealed class ReviewDecisionOrchestratorLoop : IOrchestrationStageHandler
{
    public OrchestrationStage Stage => OrchestrationStage.ReviewDecision;

    public Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var payload = JsonDocument.Parse(run.PayloadJson);
        var reviewOutcome = ReadString(payload.RootElement, "reviewOutcome");
        var outcome = reviewOutcome
                      ?? ReadString(payload.RootElement, "agentOutcome")
                      ?? ReadString(payload.RootElement, "terminal");
        var action = Normalize(outcome) switch
        {
            "productfailure" => OrchestrationAction.Reissue,
            "reviewinfra" => OrchestrationAction.Escalate,
            "pass" => OrchestrationAction.Continue,
            "blocked" => OrchestrationAction.Escalate,
            "needsinput" => OrchestrationAction.Reissue,
            _ => OrchestrationAction.Continue,
        };
        return Task.FromResult(new OrchestrationStageDecision(
            action,
            JsonSerializer.Serialize(new
            {
                component = nameof(ReviewDecisionOrchestratorLoop),
                source = reviewOutcome is null ? "execution-terminal" : "remote-review",
                observedOutcome = outcome,
                decision = action.ToString(),
            })));
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Normalize(string? value)
        => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

public sealed class CouncilLoop : IOrchestrationStageHandler
{
    public OrchestrationStage Stage => OrchestrationStage.Council;

    public Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var payload = JsonDocument.Parse(run.PayloadJson);
        var blockerCount = 0;
        var findingCount = 0;
        if (payload.RootElement.TryGetProperty("reviewFindings", out var findings)
            && findings.ValueKind == JsonValueKind.Array)
        {
            foreach (var finding in findings.EnumerateArray())
            {
                findingCount++;
                if (finding.TryGetProperty("severity", out var severity)
                    && severity.ValueKind == JsonValueKind.String
                    && severity.GetString() is { } value
                    && value is "blocker" or "critical")
                    blockerCount++;
            }
        }
        if (payload.RootElement.TryGetProperty("verdicts", out var verdicts)
            && verdicts.ValueKind == JsonValueKind.Array)
        {
            foreach (var verdict in verdicts.EnumerateArray())
            {
                if (!verdict.TryGetProperty("status", out var status)
                    || status.ValueKind != JsonValueKind.String
                    || status.GetString() is not { } value
                    || value == "pass")
                    continue;
                findingCount++;
                if (value is "concerns" or "block" or "fail")
                    blockerCount++;
            }
        }
        var action = blockerCount > 0
            ? OrchestrationAction.Reissue
            : OrchestrationAction.Continue;
        return Task.FromResult(new OrchestrationStageDecision(
            action,
            JsonSerializer.Serialize(new
            {
                component = nameof(CouncilLoop),
                findingCount,
                blockerCount,
                decision = action.ToString(),
            })));
    }
}

public sealed class PostProcessingLoop : IOrchestrationStageHandler
{
    public OrchestrationStage Stage => OrchestrationStage.PostProcessing;

    public Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new OrchestrationStageDecision(
            OrchestrationAction.Continue,
            JsonSerializer.Serialize(new
            {
                component = nameof(PostProcessingLoop),
                status = "decision-chain-running-remotely",
            })));
    }
}

public sealed class GateDispatchLoop : IOrchestrationStageHandler
{
    public OrchestrationStage Stage => OrchestrationStage.GateDispatch;

    public Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var payload = JsonDocument.Parse(run.PayloadJson);
        var failed = 0;
        if (payload.RootElement.TryGetProperty("gates", out var gates)
            && gates.ValueKind == JsonValueKind.Array)
        {
            failed = gates.EnumerateArray().Count(gate =>
                gate.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && status.GetString() is "failed" or "fail");
        }
        var action = failed > 0
            ? OrchestrationAction.Reissue
            : OrchestrationAction.Continue;
        return Task.FromResult(new OrchestrationStageDecision(
            action,
            JsonSerializer.Serialize(new
            {
                component = nameof(GateDispatchLoop),
                failed,
                decision = action.ToString(),
            })));
    }
}

public sealed class CompletionJudgeLoop : IOrchestrationStageHandler
{
    public OrchestrationStage Stage => OrchestrationStage.CompletionJudge;

    public Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var priorFailure = run.StageResults?.Any(result =>
            result.Action is OrchestrationAction.Fail or OrchestrationAction.Escalate) == true;
        var action = priorFailure
            ? OrchestrationAction.Escalate
            : OrchestrationAction.Complete;
        return Task.FromResult(new OrchestrationStageDecision(
            action,
            JsonSerializer.Serialize(new
            {
                component = nameof(CompletionJudgeLoop),
                decision = action.ToString(),
            })));
    }
}
