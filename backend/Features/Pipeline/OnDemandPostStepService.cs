using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    private readonly IManagedProjectArtifactCommitService? _managedArtifacts;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _runGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _planGates =
        new(StringComparer.OrdinalIgnoreCase);

    public OnDemandPostStepService(
        WikiMaintenancePostStepRunner maintenance,
        WikiLearningsPostStepRunner learnings,
        AgentsWikiSyncPostStepRunner agentsWiki,
        IJsonlAppender jsonl,
        IManagedProjectArtifactCommitService? managedArtifacts = null)
    {
        _maintenance = maintenance;
        _learnings = learnings;
        _agentsWiki = agentsWiki;
        _jsonl = jsonl;
        _managedArtifacts = managedArtifacts;
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
        var planKey = Path.GetFullPath(jobFolderPath);
        lock (_planGates.GetOrAdd(planKey, static _ => new object()))
        {
            var ids = ReadPlan(jobFolderPath).Append(stepId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var dir = Path.Combine(jobFolderPath, ".metadata");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, PlanFileName);
            var temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new CardPostStepPlan(ids), JsonOptions));
            File.Move(temp, target, overwrite: true);
        }
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
        string projectId,
        string stepId,
        bool addToCard,
        CancellationToken ct)
    {
        if (!IsSupported(stepId))
            throw new NotSupportedException($"Post-step '{stepId}' does not support on-demand execution.");

        var gateKey = $"{Path.GetFullPath(task.FolderPath)}::{stepId}";
        var gate = _runGates.GetOrAdd(gateKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (addToCard) AddToPlan(task.FolderPath, stepId);
            var attempt = ReserveAttempt(task.FolderPath, stepId);
            var started = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();

            ManagedProjectArtifactOutput RunWriter()
            {
                switch (stepId)
                {
                    case PipelineCatalogue.WikiMaintenanceStepId:
                    {
                        var result = _maintenance.Run(task, entry, started);
                        var status = result.Verdict == WikiMaintenanceVerdict.Error ? "Failed"
                            : result.Verdict == WikiMaintenanceVerdict.Skipped ? "Skipped" : "Ok";
                        var artifact = result.Slug is null ? null : $"docs/wiki/common-problems/{result.Slug}/README.md";
                        return new ManagedProjectArtifactOutput(status, result.Reason, artifact);
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
                        var status = result.Verdict == WikiLearningsVerdict.Error ? "Failed"
                            : result.Verdict == WikiLearningsVerdict.Skipped ? "Skipped" : "Ok";
                        var artifact = result.Slug is null ? null : $"docs/wiki/learnings/{result.Slug}.md";
                        return new ManagedProjectArtifactOutput(status, result.Reason, artifact);
                    }
                    default:
                    {
                        var result = _agentsWiki.Run(task, entry, changedFiles: null, nowUtc: started);
                        var status = result.Verdict == AgentsWikiSyncVerdict.Error ? "Failed"
                            : result.Verdict == AgentsWikiSyncVerdict.Skipped ? "Skipped" : "Ok";
                        return new ManagedProjectArtifactOutput(
                            status, result.Reason, AgentsWikiSyncPostStepRunner.IndexRepoRel);
                    }
                }
            }

            ManagedProjectArtifactOutput output;
            try
            {
                if (_managedArtifacts is null)
                {
                    output = RunWriter();
                }
                else
                {
                    var durable = await _managedArtifacts.ExecuteAsync(task, stepId, RunWriter, ct);
                    output = durable.Output ?? new ManagedProjectArtifactOutput(
                        "Failed", durable.Error ?? "managed commit/push boundary failed", null);
                    if (!durable.Success)
                    {
                        output = output with
                        {
                            Status = "Failed",
                            Summary = string.IsNullOrWhiteSpace(durable.Error)
                                ? output.Summary
                                : $"{output.Summary}. Durability: {durable.Error}",
                        };
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                output = new ManagedProjectArtifactOutput("Failed", ex.Message, null);
            }

            sw.Stop();
            var resultRel = Path.Combine("results", "post-steps", $"{stepId}-attempt-{attempt:000}.md")
                .Replace(Path.DirectorySeparatorChar, '/');
            var resultPath = Path.Combine(task.FolderPath, resultRel.Replace('/', Path.DirectorySeparatorChar));
            await WriteImmutableResultAsync(
                resultPath,
                $"# {stepId} attempt {attempt}\n\n" +
                $"- Status: {output.Status}\n" +
                $"- Started: {started:O}\n" +
                $"- Duration: {sw.ElapsedMilliseconds} ms\n" +
                $"- Produced artifact: {output.ProducedArtifact ?? "none"}\n\n" +
                $"{output.Summary}\n",
                ct);

            var row = CreateAttemptRow(
                task,
                projectId,
                stepId,
                PipelineCatalogue.Standard.Post.First(step => step.Id == stepId).DisplayName,
                "Script",
                "Scripted",
                attempt,
                output.Status,
                started,
                DateTime.UtcNow,
                sw.ElapsedMilliseconds,
                output.Summary,
                resultRel);
            await AppendRowAsync(task.FolderPath, row, ct);
            return row;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Records an externally executed on-demand step (currently quality grade)
    /// through the same durable attempt allocator and schema-aligned row builder.
    /// </summary>
    public async Task<OnDemandStepAttempt> AppendAttemptAsync(
        TaskInfo task,
        string projectId,
        string stepId,
        string stepLabel,
        string stepType,
        string orchestratorReaction,
        string status,
        DateTime startedAt,
        DateTime finishedAt,
        long durationMs,
        string summary,
        string? artifactRef,
        CancellationToken ct)
    {
        var gateKey = $"{Path.GetFullPath(task.FolderPath)}::{stepId}";
        var gate = _runGates.GetOrAdd(gateKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var attempt = ReserveAttempt(task.FolderPath, stepId);
            var row = CreateAttemptRow(
                task, projectId, stepId, stepLabel, stepType, orchestratorReaction,
                attempt, status, startedAt, finishedAt, durationMs, summary, artifactRef);
            await AppendRowAsync(task.FolderPath, row, ct);
            return row;
        }
        finally
        {
            gate.Release();
        }
    }

    internal int ReserveAttempt(string jobFolderPath, string stepId)
    {
        var reservations = Path.Combine(jobFolderPath, ".metadata", "post-step-attempts");
        Directory.CreateDirectory(reservations);
        var resultFolder = Path.Combine(jobFolderPath, "results", "post-steps");
        var existingRows = ReadAttempts(jobFolderPath)
            .Where(row => string.Equals(row.StepId, stepId, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.Attempt)
            .ToHashSet();

        for (var attempt = 1; ; attempt++)
        {
            var resultPath = Path.Combine(resultFolder, $"{stepId}-attempt-{attempt:000}.md");
            if (existingRows.Contains(attempt) || File.Exists(resultPath)) continue;

            var reservation = Path.Combine(reservations, $"{stepId}-attempt-{attempt:000}.reservation");
            try
            {
                using var stream = new FileStream(
                    reservation, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256, FileOptions.WriteThrough);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write($"{DateTime.UtcNow:O}\n");
                return attempt;
            }
            catch (IOException ex) when (File.Exists(reservation))
            {
                // Another request or process reserved this attempt first.
                SilentCatch.Note(ex, "OnDemandPostStepService: attempt reservation already exists.");
            }
        }
    }

    internal static string CreateRowId(string jobKey, string stepId, int attempt)
    {
        var material = $"{jobKey}\n{stepId}\n{attempt}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static string CreateJobKey(string projectId, string jobId) => $"{projectId}::{jobId}";

    private static OnDemandStepAttempt CreateAttemptRow(
        TaskInfo task,
        string projectId,
        string stepId,
        string stepLabel,
        string stepType,
        string orchestratorReaction,
        int attempt,
        string status,
        DateTime startedAt,
        DateTime finishedAt,
        long durationMs,
        string summary,
        string? artifactRef)
    {
        var jobKey = CreateJobKey(projectId, task.Id);
        return new OnDemandStepAttempt(
            Id: CreateRowId(jobKey, stepId, attempt),
            ProjectId: projectId,
            JobId: task.Id,
            JobKey: jobKey,
            PipelineDefVersion: PipelineCatalogue.Standard.Version,
            StepId: stepId,
            StepLabel: stepLabel,
            Phase: "Post",
            StepType: stepType,
            FailureMode: "Soft",
            OrchestratorReaction: orchestratorReaction,
            Attempt: attempt,
            Status: status,
            StartedAt: startedAt,
            FinishedAt: finishedAt,
            DurationMs: durationMs,
            Summary: summary,
            ArtifactRef: artifactRef);
    }

    private async Task AppendRowAsync(string jobFolderPath, OnDemandStepAttempt row, CancellationToken ct)
        => await _jsonl.AppendAsync(Path.Combine(jobFolderPath, "logs", AttemptsFileName), row, ct: ct);

    private static async Task WriteImmutableResultAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), ct);
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
