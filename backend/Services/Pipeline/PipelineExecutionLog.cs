using System.Collections.Concurrent;
using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Pipeline;

/// <summary>
/// Per-job append-mostly writer for <c>pipeline-execution.json</c>. One
/// record per pipeline run, with one entry per step inside it. The file
/// lives next to <c>aspect-*.md</c> in the job folder so consumers
/// (Overview tab, future Pipeline View, audit tooling) read the same
/// folder they already read for the per-aspect MD reports.
///
/// Concurrency model: aspect steps run in parallel and finish in
/// non-deterministic order, so the log holds an in-memory record per
/// (jobFolder, pipelineId) keyed pair under a lock; finishing a step
/// merges into that record and rewrites the file atomically. Failures
/// to write are logged and swallowed - the persistence is observability,
/// not a state-machine input.
/// </summary>
public sealed class PipelineExecutionLog
{
    public const string FileName = "pipeline-execution.json";

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<PipelineExecutionLog> _logger;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

    public PipelineExecutionLog(ILogger<PipelineExecutionLog> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start a new pipeline run record in the job folder. Overwrites any
    /// existing file - a re-run is a new pipeline execution. Steps are
    /// pre-populated from the supplied <see cref="TaskPipeline"/> in
    /// <see cref="PipelineStepStatus.Pending"/> / <see cref="PipelineStepStatus.Planned"/>
    /// state so the file is observable from t=0.
    /// </summary>
    public PipelineExecutionRecord Begin(
        string jobFolderPath,
        TaskPipeline pipeline,
        string project,
        string jobId,
        DateTime? nowUtc = null)
    {
        var record = BuildFresh(pipeline, project, jobId, nowUtc ?? DateTime.UtcNow);
        WriteAtomic(jobFolderPath, record);
        return record;
    }

    /// <summary>
    /// Return the in-flight execution record for this job, or begin a fresh
    /// one when none exists yet or the prior run already completed. Unlike
    /// <see cref="Begin"/> (which always overwrites), this preserves an
    /// in-progress record so a step recorded by an earlier stage of the SAME
    /// run is not clobbered: the core agent run marks its step
    /// <see cref="PipelineStepStatus.Running"/> at spawn and
    /// <see cref="PipelineStepStatus.Passed"/> / <see cref="PipelineStepStatus.Failed"/>
    /// at exit (in <c>ProjectRunner</c>), then the later aspect stage opens
    /// the same file in <c>ReviewDecisionOrchestrator</c>; without this the
    /// aspect stage's <see cref="Begin"/> reset the core step back to
    /// <see cref="PipelineStepStatus.Pending"/> and it never showed as done.
    ///
    /// A prior record that is already
    /// <see cref="PipelineExecutionRecord.IsComplete"/> (or that belongs to a
    /// different pipeline / job) is treated as a finished run, so the next
    /// call starts a new execution. That makes a pipeline re-run / re-issue
    /// observable as a fresh record with a new <see cref="PipelineExecutionRecord.StartedAt"/>
    /// rather than a silent overwrite of the previous run's values.
    /// </summary>
    public PipelineExecutionRecord EnsureRun(
        string jobFolderPath,
        TaskPipeline pipeline,
        string project,
        string jobId,
        DateTime? nowUtc = null)
    {
        var lockObj = _locks.GetOrAdd(NormalizeKey(jobFolderPath), _ => new object());
        lock (lockObj)
        {
            var current = TryRead(jobFolderPath);
            if (current != null
                && !current.IsComplete
                && string.Equals(current.PipelineId, pipeline.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.JobId, jobId, StringComparison.Ordinal))
            {
                return current;
            }
            var record = BuildFresh(pipeline, project, jobId, nowUtc ?? DateTime.UtcNow);
            WriteAtomic(jobFolderPath, record);
            return record;
        }
    }

    private static PipelineExecutionRecord BuildFresh(
        TaskPipeline pipeline,
        string project,
        string jobId,
        DateTime started)
    {
        var steps = new List<PipelineStepExecution>();
        foreach (var step in pipeline.AllSteps)
        {
            steps.Add(new PipelineStepExecution
            {
                StepId = step.Id,
                Kind = step.Kind,
                Model = step.Model,
                Status = step.Stub ? PipelineStepStatus.Planned : PipelineStepStatus.Pending,
            });
        }
        return new PipelineExecutionRecord
        {
            PipelineId = pipeline.Id,
            PipelineVersion = pipeline.Version,
            Project = project,
            JobId = jobId,
            StartedAt = started,
            Steps = steps,
        };
    }

    /// <summary>
    /// Record one step's outcome (or in-flight start). Merges into the
    /// in-memory record under a per-folder lock, rewrites the file. Safe
    /// to call from concurrent parallel step tasks.
    /// </summary>
    public void RecordStep(string jobFolderPath, PipelineStepExecution stepResult)
    {
        var lockObj = _locks.GetOrAdd(NormalizeKey(jobFolderPath), _ => new object());
        lock (lockObj)
        {
            var current = TryRead(jobFolderPath);
            if (current == null) return;

            var updatedSteps = new List<PipelineStepExecution>(current.Steps.Count);
            var replaced = false;
            foreach (var existing in current.Steps)
            {
                if (!replaced && string.Equals(existing.StepId, stepResult.StepId, StringComparison.OrdinalIgnoreCase))
                {
                    updatedSteps.Add(stepResult);
                    replaced = true;
                }
                else
                {
                    updatedSteps.Add(existing);
                }
            }
            if (!replaced) updatedSteps.Add(stepResult);

            WriteAtomic(jobFolderPath, current with { Steps = updatedSteps });
        }
    }

    /// <summary>
    /// Stamp <see cref="PipelineExecutionRecord.CompletedAt"/> on the
    /// record so the UI can show "ran for X" instead of "still running".
    /// </summary>
    public void Complete(string jobFolderPath, DateTime? nowUtc = null)
    {
        var lockObj = _locks.GetOrAdd(NormalizeKey(jobFolderPath), _ => new object());
        lock (lockObj)
        {
            var current = TryRead(jobFolderPath);
            if (current == null) return;
            WriteAtomic(jobFolderPath, current with { CompletedAt = nowUtc ?? DateTime.UtcNow });
        }
    }

    /// <summary>
    /// Read the current execution record for the job, or null if no
    /// pipeline run has been recorded yet.
    /// </summary>
    public PipelineExecutionRecord? Read(string jobFolderPath) => TryRead(jobFolderPath);

    private PipelineExecutionRecord? TryRead(string jobFolderPath)
    {
        var path = Path.Combine(jobFolderPath, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PipelineExecutionRecord>(json, ReadOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PipelineExecutionLog: failed to read {Path}", path);
            return null;
        }
    }

    private void WriteAtomic(string jobFolderPath, PipelineExecutionRecord record)
    {
        try
        {
            if (!Directory.Exists(jobFolderPath))
            {
                Directory.CreateDirectory(jobFolderPath);
            }
            var path = Path.Combine(jobFolderPath, FileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(record, WriteOpts));
            // Replace via move so a partial write never leaves a truncated
            // pipeline-execution.json on disk for the next reader.
            if (File.Exists(path))
            {
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PipelineExecutionLog: failed to persist record for {Folder}", jobFolderPath);
        }
    }

    private static string NormalizeKey(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
