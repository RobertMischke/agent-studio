using System.Globalization;
using System.Text.Json;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// Read-time bridge between the remote execution lifecycle and the existing
/// task pipeline table. Remote runners deliberately do not write the local
/// <c>pipeline-execution.json</c> lifecycle. Their canonical facts already live
/// in task/session metadata, the task timeline, Remote Review Grade evidence,
/// and the token ledger. This projection joins those sources without creating
/// another persisted state.
/// </summary>
internal static class RemotePipelineExecutionProjection
{
    internal const string NotApplicableReason =
        "Executed remotely; this local pipeline step is not applicable.";

    internal sealed record Result(
        PipelineExecutionRecord? Execution,
        IReadOnlyDictionary<string, IReadOnlyList<TaskTokenCall>> LedgerCalls);

    internal sealed record ReviewGrade(
        string AttemptId,
        DateTime ReceivedAt,
        string Outcome,
        string? Summary,
        Contract.ReviewReportRequest? Report = null);

    public static Result Project(
        PipelineExecutionRecord? local,
        TaskPipeline pipeline,
        TaskInfo task,
        IReadOnlyList<SessionEvent> sessions,
        IReadOnlyList<TimelineEvent> timeline,
        TaskTokenSummary? tokenSummary)
    {
        var remoteSessions = sessions
            .Where(IsRemoteSession)
            .OrderBy(item => item.Ts)
            .ToList();
        var remoteCompletions = timeline
            .Where(IsRemoteCompletion)
            .OrderBy(item => item.Ts)
            .ToList();

        if (remoteSessions.Count == 0 && remoteCompletions.Count == 0)
            return new Result(local, EmptyLedger());
        var latestSession = sessions.OrderBy(item => item.Ts).LastOrDefault();
        var latestCompletion = timeline
            .Where(item => string.Equals(
                item.Kind,
                TimelineEventKinds.AgentRunFinished,
                StringComparison.Ordinal))
            .OrderBy(item => item.Ts)
            .LastOrDefault();
        if ((latestSession is not null
                && !IsRemoteSession(latestSession)
                && latestSession.Ts > (remoteSessions.LastOrDefault()?.Ts ?? DateTime.MinValue))
            || (latestCompletion is not null
                && !IsRemoteCompletion(latestCompletion)
                && latestCompletion.Ts > (remoteCompletions.LastOrDefault()?.Ts ?? DateTime.MinValue)))
        {
            return new Result(local, EmptyLedger());
        }

        var completion = remoteCompletions.LastOrDefault();
        var startedAt = remoteSessions.LastOrDefault(item =>
                completion is null || item.Ts <= completion.Ts)?.Ts
            ?? remoteSessions.LastOrDefault()?.Ts
            ?? local?.StartedAt
            ?? task.CreatedAt;
        var grade = ReadLatestGrade(task.FolderPath);
        var completedAt = completion?.Ts;
        var decisionId = ResolveDecisionStepId(pipeline);

        var execution = local ?? NewRecord(pipeline, task, startedAt);
        var attempt = execution.Attempt <= 0 ? 1 : execution.Attempt;
        var steps = execution.Steps
            .Select(step => step with { Attempt = step.Attempt ?? attempt })
            .ToList();

        SkipRemoteOnlySteps(steps, pipeline.Pre.Select(step => step.Id));

        var allocations = AllocateLedgerCalls(
            tokenSummary,
            task,
            startedAt,
            completion?.Ts,
            grade,
            decisionId);
        var coreCalls = allocations.GetValueOrDefault(PipelineCatalogue.CoreAgentRunStepId) ?? [];
        if (completion is not null)
        {
            Upsert(
                steps,
                pipeline,
                PipelineCatalogue.CoreAgentRunStepId,
                BuildCoreStep(
                    task,
                    completion,
                    startedAt,
                    attempt,
                    coreCalls,
                    remoteSessions.LastOrDefault(item => item.Ts <= completion.Ts)?.ExecutionLocation));
        }

        if (grade is not null)
        {
            ProjectReviewSteps(steps, pipeline, grade, attempt);
            var decisionCalls = allocations.GetValueOrDefault(decisionId) ?? [];
            Upsert(
                steps,
                pipeline,
                decisionId,
                BuildDecisionStep(decisionId, grade, attempt, decisionCalls));
        }

        SkipRemoteOnlySteps(
            steps,
            pipeline.Post
                .Where(step =>
                    step.Kind is StepKind.Aspect or StepKind.Drift
                    || step.Id is PipelineCatalogue.OrchestratorReviewStepId
                        or PipelineCatalogue.CodeReviewGradeStepId)
                .Select(step => step.Id));

        var latestProjectedAt = new[]
            {
                execution.CompletedAt,
                completedAt,
                grade?.ReceivedAt,
                steps.Where(step => step.CompletedAt.HasValue)
                    .Select(step => step.CompletedAt)
                    .Max(),
            }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();
        var taskSettled = task.State is TaskStates.HumanReview or TaskStates.Completed or TaskStates.Archive;
        var recordCompletedAt = execution.CompletedAt
            ?? (taskSettled && latestProjectedAt != default ? latestProjectedAt : null);

        execution = execution with
        {
            StartedAt = Min(execution.StartedAt, startedAt),
            CompletedAt = recordCompletedAt,
            Steps = steps,
        };
        return new Result(execution, allocations);
    }

