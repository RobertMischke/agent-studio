using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

public enum TaskComplexity
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// Replaceable economy seam. The built-in implementation ranks the live CLI
/// catalogue; a TokenEconomy adapter can implement the same contract once its
/// package is available without changing runner or persistence code.
/// </summary>
public interface IModelEconomyAdvisor
{
    ModelEconomySuggestion Suggest(
        IReadOnlyList<CliModelInfo> availableModels,
        TaskComplexity complexity);
}

public sealed record ModelEconomySuggestion(
    string Model,
    string? ThinkingLevel,
    int EstimatedSavingsPercent,
    string Basis);

/// <summary>
/// Local, zero-token fallback for TokenEconomy.SuggestModel. Model ids and
/// reasoning ladders come exclusively from the CLI catalogue.
/// </summary>
public sealed class CatalogueModelEconomyAdvisor : IModelEconomyAdvisor
{
    public ModelEconomySuggestion Suggest(
        IReadOnlyList<CliModelInfo> availableModels,
        TaskComplexity complexity)
    {
        var models = availableModels.Where(model => model.Available && !model.Deprecated).ToList();
        if (models.Count == 0) throw new InvalidOperationException("The CLI reported no available models.");

        // CLI catalogues are already capability ordered (best/default first).
        // Select a rung, never a concrete id, so newly discovered models enter
        // the ladder without a Studio release.
        var index = complexity switch
        {
            TaskComplexity.Large => 0,
            TaskComplexity.Medium => Math.Min(models.Count - 1, models.Count / 2),
            _ => models.Count - 1,
        };
        var selected = models[index];
        var levels = selected.ThinkingLevels ?? [];
        var levelIndex = complexity switch
        {
            TaskComplexity.Large => Math.Max(0, levels.Count - 1),
            TaskComplexity.Medium => Math.Max(0, levels.Count / 2),
            _ => 0,
        };
        var level = levels.Count == 0 ? selected.DefaultThinkingLevel : levels[levelIndex];
        var savings = models.Count <= 1 || index == 0
            ? 0
            : Math.Clamp((int)Math.Round(65d * index / (models.Count - 1)), 10, 65);
        return new ModelEconomySuggestion(
            selected.Id,
            level,
            savings,
            "live CLI model ladder + local economy policy");
    }
}

