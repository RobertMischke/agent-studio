using System.Globalization;
using System.Text;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Orchestrator;

/// <summary>
/// Resolves the active task through the bounded task read layer and renders a
/// size-limited prompt block for one orchestrator chat turn.
/// </summary>
public sealed class OrchestratorTaskPromptContextComposer
{
    internal const int MetadataTokenLimit = 256;
    internal const int PromptTokenLimit = 1_800;
    internal const int StatusTokenLimit = 1_800;
    internal const int RunOutcomeTokenLimit = 256;
    private const int EstimatedCharactersPerToken = 4;

    private readonly TaskScannerService _scanner;

    public OrchestratorTaskPromptContextComposer(TaskScannerService scanner)
    {
        _scanner = scanner;
    }

    /// <summary>
    /// Returns null outside task scope. A task scope that cannot be resolved is
    /// an error so callers can log the failed lookup instead of silently
    /// presenting a project-only snapshot as complete task context.
    /// </summary>
    public OrchestratorTaskPromptContext? Compose(
        string projectName,
        string watchPath,
        ChatNavigationContext? navigation,
        OrchestratorContextKey? routeContext)
    {
        var identity = ResolveTaskIdentity(navigation, routeContext);
        if (identity == null) return null;

        var detail = _scanner.GetJobDetail(identity, watchPath);
        if (detail == null
            || !string.Equals(detail.Info.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
        {
            throw new KeyNotFoundException(
                $"Unknown task '{identity}' in project '{projectName}'.");
        }

        return Compose(detail);
    }

    internal static string? ResolveTaskIdentity(
        ChatNavigationContext? navigation,
        OrchestratorContextKey? routeContext)
    {
        if (!string.IsNullOrWhiteSpace(navigation?.CurrentTaskKey))
            return navigation.CurrentTaskKey.Trim();
        if (routeContext?.Kind == OrchestratorContextKey.TaskKind
            && !string.IsNullOrWhiteSpace(routeContext.TaskKey))
            return routeContext.TaskKey.Trim();
        if (!string.IsNullOrWhiteSpace(navigation?.CurrentTaskId))
            return navigation.CurrentTaskId.Trim();
        return null;
    }

    internal static OrchestratorTaskPromptContext Compose(TaskDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var info = detail.Info;
        var taskKey = string.IsNullOrWhiteSpace(info.Key)
            ? (string.IsNullOrWhiteSpace(info.TaskKey) ? info.Id : info.TaskKey)
            : info.Key;
        var included = new List<string> { "task metadata" };
        var blocks = new List<string>
        {
            RenderBoundedBlock(
                "TASK METADATA",
                $"Key: {taskKey}\nTitle: {info.Title}\nLane: {info.State}",
                MetadataTokenLimit)
        };

        if (!string.IsNullOrWhiteSpace(detail.PromptMarkdown))
        {
            included.Add("prompt.md");
            blocks.Add(RenderBoundedBlock("prompt.md", detail.PromptMarkdown, PromptTokenLimit));
        }

        if (!string.IsNullOrWhiteSpace(detail.StatusMarkdown))
        {
            included.Add("status.md");
            blocks.Add(RenderBoundedBlock("status.md", detail.StatusMarkdown, StatusTokenLimit));
        }

        included.Add("last run outcome");
        blocks.Add(RenderBoundedBlock(
            "LAST RUN OUTCOME",
            RenderLastRunOutcome(info),
            RunOutcomeTokenLimit));

        var sb = new StringBuilder();
        sb.AppendLine("=== ACTIVE TASK CONTEXT ===");
        sb.AppendLine("The operator is asking from this task. Treat this task block as the authoritative task scope for the current message.");
        sb.AppendLine($"Included context blocks: {string.Join(", ", included)}.");
        if (string.IsNullOrWhiteSpace(detail.PromptMarkdown))
            sb.AppendLine("prompt.md: missing or empty, so no prompt.md content was included.");
        if (string.IsNullOrWhiteSpace(detail.StatusMarkdown))
            sb.AppendLine("status.md: missing or empty, so no status.md content was included.");
        sb.AppendLine();
        foreach (var block in blocks)
        {
            sb.AppendLine(block);
            sb.AppendLine();
        }

        return new OrchestratorTaskPromptContext(taskKey, sb.ToString().TrimEnd(), included);
    }

    internal static string RenderBoundedBlock(string label, string content, int tokenLimit)
    {
        var normalized = content.Trim();
        var characterLimit = tokenLimit * EstimatedCharactersPerToken;
        var truncated = normalized.Length > characterLimit;
        var bounded = truncated
            ? normalized[..Math.Max(0, characterLimit - 20)].TrimEnd() + "\n[content truncated]"
            : normalized;
        return $"--- {label} (limit: {tokenLimit.ToString(CultureInfo.InvariantCulture)} estimated tokens; truncated: {(truncated ? "yes" : "no")}) ---\n{bounded}\n--- END {label} ---";
    }

    private static string RenderLastRunOutcome(TaskInfo info)
    {
        var execution = info.Execution;
        if (execution == null && info.OutcomeIssue == null)
            return "No run outcome is recorded for this task.";

        var lines = new List<string>();
        if (execution != null)
        {
            lines.Add($"Status: {ValueOrUnknown(execution.Status)}");
            lines.Add($"Terminal outcome: {ValueOrUnknown(execution.RunOutcome)}");
            lines.Add($"Started at: {execution.StartedAt.ToUniversalTime():O}");
            if (execution.ExitCode != null)
                lines.Add($"Exit code: {execution.ExitCode.Value.ToString(CultureInfo.InvariantCulture)}");
            if (execution.DurationSeconds != null)
                lines.Add($"Duration seconds: {execution.DurationSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
        }
        if (info.OutcomeIssue != null)
        {
            lines.Add($"Outcome issue kind: {ValueOrUnknown(info.OutcomeIssue.Kind)}");
            lines.Add($"Outcome issue: {ValueOrUnknown(info.OutcomeIssue.Summary)}");
        }
        return string.Join("\n", lines);
    }

    private static string ValueOrUnknown(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
}

public sealed record OrchestratorTaskPromptContext(
    string TaskKey,
    string PromptBlock,
    IReadOnlyList<string> IncludedBlocks);