    private static PipelineExecutionRecord NewRecord(
        TaskPipeline pipeline,
        TaskInfo task,
        DateTime startedAt) =>
        new()
        {
            PipelineId = pipeline.Id,
            PipelineVersion = pipeline.Version,
            Project = task.ProjectName,
            JobId = task.Id,
            StartedAt = startedAt,
            Attempt = 1,
            Steps = pipeline.AllSteps.Select(step => new PipelineStepExecution
            {
                StepId = step.Id,
                Kind = step.Kind,
                Attempt = 1,
                Model = step.Model,
                Status = step.Stub ? PipelineStepStatus.Planned : PipelineStepStatus.Pending,
            }).ToList(),
        };

    private static PipelineStepExecution BuildCoreStep(
        TaskInfo task,
        TimelineEvent completion,
        DateTime startedAt,
        int attempt,
        IReadOnlyList<TaskTokenCall> calls,
        TaskExecutionLocation? location)
    {
        var statusToken = Detail(completion, "status")?.Trim().ToLowerInvariant();
        var passed = statusToken is "done" or "noop" or "completed" or "pass";
        var completedAt = completion.Ts;
        return WithLedgerUsage(
            new PipelineStepExecution
            {
                StepId = PipelineCatalogue.CoreAgentRunStepId,
                Kind = StepKind.Core,
                Attempt = attempt,
                Model = task.Model,
                ThinkingLevel = task.ThinkingLevel,
                Status = passed ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                DurationMs = DurationMs(startedAt, completedAt),
                Reason = passed
                    ? "Remote runner completed the fenced coding run."
                    : completion.Summary,
                Verdict = statusToken,
                ExecutionLocation = location is null
                    ? new PipelineStepExecutionLocation { ExecutionKind = "remote" }
                    : new PipelineStepExecutionLocation
                    {
                        ExecutionKind = "remote",
                        HostId = location.HostDisplayName,
                        ExecutorId = location.RunnerId,
                        InstanceId = location.ClientId,
                        WorkspaceIdentity = location.WorktreePath,
                    },
            },
            calls,
            task.Model);
    }

    private static PipelineStepExecution BuildDecisionStep(
        string stepId,
        ReviewGrade grade,
        int attempt,
        IReadOnlyList<TaskTokenCall> calls)
    {
        var passed = string.Equals(grade.Outcome, "Pass", StringComparison.OrdinalIgnoreCase);
        var verdict = grade.Outcome.Trim().ToLowerInvariant() switch
        {
            "productfailure" => "product-failure",
            "reviewinfra" => "review-infra",
            var value => value,
        };
        var reason = $"Remote Review Plane verdict {grade.Outcome} (attempt {grade.AttemptId}).";
        if (!string.IsNullOrWhiteSpace(grade.Summary))
            reason += " " + grade.Summary!.Trim();

        return WithLedgerUsage(
            new PipelineStepExecution
            {
                StepId = stepId,
                Kind = StepKind.Orchestrator,
                Attempt = attempt,
                Status = passed ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
                StartedAt = grade.ReceivedAt,
                CompletedAt = grade.ReceivedAt,
                DurationMs = 0,
                Reason = reason,
                Verdict = verdict,
                VerdictSummary = grade.Summary,
                ExecutionLocation = ReviewLocation(grade),
            },
            calls,
            calls.LastOrDefault()?.Model);
    }

