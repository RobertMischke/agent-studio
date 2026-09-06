namespace AgentStudio.Retention;

public sealed record RetentionFileInventory(
    string RelativePath,
    long Size,
    DateTimeOffset LastModifiedAt,
    ArtifactClass ArtifactClass,
    string RuleFamily = "");

public sealed record RetentionTaskInventory(
    string Id,
    string Key,
    string Project,
    string Lane,
    DateTimeOffset? TerminalAt,
    IReadOnlyList<RetentionFileInventory> Files,
    string StorePath);

public enum RetentionActionKind
{
    ArchiveHeavy,
    ArchiveTask,
    DeleteRuntime,
    RefuseOversize,
}

public sealed record RetentionAction(
    RetentionActionKind Kind,
    string Project,
    string? TaskKey,
    string RuleId,
    IReadOnlyList<string> RelativePaths,
    long Bytes,
    string Reason);

public sealed record RetentionPlan(
    DateTimeOffset PlannedAt,
    int PolicyVersion,
    IReadOnlyList<RetentionAction> Actions)
{
    public long ArchiveBytes => Actions
        .Where(action => action.Kind is RetentionActionKind.ArchiveHeavy or RetentionActionKind.ArchiveTask)
        .Sum(action => action.Bytes);
    public long DeleteBytes => Actions
        .Where(action => action.Kind == RetentionActionKind.DeleteRuntime)
        .Sum(action => action.Bytes);
    public long RefusedBytes => Actions
        .Where(action => action.Kind == RetentionActionKind.RefuseOversize)
        .Sum(action => action.Bytes);
}

public sealed class RetentionPlanner
{
    public RetentionPlan Plan(
        RetentionPolicy policy,
        IReadOnlyList<RetentionTaskInventory> tasks,
        DateTimeOffset now)
    {
        var actions = new List<RetentionAction>();
        foreach (var task in tasks)
            PlanTask(policy, task, now, actions);
        return new RetentionPlan(now, policy.Version, actions);
    }

    private static void PlanTask(
        RetentionPolicy policy,
        RetentionTaskInventory task,
        DateTimeOffset now,
        ICollection<RetentionAction> actions)
    {
        var rules = policy.Resolve(task.Project);
        var heavyRule = rules.HeavyWorkingData;
        var runtimeRule = rules.Runtime;

        foreach (var file in task.Files.Where(file =>
                     file.ArtifactClass == ArtifactClass.HeavyWorkingData
                     && heavyRule.RefuseAboveBytes is not null
                     && file.Size > heavyRule.RefuseAboveBytes))
        {
            actions.Add(Action(RetentionActionKind.RefuseOversize, task, heavyRule.Id,
                [file], $"file exceeds {heavyRule.RefuseAboveBytes} bytes"));
        }

        var expiredRuntime = task.Files.Where(file =>
                file.ArtifactClass == ArtifactClass.Runtime
                && RuntimeDeleteDays(file, runtimeRule) is { } deleteDays
                && file.LastModifiedAt <= now.AddDays(-deleteDays))
            .ToArray();
        if (expiredRuntime.Length > 0)
            actions.Add(Action(RetentionActionKind.DeleteRuntime, task, runtimeRule.Id,
                expiredRuntime, "runtime retention elapsed"));

        if (task.TerminalAt is null || IsExcluded(task.Lane, heavyRule.NeverArchiveLanes))
            return;

        var age = now - task.TerminalAt.Value;
        if (age.TotalDays >= rules.Stage2StubAfterDaysTerminal)
        {
            var wholeTask = task.Files.Where(file =>
                    file.ArtifactClass == ArtifactClass.HeavyWorkingData
                    || file.ArtifactClass == ArtifactClass.Evidence
                       && !IsHotStubEvidence(file.RelativePath))
                .ToArray();
            if (wholeTask.Length > 0)
            {
                actions.Add(Action(RetentionActionKind.ArchiveTask, task, rules.Evidence.Id,
                    wholeTask, $"terminal for at least {rules.Stage2StubAfterDaysTerminal} days"));
                return;
            }
        }

        var heavy = task.Files
            .Where(file => file.ArtifactClass == ArtifactClass.HeavyWorkingData)
            .OrderBy(file => file.LastModifiedAt)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (heavy.Count == 0)
            return;

        if (age.TotalDays >= rules.Stage1ExcerptAfterDaysTerminal)
        {
            actions.Add(Action(RetentionActionKind.ArchiveHeavy, task, heavyRule.Id,
                heavy, $"terminal for at least {rules.Stage1ExcerptAfterDaysTerminal} days"));
            return;
        }

        var budget = heavyRule.HotBudgetBytesPerTask;
        if (budget is null || heavy.Sum(file => file.Size) <= budget)
            return;
        var bytesToRemove = heavy.Sum(file => file.Size) - budget.Value;
        var selected = new List<RetentionFileInventory>();
        long selectedBytes = 0;
        foreach (var file in heavy)
        {
            selected.Add(file);
            selectedBytes += file.Size;
            if (selectedBytes >= bytesToRemove)
                break;
        }
        actions.Add(Action(RetentionActionKind.ArchiveHeavy, task, "heavy-hot-budget",
            selected, $"heavy hot budget of {budget.Value} bytes exceeded"));
    }

    private static bool IsExcluded(string lane, IReadOnlyList<string> excluded) =>
        excluded.Any(value => value.Equals(lane, StringComparison.OrdinalIgnoreCase));

    private static int? RuntimeDeleteDays(RetentionFileInventory file, ArtifactRetentionRule rule)
    {
        if (file.RelativePath.StartsWith(".metadata/attempt-authority.archive-", StringComparison.OrdinalIgnoreCase))
            return 90;
        if (file.RelativePath.Contains("rotation", StringComparison.OrdinalIgnoreCase)
            || file.RelativePath.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            return 7;
        return rule.DeleteAfterDays;
    }

    private static bool IsHotStubEvidence(string path) =>
        path.Equals("status.md", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("excerpts/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("archive-manifest.json", StringComparison.OrdinalIgnoreCase);

    private static RetentionAction Action(
        RetentionActionKind kind,
        RetentionTaskInventory task,
        string rule,
        IReadOnlyList<RetentionFileInventory> files,
        string reason) => new(
            kind,
            task.Project,
            task.Key,
            rule,
            files.Select(file => file.RelativePath).ToArray(),
            files.Sum(file => file.Size),
            reason);
}
