using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Projects fenced Remote Review command evidence into the unified task
/// timeline. The Review report remains canonical; these rows make physical
/// placement and per-pipeline-step duration visible without parsing Markdown.
/// </summary>
internal static class RemotePipelineStepTimeline
{
    public static void Record(
        TimelineLog timeline,
        string jobFolder,
        string attemptId,
        string evidenceFile,
        Contract.ReviewReportRequest report)
    {
        var existing = timeline.ReadAll(jobFolder);
        foreach (var group in report.Commands
                     .Where(command =>
                         !string.IsNullOrWhiteSpace(command.PipelineStepId)
                         && string.Equals(command.WorkspaceRole, "candidate", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(command => command.PipelineStepId!, StringComparer.OrdinalIgnoreCase))
        {
            if (existing.Any(item =>
                    item.Kind == TimelineEventKinds.PostStepFinished
                    && string.Equals(item.RunId, attemptId, StringComparison.Ordinal)
                    && string.Equals(Detail(item, "step"), group.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var commands = group.OrderBy(command => command.StartedAt).ToArray();
            var first = commands[0];
            var startedAt = commands.Min(command => command.StartedAt);
            var finishedAt = commands.Max(command => command.FinishedAt);
            var verdicts = report.Verdicts
                .Where(verdict => commands.Any(command =>
                    string.Equals(command.Aspect, verdict.Aspect, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var blocking = verdicts.FirstOrDefault(verdict =>
                verdict.Status is "block" or "fail")
                ?? verdicts.FirstOrDefault(verdict => verdict.Status == "concerns");
            var processFailed = commands.Any(command =>
                command.Signal is not null || command.ExitCode is not (0 or null));
            var aspect = string.Equals(first.PipelineStepClass, "aspect", StringComparison.OrdinalIgnoreCase);
            // A baseline-compared gate may legitimately return a non-zero
            // process exit while its verdict proves there are no new failures.
            // Semantic aspects, by contrast, need a healthy CLI process even
            // when their product verdict is non-blocking.
            var failed = aspect
                ? processFailed
                : blocking is not null || (verdicts.Length == 0 && processFailed);
            var verdict = blocking ?? verdicts.LastOrDefault();
            var details = new Dictionary<string, string>
            {
                ["step"] = group.Key,
                ["stepClass"] = first.PipelineStepClass ?? "tool",
                ["executionLocation"] = "remote",
                ["executor"] = report.ExecutorId,
                ["host"] = report.Environment.HostId,
                ["workspace"] = report.Workspace.WorkspaceIdentity,
                ["attemptId"] = attemptId,
                ["expectedResultSha"] = report.Workspace.ExpectedResultSha,
                ["status"] = failed ? "failed" : "passed",
                ["durationMs"] = Math.Max(0L, (long)(finishedAt - startedAt).TotalMilliseconds)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (verdict is not null)
            {
                details["verdict"] = verdict.Status;
                details["classification"] = verdict.Classification;
                details["verdictSummary"] = verdict.Summary;
            }

            timeline.Append(jobFolder, new TimelineEvent
            {
                Ts = startedAt,
                Kind = TimelineEventKinds.PostStepStarted,
                Actor = TimelineActors.System,
                RunId = attemptId,
                Summary = $"{group.Key} started on {report.Environment.HostId}",
                Details = new Dictionary<string, string>(details)
                {
                    ["status"] = "running",
                },
            });
            timeline.Append(jobFolder, new TimelineEvent
            {
                Ts = finishedAt,
                Kind = TimelineEventKinds.PostStepFinished,
                Actor = TimelineActors.System,
                RunId = attemptId,
                PayloadRef = evidenceFile,
                Summary = $"{group.Key} {(failed ? "failed" : "passed")} on {report.Environment.HostId}",
                Details = details,
            });
        }
    }

    private static string? Detail(TimelineEvent item, string key)
        => item.Details is not null && item.Details.TryGetValue(key, out var value)
            ? value
            : null;
}
