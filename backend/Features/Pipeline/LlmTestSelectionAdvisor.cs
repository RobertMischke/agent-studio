using System.Text.Json;

namespace AgentStudio.Pipeline;

public interface ITestSelectionAdvisor
{
    Task<TestSelectionAdvice?> AdviseAsync(
        TestSelectionAudit input,
        TestExecutionPolicy? policy,
        string repositoryPath,
        string? project,
        string? jobId,
        CancellationToken ct);
}

/// <summary>
/// Optional constrained LLM layer for impacted-test selection. The prompt and
/// answer are not executable. The planner accepts only candidate ids from its
/// deterministic inventory and persists the exact diff, history, candidates,
/// chosen ids, model, and rationale in <see cref="TestSelectionAudit"/>.
/// </summary>
public sealed class LlmTestSelectionAdvisor : ITestSelectionAdvisor
{
    private readonly CliOneShotRegistry _oneShots;
    private readonly ILogger<LlmTestSelectionAdvisor> _logger;

    public LlmTestSelectionAdvisor(
        CliOneShotRegistry oneShots,
        ILogger<LlmTestSelectionAdvisor> logger)
    {
        _oneShots = oneShots;
        _logger = logger;
    }

    public async Task<TestSelectionAdvice?> AdviseAsync(
        TestSelectionAudit input,
        TestExecutionPolicy? policy,
        string repositoryPath,
        string? project,
        string? jobId,
        CancellationToken ct)
    {
        if (policy?.LlmSelectionEnabled != true || input.Candidates.Count == 0) return null;
        var resolved = TaskSpawnerModelSelector.Resolve(
            policy.LlmModel,
            policy.LlmCliType,
            policy.LlmThinkingLevel);
        var cli = _oneShots.Get(resolved.Cli);
        if (cli is null)
        {
            _logger.LogWarning("test selection adviser CLI {Cli} is unavailable; deterministic selection remains", resolved.Cli);
            return null;
        }

        var prompt = BuildPrompt(input);
        var result = await cli.RunAsync(new CliOneShotRequest(resolved.Cli, resolved.Model, prompt)
        {
            ThinkingLevel = resolved.ThinkingLevel,
            WorkingDirectory = repositoryPath,
            Timeout = TimeSpan.FromMinutes(2),
            Source = AdHocUsageSources.ReviewDecision,
            Project = project,
            JobId = jobId,
            RecordUsage = true,
            StepId = PipelineCatalogue.BuildTestGateStepId,
        }, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            _logger.LogWarning("test selection adviser failed: {Error}", result.Error ?? result.Stderr);
            return null;
        }

        var reply = string.IsNullOrWhiteSpace(result.ParsedText) ? result.Stdout : result.ParsedText;
        return Parse(reply, resolved.Model);
    }

    internal static string BuildPrompt(TestSelectionAudit input)
    {
        var payload = JsonSerializer.Serialize(new
        {
            diffInput = input.DiffInput,
            testHubHistory = input.HistoryInput,
            candidates = input.Candidates.Select(candidate => new
            {
                candidate.Id,
                command = candidate.Command.Command,
                workingSubdir = candidate.Command.WorkingSubdir,
                deterministicReasons = candidate.Reasons,
            }),
        }, new JsonSerializerOptions { WriteIndented = true });
        return """
            Select additional tests that are plausibly impacted by this diff. You may only select candidate ids from the supplied inventory. Do not remove deterministic selections and do not write shell commands. Return one JSON object only:
            {"selectedCandidateIds":["test-id"],"reason":"short evidence-based rationale"}

            INPUT:
            """ + payload;
    }

    internal static TestSelectionAdvice? Parse(string? raw, string model)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            var ids = root.TryGetProperty("selectedCandidateIds", out var selected)
                && selected.ValueKind == JsonValueKind.Array
                ? selected.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? "")
                    .Where(id => id.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
                : [];
            var reason = root.TryGetProperty("reason", out var reasonNode)
                && reasonNode.ValueKind == JsonValueKind.String
                ? reasonNode.GetString() ?? "no rationale supplied"
                : "no rationale supplied";
            return new TestSelectionAdvice(ids, reason, model);
        }
        catch (JsonException ex)
        {
            SilentCatch.Note(ex, "LlmTestSelectionAdvisor: malformed adviser reply");
            return null;
        }
    }
}