public sealed record ModelQualificationDecision
{
    public DateTime At { get; init; }
    public string Event { get; init; } = "decision";
    public string DecisionId { get; init; } = Guid.NewGuid().ToString("N");
    public string JobId { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string CliType { get; init; } = string.Empty;
    public string TaskType { get; init; } = string.Empty;
    public string Surface { get; init; } = string.Empty;
    public string Complexity { get; init; } = string.Empty;
    public int Score { get; init; }
    public string RecommendedModel { get; init; } = string.Empty;
    public string? RecommendedThinkingLevel { get; init; }
    public string SelectedModel { get; init; } = string.Empty;
    public string? SelectedThinkingLevel { get; init; }
    public string SelectionSource { get; init; } = string.Empty;
    public int EstimatedSavingsPercent { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string CatalogueSource { get; init; } = string.Empty;
}

public sealed record ModelQualificationOutcome
{
    public DateTime At { get; init; }
    public string Event { get; init; } = "outcome";
    public string JobId { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Verdict { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public int Attempt { get; init; }
}

public sealed class ModelQualificationService
{
    public const string LogFileName = "model-qualification.jsonl";

    private static readonly Regex ArchitectureSignal = new(
        @"\b(architect|backend|orchestrat|pipeline|concurr|state machine|schema|migration|security|permission|cross-project|api contract)\w*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FrontendPolishSignal = new(
        @"\b(ui polish|frontend polish|spacing|tooltip|colour|color|alignment|copy change|css|scss|pixel)\w*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CrossCuttingSignal = new(
        @"\b(frontend|backend|runner|cli|database|docs)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IModelEconomyAdvisor _economy;
    private readonly IJsonlAppender _jsonl;
    private readonly ILogger<ModelQualificationService> _logger;

    public ModelQualificationService(
        IModelEconomyAdvisor economy,
        IJsonlAppender jsonl,
        ILogger<ModelQualificationService> logger)
    {
        _economy = economy;
        _jsonl = jsonl;
        _logger = logger;
    }

    public ModelQualificationDecision Qualify(
        TaskInfo task,
        string prompt,
        CliModelCatalog catalogue,
        IReadOnlyList<TaskInfo> projectHistory,
        DateTime? nowUtc = null)
    {
        var text = $"{task.Title}\n{prompt}";
        var score = task.TaskType switch
        {
            TaskTypes.Feature => 1,
            TaskTypes.Bug => 1,
            _ => 0,
        };
        if (prompt.Length > 4_000) score += 2;
        else if (prompt.Length > 1_500) score += 1;

        var architectureHits = ArchitectureSignal.Matches(text).Count;
        var polishHits = FrontendPolishSignal.Matches(text).Count;
        score += Math.Min(3, architectureHits);
        if (polishHits > 0 && architectureHits == 0) score -= 2;
        var areas = CrossCuttingSignal.Matches(text)
            .Select(match => match.Value.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (areas >= 3) score += 2;

        var similarAttempts = projectHistory
            .Where(other => other.Id != task.Id && other.TaskType == task.TaskType)
            .Select(other => ReadAttemptCount(other.FolderPath))
            .Where(attempts => attempts > 1)
            .Take(3)
            .ToList();
        if (similarAttempts.Count >= 2) score += 1;

        var complexity = score >= 4 ? TaskComplexity.Large : score >= 1 ? TaskComplexity.Medium : TaskComplexity.Small;
        var surface = architectureHits > 0
            ? (areas >= 3 ? "cross-cutting architecture" : "backend/architecture")
            : polishHits > 0 ? "frontend polish" : "general";
        var recommendation = _economy.Suggest(catalogue.Models, complexity);

        var modelExplicit = task.ModelExplicit;
        var thinkingExplicit = task.ThinkingLevelExplicit;
        var selectedModel = modelExplicit && !string.IsNullOrWhiteSpace(task.Model)
            ? task.Model!
            : recommendation.Model;
        var selectedThinking = thinkingExplicit
            ? task.ThinkingLevel
            : string.Equals(selectedModel, recommendation.Model, StringComparison.OrdinalIgnoreCase)
                ? recommendation.ThinkingLevel
                : ModelMetadataRegistry.ResolveThinkingLevel(task.CliType, selectedModel, task.ThinkingLevel);
        var source = modelExplicit || thinkingExplicit ? "task-override" : "qualification";
        var reason = $"{task.TaskType}/{complexity.ToString().ToLowerInvariant()}/{surface}; score {score}; " +
                     $"recommend {recommendation.Model} at {recommendation.ThinkingLevel ?? "model default"}; " +
                     (source == "task-override"
                         ? $"card override wins, selected {selectedModel} at {selectedThinking ?? "model default"}"
                         : $"selected recommendation; expected saving about {recommendation.EstimatedSavingsPercent}% vs top rung");

        return new ModelQualificationDecision
        {
            At = nowUtc ?? DateTime.UtcNow,
            JobId = task.Id,
            Project = task.ProjectName,
            CliType = CliTypes.Normalize(task.CliType),
            TaskType = task.TaskType,
            Surface = surface,
            Complexity = complexity.ToString().ToLowerInvariant(),
            Score = score,
            RecommendedModel = recommendation.Model,
            RecommendedThinkingLevel = recommendation.ThinkingLevel,
            SelectedModel = selectedModel,
            SelectedThinkingLevel = selectedThinking,
            SelectionSource = source,
            EstimatedSavingsPercent = source == "qualification" ? recommendation.EstimatedSavingsPercent : 0,
            Reason = reason,
            CatalogueSource = catalogue.Source ?? "unknown",
        };
    }

    public async Task RecordDecisionAsync(string jobFolder, ModelQualificationDecision decision, CancellationToken ct)
    {
        try
        {
            await _jsonl.AppendAsync(Path.Combine(jobFolder, LogFileName), decision, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "model-qualification decision log failed for {JobId}", decision.JobId);
        }
    }

    public async Task RecordOutcomeAsync(string jobFolder, ModelQualificationOutcome outcome)
    {
        try
        {
            await _jsonl.AppendAsync(Path.Combine(jobFolder, LogFileName), outcome);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "model-qualification outcome log failed for {JobId}", outcome.JobId);
        }
    }

    private static int ReadAttemptCount(string folder)
    {
        try
        {
            var path = Path.Combine(folder, PipelineExecutionLog.FileName);
            if (!File.Exists(path)) return 0;
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty("attempt", out var attempt) && attempt.TryGetInt32(out var value)
                ? value
                : 1;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "ModelQualificationService: similar-task attempt history is best-effort");
            return 0;
        }
    }
}
