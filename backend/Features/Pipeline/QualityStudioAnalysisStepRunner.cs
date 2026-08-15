using System.Diagnostics;

namespace AgentStudio.Pipeline;

public sealed record QualityStudioStepOutcome(
    string StepId,
    PipelineStepStatus Status,
    int FindingCount,
    string? ArtifactPath,
    string? Reason);

public sealed record QualityStudioAnalysisRunOutcome(
    QualityStudioAnalysisSelection Selection,
    IReadOnlyList<QualityStudioStepOutcome> Steps,
    bool RequiresSteeredRetry,
    bool DependencyUnavailable);

/// <summary>
/// Coordinates the Agent Studio side of the in-process Quality Studio bracket.
/// It has no HTTP or process boundary. A typed package adapter supplies
/// <see cref="IQualityStudioAnalysisCore"/> once the QS package is published.
/// </summary>
public sealed class QualityStudioAnalysisStepRunner
{
    private readonly IQualityStudioAnalysisCore _core;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly ILogger<QualityStudioAnalysisStepRunner> _logger;

    public QualityStudioAnalysisStepRunner(
        IQualityStudioAnalysisCore core,
        PipelineExecutionLog pipelineLog,
        ILogger<QualityStudioAnalysisStepRunner> logger)
    {
        _core = core;
        _pipelineLog = pipelineLog;
        _logger = logger;
    }

    public async Task<QualityStudioAnalysisRunOutcome> RunAsync(
        string repositoryPath,
        string jobFolder,
        TaskInfo task,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolder);
        ArgumentNullException.ThrowIfNull(task);

        var changedPaths = task.Commits
            .Where(commit => !TaskCommitSupersession.IsSuperseded(commit))
            .SelectMany(commit => commit.Files)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selection = QualityStudioAnalysisPolicy.Resolve(repositoryPath, changedPaths);
        var outcomes = new List<QualityStudioStepOutcome>();
        var requiresRetry = false;

        foreach (var stepId in PipelineCatalogue.QualityAnalysisStepIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!selection.Runs(stepId))
            {
                const string reason = "Not selected by the repository Quality Studio policy for this card class.";
                Record(jobFolder, stepId, PipelineStepStatus.NotApplicable, 0, null, reason, 0);
                outcomes.Add(new QualityStudioStepOutcome(
                    stepId, PipelineStepStatus.NotApplicable, 0, null, reason));
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            QualityStudioAnalysisResult result;
            try
            {
                result = await _core.RunAsync(new QualityStudioAnalysisRequest(
                    Path.GetFullPath(repositoryPath),
                    stepId,
                    changedPaths,
                    selection.RuleProfiles,
                    selection.RuleConfigurationPath), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                stopwatch.Stop();
                var reason = $"Quality Studio in-process analysis failed: {exception.Message}";
                Record(jobFolder, stepId, PipelineStepStatus.Failed, 0, null, reason, stopwatch.ElapsedMilliseconds);
                _logger.LogWarning(exception,
                    "Quality Studio analysis {StepId} failed for {TaskId}", stepId, task.Id);
                outcomes.Add(new QualityStudioStepOutcome(
                    stepId, PipelineStepStatus.Failed, 0, null, reason));
                return new QualityStudioAnalysisRunOutcome(selection, outcomes, requiresRetry, true);
            }

            stopwatch.Stop();
            if (!result.Available)
            {
                var reason = result.UnavailableReason ?? "Quality Studio analysis core reported the analysis unavailable.";
                Record(jobFolder, stepId, PipelineStepStatus.Failed, 0, null, reason, stopwatch.ElapsedMilliseconds);
                outcomes.Add(new QualityStudioStepOutcome(
                    stepId, PipelineStepStatus.Failed, 0, null, reason));
                return new QualityStudioAnalysisRunOutcome(selection, outcomes, requiresRetry, true);
            }

            var evidence = QualityStudioAnalysisEvidence.Persist(
                jobFolder, task.Key ?? task.Id, stepId, selection, result);
            requiresRetry |= evidence.RequiresSteeredRetry;
            var verdict = result.Findings.Count == 0 ? "pass" : "findings";
            var reasonText = result.Findings.Count == 0
                ? null
                : QualityStudioAnalysisPolicy.FindingsBlock(stepId)
                    ? $"{result.Findings.Count} finding(s) require a steered retry."
                    : $"{result.Findings.Count} security finding(s) recorded as non-blocking evidence.";
            Record(jobFolder, stepId, PipelineStepStatus.Passed, result.Findings.Count,
                verdict, reasonText, stopwatch.ElapsedMilliseconds);
            outcomes.Add(new QualityStudioStepOutcome(
                stepId, PipelineStepStatus.Passed, result.Findings.Count,
                evidence.ArtifactPath, reasonText));
        }

        return new QualityStudioAnalysisRunOutcome(selection, outcomes, requiresRetry, false);
    }

    private void Record(
        string jobFolder,
        string stepId,
        PipelineStepStatus status,
        int findings,
        string? verdict,
        string? reason,
        long durationMs)
    {
        var completedAt = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolder, new PipelineStepExecution
        {
            StepId = stepId,
            Kind = StepKind.Analysis,
            Status = status,
            CompletedAt = completedAt,
            DurationMs = durationMs,
            Verdict = verdict,
            VerdictSummary = findings > 0 ? $"{findings} Quality Studio finding(s)" : null,
            Reason = reason,
        });
    }
}