    private static void ProjectReviewSteps(
        List<PipelineStepExecution> steps,
        TaskPipeline pipeline,
        ReviewGrade grade,
        int attempt)
    {
        if (grade.Report is null) return;
        var candidates = grade.Report.Commands
            .Where(command => string.Equals(command.Phase, "verification", StringComparison.OrdinalIgnoreCase)
                              && string.Equals(command.WorkspaceRole, "candidate", StringComparison.OrdinalIgnoreCase))
            .GroupBy(command => PipelineStepId(command.StepId), StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates)
        {
            if (!pipeline.AllSteps.Any(step => string.Equals(step.Id, group.Key, StringComparison.OrdinalIgnoreCase)))
                continue;
            var commands = group.ToArray();
            var verdict = grade.Report.Verdicts.LastOrDefault(item =>
                commands.Any(command => string.Equals(command.Aspect, item.Aspect, StringComparison.OrdinalIgnoreCase)));
            var isDecisionSupport = string.Equals(
                group.Key,
                PipelineCatalogue.CodeReviewGradeStepId,
                StringComparison.OrdinalIgnoreCase);
            var failed = commands.Any(command => command.ExitCode != 0 || command.Signal is not null)
                         || verdict?.Status is "block" or "fail";
            var startedAt = commands.Min(command => command.StartedAt);
            var completedAt = commands.Max(command => command.FinishedAt);
            var usage = commands.Where(command => command.TokenUsage is not null)
                .Select(command => command.TokenUsage!)
                .ToArray();
            Upsert(steps, pipeline, group.Key, new PipelineStepExecution
            {
                StepId = group.Key,
                Attempt = attempt,
                Status = failed ? PipelineStepStatus.Failed : PipelineStepStatus.Passed,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                DurationMs = DurationMs(startedAt, completedAt),
                Model = commands.LastOrDefault(command => !string.IsNullOrWhiteSpace(command.Model))?.Model,
                ThinkingLevel = commands.LastOrDefault(command => !string.IsNullOrWhiteSpace(command.ThinkingLevel))?.ThinkingLevel,
                InputTokens = usage.Sum(item => item.InputTokens),
                OutputTokens = usage.Sum(item => item.OutputTokens),
                CacheReadTokens = usage.Sum(item => item.CacheReadTokens),
                CacheCreationTokens = usage.Sum(item => item.CacheCreationTokens),
                TokenUsageSource = usage.Length == 0 ? null : "Remote Review command evidence",
                Reason = $"Remote Review Executor {grade.Report.ExecutorId} on {grade.Report.Environment.HostId} ran this step under lease {grade.Report.LeaseId}, fence {grade.Report.Fence}.",
                Verdict = isDecisionSupport
                          && verdict?.Classification.StartsWith("CodeReviewGrade:", StringComparison.OrdinalIgnoreCase) == true
                    ? verdict.Classification[(verdict.Classification.IndexOf(':') + 1)..].ToLowerInvariant()
                    : verdict?.Status ?? (failed ? "fail" : "pass"),
                VerdictSummary = verdict?.Summary,
                ExecutionLocation = ReviewLocation(grade),
            });
        }
    }

    private static string PipelineStepId(string commandStepId)
    {
        var separator = commandStepId.IndexOf(':');
        return separator > 0 ? commandStepId[..separator] : commandStepId;
    }

    private static PipelineStepExecutionLocation? ReviewLocation(ReviewGrade grade)
        => grade.Report is null
            ? null
            : new PipelineStepExecutionLocation
            {
                ExecutionKind = "remote",
                HostId = grade.Report.Environment.HostId,
                ExecutorId = grade.Report.ExecutorId,
                InstanceId = grade.Report.InstanceId,
                LeaseId = grade.Report.LeaseId,
                Fence = grade.Report.Fence,
                AttemptId = grade.AttemptId,
                ResourceNamespace = grade.Report.Workspace.ResourceNamespace,
                WorkspaceIdentity = grade.Report.Workspace.WorkspaceIdentity,
            };

