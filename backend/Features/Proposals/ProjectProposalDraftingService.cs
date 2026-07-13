using System.Text.Json;

namespace AgentStudio.Proposals;

public sealed record GeneratedProposalDraft(
    string Finding,
    string Proposal,
    string EstimatedEffort,
    string Severity,
    IReadOnlyList<string> Categories);

/// <summary>Small, read-only CLI passes used by proposal management.</summary>
public class ProjectProposalDraftingService
{
    private readonly CliOneShotRegistry _oneShots;
    private readonly IConfiguration _configuration;

    public ProjectProposalDraftingService(CliOneShotRegistry oneShots, IConfiguration configuration)
    {
        _oneShots = oneShots;
        _configuration = configuration;
    }

    public virtual async Task<string> RefineFeedbackAsync(string feedback, CancellationToken ct)
    {
        var input = feedback.Trim();
        if (input.Length == 0) return "";
        var prompt = $$"""
            Translate the following project-proposal rejection feedback to concise, professional English.
            Preserve every concrete observation and requested correction. Remove filler and repetition.
            Return only the refined feedback as plain text, without a heading or commentary.

            FEEDBACK:
            {{input}}
            """;
        return (await RunAsync(prompt, null, ct)).Trim();
    }

    public virtual async Task<GeneratedProposalDraft> GenerateAsync(
        string projectRoot, string topic, string guidance, CancellationToken ct)
    {
        var prompt = $$"""
            You are preparing one implementation-ready project proposal for the repository in your current working directory.
            Work read-only. Inspect only the files needed to ground the requested topic. Do not edit files or run mutating commands.

            TOPIC: {{topic}}
            OPERATOR GUIDANCE: {{(string.IsNullOrWhiteSpace(guidance) ? "None" : guidance.Trim())}}

            Return JSON only with this exact shape:
            {"finding":"measured or code-grounded current-state observation","proposal":"specific implementation decision","estimatedEffort":"small|medium|large","severity":"critical|medium|low","categories":["short-category"]}
            The topic must be explicit in both finding and proposal. Do not invent measured evidence.
            """;
        var raw = await RunAsync(prompt, projectRoot, ct);
        return ParseDraft(raw);
    }

    internal static GeneratedProposalDraft ParseDraft(string raw)
    {
        var text = raw.Trim();
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first < 0 || last <= first) throw new InvalidOperationException("Proposal CLI did not return JSON.");
        using var json = JsonDocument.Parse(text[first..(last + 1)]);
        var root = json.RootElement;
        string Value(string key) => root.TryGetProperty(key, out var value) ? value.GetString()?.Trim() ?? "" : "";
        var finding = Value("finding");
        var proposal = Value("proposal");
        if (finding.Length == 0 || proposal.Length == 0)
            throw new InvalidOperationException("Proposal CLI returned an incomplete draft.");
        var effort = Value("estimatedEffort").ToLowerInvariant();
        if (effort is not ("small" or "medium" or "large")) effort = "medium";
        var severity = Value("severity").ToLowerInvariant();
        if (severity is not ("critical" or "medium" or "low")) severity = "medium";
        var categories = root.TryGetProperty("categories", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(value => value.GetString()?.Trim() ?? "").Where(value => value.Length > 0).Take(5).ToArray()
            : [];
        return new GeneratedProposalDraft(finding, proposal, effort, severity, categories);
    }

    private async Task<string> RunAsync(string prompt, string? workingDirectory, CancellationToken ct)
    {
        var cli = _configuration["ProposalManagement:Cli"] ?? "claude";
        var model = _configuration["ProposalManagement:Model"]
                    ?? _configuration["PromptEnhancement:Model"]
                    ?? ModelIds.ClaudeHaiku45;
        var result = await _oneShots.Require(cli).RunAsync(new CliOneShotRequest(cli, model, prompt)
        {
            WorkingDirectory = workingDirectory,
            Timeout = TimeSpan.FromSeconds(90),
            Source = "proposal-management",
        }, ct).ConfigureAwait(false);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "Proposal CLI call failed.");
        return string.IsNullOrWhiteSpace(result.ParsedText) ? result.Stdout : result.ParsedText;
    }
}
