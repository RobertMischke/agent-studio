using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Persistence;

namespace AgentStudio.Pipeline;

/// <summary>
/// Card-owned planning and append-only execution for implemented, idempotent
/// post steps. This is deliberately catalogue-bounded: it cannot introduce an
/// arbitrary executable into a task.
/// </summary>
public sealed class OnDemandPostStepService
{
    public const string PlanFileName = "card-pipeline-steps.json";
    public const string AttemptsFileName = "step-runs.jsonl";

    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        PipelineCatalogue.WikiMaintenanceStepId,
        PipelineCatalogue.WikiLearningsStepId,
        PipelineCatalogue.AgentsWikiSyncStepId,
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly WikiMaintenancePostStepRunner _maintenance;
    private readonly WikiLearningsPostStepRunner _learnings;
    private readonly AgentsWikiSyncPostStepRunner _agentsWiki;
    private readonly IJsonlAppender _jsonl;

    public OnDemandPostStepService(
        WikiMaintenancePostStepRunner maintenance,
        WikiLearningsPostStepRunner learnings,
        AgentsWikiSyncPostStepRunner agentsWiki,
        IJsonlAppender jsonl)
    {
        _maintenance = maintenance;
        _learnings = learnings;
        _agentsWiki = agentsWiki;
        _jsonl = jsonl;
    }

    public static bool IsSupported(string stepId) => Supported.Contains(stepId);

    public IReadOnlyList<string> ReadPlan(string jobFolderPath)
    {
        var path = Path.Combine(jobFolderPath, ".metadata", PlanFileName);
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<CardPostStepPlan>(File.ReadAllText(path), JsonOptions)?.StepIds
                ?.Where(IsSupported).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void AddToPlan(string jobFolderPath, string stepId)
    {
        if (!IsSupported(stepId)) throw new ArgumentOutOfRangeException(nameof(stepId));
        var ids = ReadPlan(jobFolderPath).Append(stepId)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var dir = Path.Combine(jobFolderPath, ".metadata");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, PlanFileName);
        var temp = target + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new CardPostStepPlan(ids), JsonOptions));
        File.Move(temp, target, overwrite: true);
    }

    public IReadOnlyList<OnDemandStepAttempt> ReadAttempts(string jobFolderPath)
    {
        var path = Path.Combine(jobFolderPath, "logs", AttemptsFileName);
        if (!File.Exists(path)) return [];
        var rows = new List<OnDemandStepAttempt>();
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                var row = JsonSerializer.Deserialize<OnDemandStepAttempt>(line, JsonOptions);
                if (row is not null) rows.Add(row);
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "OnDemandPostStepService: skip an unreadable step-attempt row.");
            }
        }
        return rows;
    }

    public async Task<OnDemandStepAttempt> RunAsync(
        TaskInfo task,
        WatchPathEntry entry,
        string stepId,
        bool addToCard,
        CancellationToken ct)
    {
        if (!IsSupported(stepId))
            throw new NotSupportedException($"Post-step '{stepId}' does not support on-demand execution.");

        if (addToCard) AddToPlan(task.FolderPath, stepId);
        var prior = ReadAttempts(task.FolderPath).Count(row =>
            string.Equals(row.StepId, stepId, StringComparison.OrdinalIgnoreCase));
        var attempt = prior + 1;
        var started = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        string status;
        string summary;
        string? artifactRef = null;

        try
        {
            switch (stepId)
            {
                case PipelineCatalogue.WikiMaintenanceStepId:
                {
                    var result = _maintenance.Run(task, entry, started);
                    status = result.Verdict == WikiMaintenanceVerdict.Error ? "Failed"
                        : result.Verdict == WikiMaintenanceVerdict.Skipped ? "Skipped" : "Ok";
                    summary = result.Reason;
                    artifactRef = result.Slug is null ? null : $"docs/wiki/common-problems/{result.Slug}/README.md";
                    break;
                }
                case PipelineCatalogue.WikiLearningsStepId:
                {
                    var evidence = new WikiLearningsRun(
                        Verdict: "on-demand",
                        VerdictReason: "Operator-triggered post-step",
                        Findings: [],
                        AgentNotes: null,
                        StumblingBlock: task.OutcomeIssue?.Summary,
                        ChangedSummary: null);
                    var result = _learnings.Run(task, entry, evidence, started);
                    status = result.Verdict == WikiLearningsVerdict.Error ? "Failed"
                        : result.Verdict == WikiLearningsVerdict.Skipped ? "Skipped" : "Ok";
                    summary = result.Reason;
                    artifactRef = result.Slug is null ? null : $"docs/wiki/learnings/{result.Slug}.md";
                    break;
                }
                default:
                {
                    var result = _agentsWiki.Run(task, entry, changedFiles: null, nowUtc: started);
                    status = result.Verdict == AgentsWikiSyncVerdict.Error ? "Failed"
                        : result.Verdict == AgentsWikiSyncVerdict.Skipped ? "Skipped" : "Ok";
                    summary = result.Reason;
                    artifactRef = AgentsWikiSyncPostStepRunner.IndexRepoRel;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = "Failed";
            summary = ex.Message;
        }

        sw.Stop();
        var producedArtifact = artifactRef;
        var resultRel = Path.Combine("results", "post-steps", $"{stepId}-attempt-{attempt:000}.md")
            .Replace(Path.DirectorySeparatorChar, '/');
        var resultPath = Path.Combine(task.FolderPath, resultRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        await File.WriteAllTextAsync(resultPath,
            $"# {stepId} attempt {attempt}\n\n" +
            $"- Status: {status}\n" +
            $"- Started: {started:O}\n" +
            $"- Duration: {sw.ElapsedMilliseconds} ms\n" +
            $"- Produced artifact: {producedArtifact ?? "none"}\n\n" +
            $"{summary}\n", ct);
        artifactRef = resultRel;
        var row = new OnDemandStepAttempt(
            Id: $"{task.Id}:{stepId}:{attempt}",
            ProjectId: entry.Name,
            JobId: task.Id,
            JobKey: TaskIdentity.CreateKey(entry.Path, task.Id),
            PipelineDefVersion: PipelineCatalogue.Standard.Version,
            StepId: stepId,
            StepLabel: PipelineCatalogue.Standard.Post.First(step => step.Id == stepId).DisplayName,
            Phase: "Post",
            StepType: "Script",
            FailureMode: "Soft",
            OrchestratorReaction: "Scripted",
            Attempt: attempt,
            Status: status,
            StartedAt: started,
            FinishedAt: DateTime.UtcNow,
            DurationMs: sw.ElapsedMilliseconds,
            Summary: summary,
            ArtifactRef: artifactRef);
        await _jsonl.AppendAsync(Path.Combine(task.FolderPath, "logs", AttemptsFileName), row, ct: ct);
        return row;
    }

    private sealed record CardPostStepPlan(IReadOnlyList<string> StepIds);
}

public sealed record OnDemandStepAttempt(
    string Id,
    string ProjectId,
    string JobId,
    string JobKey,
    int PipelineDefVersion,
    string StepId,
    string StepLabel,
    string Phase,
    string StepType,
    string FailureMode,
    string OrchestratorReaction,
    int Attempt,
    string Status,
    DateTime StartedAt,
    DateTime FinishedAt,
    long DurationMs,
    string Summary,
    string? ArtifactRef);