    private static PipelineStepExecution WithLedgerUsage(
        PipelineStepExecution step,
        IReadOnlyList<TaskTokenCall> calls,
        string? fallbackModel)
    {
        if (calls.Count == 0) return step;
        return step with
        {
            Model = calls.LastOrDefault(call => !string.IsNullOrWhiteSpace(call.Model))?.Model
                ?? fallbackModel,
            InputTokens = calls.Sum(call => call.InputTokens),
            OutputTokens = calls.Sum(call => call.OutputTokens),
            CacheReadTokens = calls.Sum(call => call.CacheReadTokens),
            CacheCreationTokens = calls.Sum(call => call.CacheCreationTokens),
            TokenUsageSource = $"Remote token ledger · {calls.Count} call{(calls.Count == 1 ? "" : "s")}",
        };
    }

    private static Dictionary<string, IReadOnlyList<TaskTokenCall>> AllocateLedgerCalls(
        TaskTokenSummary? summary,
        TaskInfo task,
        DateTime startedAt,
        DateTime? coreCompletedAt,
        ReviewGrade? grade,
        string decisionStepId)
    {
        var result = new Dictionary<string, IReadOnlyList<TaskTokenCall>>(StringComparer.OrdinalIgnoreCase);
        if (summary is null || summary.TotalTokens <= 0) return result;

        var calls = summary.Entries
            .Where(call => call.Ts == default || call.Ts >= startedAt)
            .OrderBy(call => call.Ts)
            .ToList();
        if (calls.Count == 0)
        {
            calls.Add(new TaskTokenCall
            {
                Ts = summary.LastUpdate ?? coreCompletedAt ?? startedAt,
                Model = summary.LastModel ?? task.Model,
                ParticipantId = "agent:remote",
                InputTokens = summary.InputTokens,
                OutputTokens = summary.OutputTokens,
                CacheReadTokens = summary.CacheReadTokens,
                CacheCreationTokens = summary.CacheCreationTokens,
                EstimatedApiCostUsd = summary.EstimatedApiCostUsd,
                ModelPriced = summary.AllModelsPriced,
            });
        }

        var agentCalls = calls
            .Where(call => TokenModelDisplay.IsAgentParticipant(call.ParticipantId))
            .ToList();
        if (agentCalls.Count == 0)
            agentCalls = calls.Where(call => grade is null || call.Ts <= (coreCompletedAt ?? grade.ReceivedAt)).ToList();
        if (agentCalls.Count == 0)
            agentCalls.Add(calls[0]);

        var agentSet = agentCalls.ToHashSet();
        var decisionCalls = calls.Where(call => !agentSet.Contains(call)).ToList();
        if (grade is null && decisionCalls.Count > 0)
        {
            agentCalls.AddRange(decisionCalls);
            decisionCalls.Clear();
        }

        result[PipelineCatalogue.CoreAgentRunStepId] = agentCalls;
        if (grade is not null && decisionCalls.Count > 0)
            result[decisionStepId] = decisionCalls;
        return result;
    }

    private static string ResolveDecisionStepId(TaskPipeline pipeline) =>
        pipeline.AllSteps.Any(step =>
            string.Equals(
                step.Id,
                PipelineCatalogue.OrchestratorDecisionStepId,
                StringComparison.OrdinalIgnoreCase))
            ? PipelineCatalogue.OrchestratorDecisionStepId
            : PipelineCatalogue.CodeReviewGradeStepId;

