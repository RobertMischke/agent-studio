
namespace AgentStudio.Companion;

/// <summary>
/// Pure function that folds the inputs already served to the desktop UI
/// (jobs, runner status, quota report, accumulated token usage) into the
/// payload pushed to the relay. No I/O so the result is unit-testable.
/// </summary>
public static class CompanionSnapshotBuilder
{
    public static CompanionSnapshotEnvelope Build(
        IReadOnlyList<TaskInfo> jobs,
        RunnerStatus runner,
        QuotaReport? quota,
        CompanionTokens tokenAggregate,
        CompanionHost host,
        DateTimeOffset now)
    {
        var jobsByProject = jobs
            .Where(j => !string.IsNullOrEmpty(j.WatchPath))
            .GroupBy(j => (j.WatchPath, j.ProjectName))
            .OrderBy(g => g.Key.ProjectName, StringComparer.OrdinalIgnoreCase);

        var projects = new List<CompanionProject>();
        foreach (var group in jobsByProject)
        {
            runner.Projects.TryGetValue(group.Key.ProjectName, out var rs);
            projects.Add(new CompanionProject
            {
                Name = group.Key.ProjectName,
                WatchPath = group.Key.WatchPath,
                Runner = new CompanionRunner
                {
                    Mode = rs?.Mode ?? "manual",
                    ActiveJobId = rs?.ActiveJobId,
                },
                Pipeline = new CompanionPipeline
                {
                    Ready = ToCards(group.Where(j => j.State == TaskStates.Ready)),
                    Progress = ToCards(group.Where(j => j.State == TaskStates.Progress)),
                    // ADR-0025: keep the companion's existing "review" field
                    // populated with both review lanes so downstream consumers
                    // (older relay clients) keep seeing one merged stream.
                    Review = ToCards(group.Where(j =>
                        j.State == TaskStates.AutoReview ||
                        j.State == TaskStates.HumanReview)),
                },
            });
        }

        var quotaWindows = new List<CompanionQuotaWindow>();
        if (quota?.Snapshots is { } snapshots)
        {
            foreach (var snap in snapshots)
            {
                if (snap.Windows.Count == 0)
                {
                    quotaWindows.Add(new CompanionQuotaWindow
                    {
                        Cli = snap.CliType,
                        Window = "",
                        Plan = snap.Plan,
                        Error = snap.Error,
                    });
                    continue;
                }
                foreach (var w in snap.Windows)
                {
                    quotaWindows.Add(new CompanionQuotaWindow
                    {
                        Cli = snap.CliType,
                        Window = w.Label,
                        UsedPct = w.UsedPct,
                        ResetsAt = w.ResetAt is { } reset
                            ? new DateTimeOffset(DateTime.SpecifyKind(reset, DateTimeKind.Utc))
                            : null,
                        Plan = snap.Plan,
                        Error = snap.Error,
                    });
                }
            }
        }

        return new CompanionSnapshotEnvelope
        {
            SnapshotAt = now,
            Host = host,
            Payload = new CompanionPayload
            {
                Projects = projects,
                Tokens = tokenAggregate,
                Quota = quotaWindows,
            },
        };
    }

    private static List<CompanionJobCard> ToCards(IEnumerable<TaskInfo> source) =>
        source
            .OrderBy(j => j.Order)
            .ThenBy(j => j.CreatedAt)
            .Select(j => new CompanionJobCard
            {
                Id = j.Id,
                Title = j.Title,
                Agent = j.Agent,
                Model = j.Model,
            })
            .ToList();
}
