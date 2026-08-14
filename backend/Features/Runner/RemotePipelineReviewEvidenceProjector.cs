using System.Text;
using System.Text.Json;
using AgentStudio.GeneratedFiles;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Projects a fenced Remote Review report into the same task-local pipeline,
/// aspect, file-provenance, and timeline views used by local pipeline steps.
/// The ReviewAttempt remains the authority. These files are operator-facing
/// evidence and never decide admission or lease ownership.
/// </summary>
public sealed class RemotePipelineReviewEvidenceProjector
{
    private readonly PipelineExecutionLog _pipeline;
    private readonly TimelineLog _timeline;
    private readonly FileGenerationIndex _files;
    private readonly AgentStudio.Projects.ProjectSettingsService _settings;

    public RemotePipelineReviewEvidenceProjector(
        PipelineExecutionLog pipeline,
        TimelineLog timeline,
        FileGenerationIndex files,
        AgentStudio.Projects.ProjectSettingsService settings)
    {
        _pipeline = pipeline;
        _timeline = timeline;
        _files = files;
        _settings = settings;
    }

    public async Task ProjectAsync(
        TaskInfo task,
        ReviewAttemptDto review,
        Contract.ReviewReportRequest report,
        string evidenceFile,
        DateTime receivedAt,
        CancellationToken ct)
    {
        var settings = PipelineTypeSettings.ForTask(_settings.Get(task.ProjectName), task);
        var catalogue = ProjectPipelineOrder.Apply(PipelineCatalogue.ForTask(task), settings);
        var execution = _pipeline.EnsureRun(
            task.FolderPath,
            catalogue,
            task.ProjectName,
            task.Id,
            report.Commands.Select(command => command.StartedAt).DefaultIfEmpty(receivedAt).Min());
        using var attempt = _pipeline.EnterAttempt(task.FolderPath, execution.Attempt);

        foreach (var command in report.Commands.Where(command => command.Phase == "verification"))
        {
            if (Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind))
                await ProjectAspectAsync(task, review, command, report, receivedAt, execution.Attempt, ct);
            else if (Contract.ReviewCommandKinds.IsQualityAnalysis(command.ExecutionKind))
                await ProjectQualityAnalysisAsync(task, review, command, report, execution.Attempt, ct);
        }
        ProjectToolGate(task, review, report, execution.Attempt);
        ProjectTimeline(task, review, report, evidenceFile);
    }

    private async Task ProjectQualityAnalysisAsync(
        TaskInfo task,
        ReviewAttemptDto review,
        Contract.ReviewCommandEvidenceDto command,
        Contract.ReviewReportRequest report,
        int pipelineAttempt,
        CancellationToken ct)
    {
        var json = ArtifactText(report.Artifacts, command.StdoutSha256);
        if (string.IsNullOrWhiteSpace(json)) return;

        var directory = Path.Combine(task.FolderPath, "results", "quality-studio");
        Directory.CreateDirectory(directory);
        var stem = command.Aspect.Equals(
            QualityAnalysisPolicy.AngularRuleAxis,
            StringComparison.OrdinalIgnoreCase)
                ? "angular-rules"
                : SafeName(command.Aspect);
        var jsonRelative = Path.Combine("results", "quality-studio", stem + ".json");
        var markdownRelative = Path.Combine("results", "quality-studio", stem + ".md");
        await File.WriteAllTextAsync(
            Path.Combine(task.FolderPath, jsonRelative),
            json,
            new UTF8Encoding(false),
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(task.FolderPath, markdownRelative),
            RenderQualityAnalysisMarkdown(json, command),
            new UTF8Encoding(false),
            ct);

        var duration = Math.Max(0, (long)(command.FinishedAt - command.StartedAt).TotalMilliseconds);
        var generation = new FileGenerationMeta
        {
            Kind = "quality-analysis",
            Cli = "quality-studio-package",
            StartedAt = command.StartedAt,
            EndedAt = command.FinishedAt,
            DurationMs = duration,
            StepId = command.StepId,
            HeadShaAfter = command.ExpectedResultSha,
        };
        _files.Upsert(task.FolderPath, generation with { File = jsonRelative.Replace('\\', '/') });
        _files.Upsert(task.FolderPath, generation with { File = markdownRelative.Replace('\\', '/') });

        var verdict = report.Verdicts.LastOrDefault(candidate =>
            string.Equals(candidate.Aspect, command.Aspect, StringComparison.OrdinalIgnoreCase));
        var passed = command.ExitCode == 0 && command.Signal is null;
        _pipeline.RecordStep(task.FolderPath, new PipelineStepExecution
        {
            StepId = command.StepId,
            Kind = StepKind.Analysis,
            Attempt = pipelineAttempt,
            Status = passed ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
            StartedAt = command.StartedAt,
            CompletedAt = command.FinishedAt,
            DurationMs = duration,
            Verdict = passed ? "pass" : "findings",
            Reason = verdict?.Summary ?? "Quality Studio analysis returned structured evidence.",
            ExecutionLocation = "remote",
            ExecutionHostId = review.Lease?.HostId ?? report.Environment.HostId,
            ExecutionExecutorId = review.Lease?.ExecutorId ?? report.ExecutorId,
            ExecutionAttemptId = review.AttemptId,
        });
    }

    private static string RenderQualityAnalysisMarkdown(
        string json,
        Contract.ReviewCommandEvidenceDto command)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var findings = root.TryGetProperty("findings", out var value)
            && value.ValueKind == JsonValueKind.Array
                ? value
                : default;
        var count = findings.ValueKind == JsonValueKind.Array ? findings.GetArrayLength() : 0;
        var builder = new StringBuilder();
        builder.AppendLine("# Quality Studio analysis evidence");
        builder.AppendLine();
        builder.Append("- Step: `").Append(command.StepId).AppendLine("`");
        builder.Append("- Axis: `").Append(command.Aspect).AppendLine("`");
        builder.Append("- Result-SHA: `").Append(command.ExpectedResultSha).AppendLine("`");
        builder.Append("- Findings: ").Append(count).AppendLine();
        builder.AppendLine("- Rule configuration: `.quality/rules.json`");
        builder.AppendLine();
        if (count == 0)
        {
            builder.AppendLine("No named rule violations were found.");
            return builder.ToString();
        }

        builder.AppendLine("## Findings");
        foreach (var finding in findings.EnumerateArray())
        {
            var ruleId = Text(finding, "ruleId", "unknown-rule");
            var title = Text(finding, "title", "Quality finding");
            var path = Text(finding, "path", "unknown-path");
            var line = finding.TryGetProperty("line", out var lineValue)
                       && lineValue.ValueKind == JsonValueKind.Number
                ? $":{lineValue.GetInt32()}"
                : string.Empty;
            builder.AppendLine();
            builder.Append("### `").Append(ruleId).Append("` ").AppendLine(title);
            builder.Append("- Location: `").Append(path).Append(line).AppendLine("`");
            builder.Append("- Severity: `").Append(Text(finding, "severity", "unknown")).AppendLine("`");
            builder.AppendLine(Text(finding, "description", "No description supplied."));
            builder.AppendLine();
            builder.Append("Recommendation: ").AppendLine(Text(finding, "recommendation", "See the named Quality Studio rule."));
        }
        return builder.ToString();
    }

    private static string Text(JsonElement value, string property, string fallback)
        => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? fallback
            : fallback;

    private static string SafeName(string value)
        => new(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
            ? character
            : '_').ToArray());

    private async Task ProjectAspectAsync(
        TaskInfo task,
        ReviewAttemptDto review,
        Contract.ReviewCommandEvidenceDto command,
        Contract.ReviewReportRequest report,
        DateTime receivedAt,
        int pipelineAttempt,
        CancellationToken ct)
    {
        var aspectId = command.StepId.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)
            ? command.StepId["aspect-".Length..]
            : command.Aspect;
        if (!AspectRunnerService.Catalogue.TryGetValue(aspectId, out var definition))
            return;
        var remoteVerdict = report.Verdicts.LastOrDefault(verdict =>
            string.Equals(verdict.Aspect, command.Aspect, StringComparison.OrdinalIgnoreCase));
        var status = remoteVerdict?.Status.ToLowerInvariant() switch
        {
            "pass" => AspectStatus.Pass,
            "block" or "fail" => AspectStatus.Block,
            _ => AspectStatus.Concerns,
        };
        var response = ArtifactText(report.Artifacts, command.StdoutSha256);
        var summary = remoteVerdict?.Summary
                      ?? $"Remote aspect '{aspectId}' returned no structured summary.";
        var verdict = new AspectVerdict(
            aspectId,
            status,
            summary,
            string.IsNullOrWhiteSpace(response)
                ? $"_{summary}_\n"
                : $"## Model reply\n\n```\n{response.Trim()}\n```\n",
            status == AspectStatus.Pass ? null : $"{definition.ConcernNamespace}:concerns");
        var markdownName = $"aspect-{aspectId}.md";
        var jsonName = $"aspect-{aspectId}.json";
        await File.WriteAllTextAsync(
            Path.Combine(task.FolderPath, markdownName),
            AspectVerdictParsing.RenderReport(verdict, receivedAt),
            new UTF8Encoding(false),
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(task.FolderPath, jsonName),
            AspectVerdictParsing.RenderJson(verdict, command.Model, receivedAt),
            new UTF8Encoding(false),
            ct);

        var duration = Math.Max(0, (long)(command.FinishedAt - command.StartedAt).TotalMilliseconds);
        var generation = new FileGenerationMeta
        {
            Kind = "aspect",
            Model = command.Model,
            Cli = command.FileName,
            TokensIn = command.InputTokens,
            TokensOut = command.OutputTokens,
            CacheReadTokens = command.CacheReadTokens,
            CacheCreationTokens = command.CacheCreationTokens,
            StartedAt = command.StartedAt,
            EndedAt = command.FinishedAt,
            DurationMs = duration,
            StepId = command.StepId,
            HeadShaAfter = command.ExpectedResultSha,
        };
        _files.Upsert(task.FolderPath, generation with { File = markdownName });
        _files.Upsert(task.FolderPath, generation with { File = jsonName });
        _pipeline.RecordStep(task.FolderPath, new PipelineStepExecution
        {
            StepId = command.StepId,
            Kind = StepKind.Aspect,
            Attempt = pipelineAttempt,
            Model = command.Model,
            ThinkingLevel = command.ThinkingLevel,
            Status = status == AspectStatus.Pass
                ? PipelineStepStatus.Passed
                : PipelineStepStatus.Failed,
            StartedAt = command.StartedAt,
            CompletedAt = command.FinishedAt,
            DurationMs = duration,
            InputTokens = command.InputTokens,
            OutputTokens = command.OutputTokens,
            CacheReadTokens = command.CacheReadTokens,
            CacheCreationTokens = command.CacheCreationTokens,
            Reason = summary,
            ExecutionLocation = "remote",
            ExecutionHostId = review.Lease?.HostId ?? report.Environment.HostId,
            ExecutionExecutorId = review.Lease?.ExecutorId ?? report.ExecutorId,
            ExecutionAttemptId = review.AttemptId,
        });
    }

    private void ProjectToolGate(
        TaskInfo task,
        ReviewAttemptDto review,
        Contract.ReviewReportRequest report,
        int pipelineAttempt)
    {
        var tools = report.Commands
            .Where(command => command.Phase == "verification"
                              && !Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
                              && !Contract.ReviewCommandKinds.IsQualityAnalysis(command.ExecutionKind))
            .ToArray();
        if (tools.Length == 0) return;
        var passed = tools.All(command => command.ExitCode == 0 && command.Signal is null);
        var first = tools.MinBy(command => command.StartedAt)!;
        var last = tools.MaxBy(command => command.FinishedAt)!;
        _pipeline.RecordStep(task.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.BuildTestGateStepId,
            Kind = StepKind.Tool,
            Attempt = pipelineAttempt,
            Status = passed ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
            StartedAt = first.StartedAt,
            CompletedAt = last.FinishedAt,
            DurationMs = Math.Max(0, (long)(last.FinishedAt - first.StartedAt).TotalMilliseconds),
            Reason = passed
                ? $"{tools.Length} remote tool gate command(s) passed at the immutable Result-SHA."
                : "A remote tool gate command failed at the immutable Result-SHA.",
            ExecutionLocation = "remote",
            ExecutionHostId = review.Lease?.HostId ?? report.Environment.HostId,
            ExecutionExecutorId = review.Lease?.ExecutorId ?? report.ExecutorId,
            ExecutionAttemptId = review.AttemptId,
        });
    }

    private void ProjectTimeline(
        TaskInfo task,
        ReviewAttemptDto review,
        Contract.ReviewReportRequest report,
        string evidenceFile)
    {
        var existing = _timeline.ReadAll(task.FolderPath)
            .Where(item => string.Equals(item.RunId, review.AttemptId, StringComparison.Ordinal))
            .Select(item => $"{item.Kind}:{Detail(item, "stepId")}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var command in report.Commands)
        {
            var details = Details(review, report, command);
            var startedKey = $"{TimelineEventKinds.PostStepStarted}:{command.StepId}";
            if (existing.Add(startedKey))
            {
                _timeline.Append(task.FolderPath, new TimelineEvent
                {
                    Ts = command.StartedAt,
                    Kind = TimelineEventKinds.PostStepStarted,
                    Actor = TimelineActors.External,
                    Summary = $"Remote pipeline step started: {command.StepId}",
                    RunId = review.AttemptId,
                    PayloadRef = evidenceFile,
                    Details = details,
                });
            }

            var finishedKey = $"{TimelineEventKinds.PostStepFinished}:{command.StepId}";
            if (!existing.Add(finishedKey)) continue;
            _timeline.Append(task.FolderPath, new TimelineEvent
            {
                Ts = command.FinishedAt,
                Kind = TimelineEventKinds.PostStepFinished,
                Actor = TimelineActors.External,
                Summary = $"Remote pipeline step finished: {command.StepId}",
                RunId = review.AttemptId,
                PayloadRef = evidenceFile,
                Details = details,
            });
        }
    }

    private static Dictionary<string, string> Details(
        ReviewAttemptDto review,
        Contract.ReviewReportRequest report,
        Contract.ReviewCommandEvidenceDto command)
    {
        var hostId = review.Lease?.HostId ?? report.Environment.HostId;
        var executorId = review.Lease?.ExecutorId ?? report.ExecutorId;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stepId"] = command.StepId,
            ["pipelineStepId"] = Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
                || Contract.ReviewCommandKinds.IsQualityAnalysis(command.ExecutionKind)
                ? command.StepId
                : PipelineCatalogue.BuildTestGateStepId,
            ["executionKind"] = command.ExecutionKind,
            ["executionLocation"] = "remote",
            ["hostId"] = hostId,
            ["executorId"] = executorId,
            ["attemptId"] = review.AttemptId,
            ["leaseId"] = report.LeaseId,
            ["fence"] = report.Fence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["resultSha"] = command.ExpectedResultSha,
            ["phase"] = command.Phase,
            ["status"] = command.ExitCode == 0 && command.Signal is null ? "passed" : "failed",
            ["durationMs"] = Math.Max(0, (long)(command.FinishedAt - command.StartedAt).TotalMilliseconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["model"] = command.Model ?? string.Empty,
            ["thinkingLevel"] = command.ThinkingLevel ?? string.Empty,
        };
    }

    private static string ArtifactText(
        IReadOnlyList<Contract.ReviewArtifactEvidenceDto> artifacts,
        string digest)
    {
        var artifact = artifacts.LastOrDefault(item =>
            string.Equals(item.Sha256, digest, StringComparison.OrdinalIgnoreCase));
        if (artifact?.ContentBase64 is null) return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(artifact.ContentBase64));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static string Detail(TimelineEvent item, string key)
        => item.Details is not null && item.Details.TryGetValue(key, out var value)
            ? value
            : string.Empty;
}
