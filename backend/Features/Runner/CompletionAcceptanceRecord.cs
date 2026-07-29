using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// Durable, structured completion evidence. The completion gate consumes this
/// record instead of guessing task state from bullets or narrative in
/// <c>status.md</c>. Status prose remains useful review context, but it is not a
/// state-machine input.
/// </summary>
public sealed record CompletionAcceptanceRecord
{
    public const string FileName = "completion-acceptance.json";

    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("recordedAt")] public DateTime RecordedAt { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("requirements")] public IReadOnlyList<CompletionRequirement> Requirements { get; init; } = [];
    [JsonPropertyName("evidence")] public IReadOnlyList<CompletionEvidenceItem> Evidence { get; init; } = [];
    [JsonPropertyName("blockers")] public IReadOnlyList<CompletionBlocker> Blockers { get; init; } = [];
    [JsonPropertyName("lifecycle")] public CompletionLifecycle Lifecycle { get; init; } = new();
    [JsonPropertyName("decisionReason")] public string DecisionReason { get; init; } = string.Empty;

    private static readonly Regex SentinelRegex = new(
        @"\[\[TASK_(?<kind>DONE|NOOP|BLOCKED|NEEDS_INPUT)(?::(?<reason>[^\]]+))?\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static CompletionAcceptanceRecord Capture(
        string taskPrompt,
        string recentLog,
        int? exitCode,
        bool runStatusCompleted,
        bool hasResultsArtifacts,
        bool parsedDoneSignal = false)
    {
        var matches = SentinelRegex.Matches(recentLog ?? string.Empty);
        var terminal = matches.Count == 0 ? null : matches[^1];
        var terminalKind = terminal?.Groups["kind"].Value.ToUpperInvariant();
        var terminalReason = terminal?.Groups["reason"].Value.Trim();
        // Reaching Post Processing with a parsed terminal is itself a durable
        // completed-turn signal. Older/remote runs may not have the synthetic
        // CLI-exit log line even though PendingDecisionScanner parsed TASK_DONE.
        var turnComplete = terminal is not null || parsedDoneSignal || exitCode == 0 || runStatusCompleted;
        var implementationComplete = turnComplete
            && (terminalKind is "DONE" or "NOOP" || parsedDoneSignal);

        var evidence = new List<CompletionEvidenceItem>
        {
            new("terminal-sentinel", "logs/cli-output.log", terminalKind is "DONE" or "NOOP" || parsedDoneSignal,
                terminalKind is not null
                    ? $"Latest structured terminal is TASK_{terminalKind}."
                    : parsedDoneSignal
                        ? "The pending decision scanner supplied a parsed TASK_DONE signal for the current subject."
                        : "No structured task terminal was recorded."),
            new("process-terminal", "logs/cli-output.log", turnComplete,
                exitCode is not null
                    ? $"Latest CLI exit code is {exitCode}."
                    : terminal is not null
                        ? "The parsed structured terminal completed the turn before Post Processing."
                        : $"Run completed status is {runStatusCompleted}."),
            new("results-artifacts", "results/", hasResultsArtifacts,
                hasResultsArtifacts ? "The run produced durable result artifacts." : "No result artifact is required for this acceptance stage."),
        };

        var blockers = new List<CompletionBlocker>();
        if (terminalKind is "BLOCKED" or "NEEDS_INPUT")
        {
            blockers.Add(new CompletionBlocker(
                $"terminal-{terminalKind.ToLowerInvariant()}",
                "logs/cli-output.log",
                string.IsNullOrWhiteSpace(terminalReason) ? "Agent emitted an explicit blocking terminal." : terminalReason!,
                "agent-terminal"));
        }

        var reason = blockers.Count > 0
            ? "Explicit structured blocker prevents implementation completion."
            : implementationComplete
                ? "Turn and implementation are complete from structured terminal/process evidence; acceptance review may proceed."
                : "Structured completion evidence is missing or unsuccessful."
            ;

        return new CompletionAcceptanceRecord
        {
            Requirements = CaptureRequirements(taskPrompt),
            Evidence = evidence,
            Blockers = blockers,
            Lifecycle = new CompletionLifecycle(
                TurnComplete: turnComplete,
                ImplementationComplete: implementationComplete,
                TaskAccepted: false,
                DeploymentPushPending: true,
                DeploymentReason: "Commit, push, and deployment are platform-owned durability steps after implementation completion."),
            DecisionReason = reason,
        };
    }

    private static IReadOnlyList<CompletionRequirement> CaptureRequirements(string taskPrompt)
    {
        // Preserve every non-heading prompt line with its exact source line.
        // This is intentionally capture, not semantic interpretation: aspect
        // review decides whether each requirement is satisfied.
        var requirements = new List<CompletionRequirement>();
        var lines = (taskPrompt ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var text = lines[index].Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            requirements.Add(new CompletionRequirement(
                $"prompt-line-{index + 1}", text, $"prompt.md:{index + 1}", "pending-review",
                "Requirement acceptance is determined by the structured aspect-review verdicts."));
        }

        if (requirements.Count == 0)
        {
            requirements.Add(new CompletionRequirement(
                "task-prompt", taskPrompt ?? string.Empty, "prompt.md", "pending-review",
                "The prompt contained no non-heading requirement lines; structured review must resolve the task title/context."));
        }
        return requirements;
    }

    public static void Write(string jobFolder, CompletionAcceptanceRecord record, ILogger? logger = null)
    {
        try
        {
            File.WriteAllText(Path.Combine(jobFolder, FileName), JsonSerializer.Serialize(record, JsonOptions));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "completion-acceptance-write failed for {JobFolder}", jobFolder);
        }
    }

    public static void MarkAccepted(string jobFolder, string source, string reason, ILogger? logger = null)
    {
        try
        {
            var path = Path.Combine(jobFolder, FileName);
            if (!File.Exists(path)) return;
            var record = JsonSerializer.Deserialize<CompletionAcceptanceRecord>(File.ReadAllText(path), JsonOptions);
            if (record is null) return;
            Write(jobFolder, record with
            {
                Requirements = record.Requirements.Select(r => r with
                {
                    Status = "accepted",
                    Reason = reason,
                }).ToList(),
                Evidence = record.Evidence.Append(new CompletionEvidenceItem(
                    "acceptance-review", source, true, reason)).ToList(),
                Lifecycle = record.Lifecycle with { TaskAccepted = true },
                DecisionReason = reason,
            }, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "completion-acceptance-update failed for {JobFolder}", jobFolder);
        }
    }
}

public sealed record CompletionRequirement(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record CompletionEvidenceItem(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("supportsCompletion")] bool SupportsCompletion,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record CompletionBlocker(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("kind")] string Kind);

public sealed record CompletionLifecycle(
    [property: JsonPropertyName("turnComplete")] bool TurnComplete = false,
    [property: JsonPropertyName("implementationComplete")] bool ImplementationComplete = false,
    [property: JsonPropertyName("taskAccepted")] bool TaskAccepted = false,
    [property: JsonPropertyName("deploymentPushPending")] bool DeploymentPushPending = true,
    [property: JsonPropertyName("deploymentReason")] string DeploymentReason = "Not evaluated.");