    private static void SkipRemoteOnlySteps(
        List<PipelineStepExecution> steps,
        IEnumerable<string> stepIds)
    {
        var ids = stepIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (!ids.Contains(step.StepId)
                || step.Status is not (PipelineStepStatus.Pending or PipelineStepStatus.Planned))
                continue;
            steps[i] = step with
            {
                Status = PipelineStepStatus.Skipped,
                Reason = NotApplicableReason,
            };
        }
    }

    private static void Upsert(
        List<PipelineStepExecution> steps,
        TaskPipeline pipeline,
        string stepId,
        PipelineStepExecution value)
    {
        var index = steps.FindIndex(step =>
            string.Equals(step.StepId, stepId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            steps[index] = value with { Kind = steps[index].Kind };
            return;
        }

        var catalogue = pipeline.AllSteps.FirstOrDefault(step =>
            string.Equals(step.Id, stepId, StringComparison.OrdinalIgnoreCase));
        if (catalogue is not null)
            steps.Add(value with { Kind = catalogue.Kind });
    }

    private static bool IsRemoteSession(SessionEvent item) =>
        string.Equals(item.Cli, "remote-runner", StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.ExecutionLocation?.ExecutionKind, "remote", StringComparison.OrdinalIgnoreCase);

    private static bool IsRemoteCompletion(TimelineEvent item) =>
        string.Equals(item.Kind, TimelineEventKinds.AgentRunFinished, StringComparison.Ordinal)
        && (string.Equals(Detail(item, "cli"), "remote-runner", StringComparison.OrdinalIgnoreCase)
            || item.Summary.Contains("remote run", StringComparison.OrdinalIgnoreCase));

    private static string? Detail(TimelineEvent item, string key) =>
        item.Details is not null && item.Details.TryGetValue(key, out var value)
            ? value
            : null;

    private static long DurationMs(DateTime start, DateTime finish) =>
        Math.Max(0, (long)(finish - start).TotalMilliseconds);

    private static DateTime Min(DateTime left, DateTime right) =>
        left == default || right < left ? right : left;

    private static IReadOnlyDictionary<string, IReadOnlyList<TaskTokenCall>> EmptyLedger() =>
        new Dictionary<string, IReadOnlyList<TaskTokenCall>>(StringComparer.OrdinalIgnoreCase);

    internal static ReviewGrade? ReadLatestGrade(string jobFolder)
    {
        if (string.IsNullOrWhiteSpace(jobFolder) || !Directory.Exists(jobFolder)) return null;
        ReviewGrade? latest = null;
        foreach (var path in Directory.EnumerateFiles(jobFolder, "remote-review-grade-*.json"))
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<AgentStudio.Runner.RemoteReviewReportEnvelope>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (envelope is null) continue;
                var parsed = new ReviewGrade(
                    envelope.AttemptId,
                    envelope.ReceivedAt,
                    envelope.Report.Outcome,
                    envelope.Report.Summary,
                    envelope.Report);
                if (latest is null || parsed.ReceivedAt > latest.ReceivedAt) latest = parsed;
            }
            catch (JsonException exception)
            {
                SilentCatch.Note(exception, $"Remote pipeline projection ignored malformed review evidence: {path}");
            }
            catch (IOException exception)
            {
                SilentCatch.Note(exception, $"Remote pipeline projection could not read review evidence: {path}");
            }
        }
        foreach (var path in Directory.EnumerateFiles(jobFolder, "remote-review-grade-*.md"))
        {
            var parsed = ParseGrade(path);
            if (parsed is not null && (latest is null || parsed.ReceivedAt > latest.ReceivedAt))
                latest = parsed;
        }
        return latest;
    }

    private static ReviewGrade? ParseGrade(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return null; }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inFrontmatter = false;
        var frontmatterClosed = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line == "---")
            {
                if (!inFrontmatter && !frontmatterClosed) inFrontmatter = true;
                else if (inFrontmatter)
                {
                    inFrontmatter = false;
                    frontmatterClosed = true;
                }
                continue;
            }
            if (!inFrontmatter) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            fields[line[..colon].Trim()] = Unquote(line[(colon + 1)..].Trim());
        }

        if (!fields.TryGetValue("attemptId", out var attemptId)
            || !fields.TryGetValue("receivedAt", out var receivedRaw)
            || !DateTime.TryParse(
                receivedRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var receivedAt)
            || !fields.TryGetValue("outcome", out var outcome))
        {
            return null;
        }

        string? summary = null;
        var outcomeLine = Array.FindIndex(lines, line =>
            line.TrimStart().StartsWith("**Outcome:**", StringComparison.OrdinalIgnoreCase));
        if (outcomeLine >= 0)
        {
            for (var i = outcomeLine + 1; i < lines.Length; i++)
            {
                var candidate = lines[i].Trim();
                if (candidate.StartsWith("## ", StringComparison.Ordinal)) break;
                if (candidate.Length > 0)
                {
                    summary = candidate;
                    break;
                }
            }
        }

        return new ReviewGrade(attemptId, receivedAt, outcome, summary);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        return value;
    }
}
