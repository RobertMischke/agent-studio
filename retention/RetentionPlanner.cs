namespace AgentStudio.Retention;

public sealed record RetentionFileInventory(
    string RelativePath,
    long Size,
    DateTimeOffset LastModified,
    ArtifactClass ArtifactClass);

public sealed record RetentionTaskInventory(
    string TaskKey,
    string TaskId,
    string Project,
    string Lane,
    DateTimeOffset? TerminalAt,
    string TaskDirectory,
    IReadOnlyList<RetentionFileInventory> Files,
    bool HasArchivePointer = false,
    DateTimeOffset? RestoredAt = null);

public enum RetentionActionKind
{
    ArchiveHeavy,
    ArchiveTask,
    DeleteRuntime,
    RefuseOversize,
}

public sealed record RetentionAction(
    RetentionActionKind Kind,
    string RuleId,
    IReadOnlyList<string> RelativePaths,
    long Bytes);

public sealed record RetentionTaskPlan(
    string TaskKey,
    string TaskId,
    string Project,
    string Lane,
    string TaskDirectory,
    IReadOnlyList<RetentionAction> Actions,
    long ArchiveBytes,
    long DeleteBytes,
    long RefusedBytes);

public sealed record RetentionPlan(DateTimeOffset PlannedAt, IReadOnlyList<RetentionTaskPlan> Tasks)
{
    public long ArchiveBytes => Tasks.Sum(task => task.ArchiveBytes);
    public long DeleteBytes => Tasks.Sum(task => task.DeleteBytes);
    public long RefusedBytes => Tasks.Sum(task => task.RefusedBytes);
    public int ActionCount => Tasks.Sum(task => task.Actions.Count);
}

public static class RetentionPlanner
{
    public static RetentionPlan Plan(
        IEnumerable<RetentionTaskInventory> inventory,
        RetentionPolicy policy,
        DateTimeOffset now)
    {
        var tasks = new List<RetentionTaskPlan>();
        foreach (var task in inventory.OrderBy(item => item.TerminalAt ?? DateTimeOffset.MaxValue))
        {
            var actions = BuildTaskActions(task, policy, now);
            if (actions.Count == 0) continue;
            tasks.Add(new RetentionTaskPlan(
                task.TaskKey, task.TaskId, task.Project, task.Lane, task.TaskDirectory, actions,
                actions.Where(action => action.Kind is RetentionActionKind.ArchiveHeavy or RetentionActionKind.ArchiveTask).Sum(action => action.Bytes),
                actions.Where(action => action.Kind == RetentionActionKind.DeleteRuntime).Sum(action => action.Bytes),
                actions.Where(action => action.Kind == RetentionActionKind.RefuseOversize).Sum(action => action.Bytes)));
        }
        return new RetentionPlan(now, tasks);
    }

    private static List<RetentionAction> BuildTaskActions(
        RetentionTaskInventory task,
        RetentionPolicy policy,
        DateTimeOffset now)
    {
        var actions = new List<RetentionAction>();
        var heavyRule = policy.Resolve(task.Project, ArtifactClass.HeavyWorkingData);
        var evidenceRule = policy.Resolve(task.Project, ArtifactClass.Evidence);
        var runtimeRule = policy.Resolve(task.Project, ArtifactClass.Runtime);

        var refused = task.Files
            .Where(file => file.ArtifactClass == ArtifactClass.HeavyWorkingData
                           && file.Size > (heavyRule.RefuseAboveBytes ?? ArtifactClassifier.RefuseAboveBytes))
            .ToList();
        Add(actions, RetentionActionKind.RefuseOversize, heavyRule.Id, refused);

        var runtime = task.Files
            .Where(file => file.ArtifactClass == ArtifactClass.Runtime
                           && runtimeRule.DeleteAfterDays is not null
                           && ShouldDeleteRuntime(file, runtimeRule, now))
            .ToList();
        Add(actions, RetentionActionKind.DeleteRuntime, runtimeRule.Id, runtime);

        if (task.TerminalAt is null || IsNeverArchiveLane(task.Lane, heavyRule.NeverArchiveLanes))
            return actions;

        var retentionAnchor = task.RestoredAt is { } restoredAt && restoredAt > task.TerminalAt.Value
            ? restoredAt
            : task.TerminalAt.Value;
        var age = now - retentionAnchor;
        if (evidenceRule.WholeTaskAfterDaysTerminal is { } wholeDays && age >= TimeSpan.FromDays(wholeDays))
        {
            var whole = task.Files
                .Where(file => file.ArtifactClass is ArtifactClass.Evidence or ArtifactClass.HeavyWorkingData)
                .Where(file => !IsHotStubFile(file.RelativePath))
                .ToList();
            Add(actions, RetentionActionKind.ArchiveTask, evidenceRule.Id, whole);
            return actions;
        }

        var heavy = task.Files.Where(file => file.ArtifactClass == ArtifactClass.HeavyWorkingData).ToList();
        if (heavyRule.ArchiveAfterDaysTerminal is { } archiveDays && age >= TimeSpan.FromDays(archiveDays))
        {
            Add(actions, RetentionActionKind.ArchiveHeavy, heavyRule.Id, heavy);
            return actions;
        }

        if (heavyRule.HotBudgetBytesPerTask is { } budget && heavy.Sum(file => file.Size) > budget)
        {
            var selected = new List<RetentionFileInventory>();
            var remaining = heavy.Sum(file => file.Size);
            foreach (var file in heavy.OrderBy(file => file.LastModified).ThenBy(file => file.RelativePath, StringComparer.Ordinal))
            {
                if (remaining <= budget) break;
                selected.Add(file);
                remaining -= file.Size;
            }
            Add(actions, RetentionActionKind.ArchiveHeavy, $"{heavyRule.Id}-budget", selected);
        }
        return actions;
    }

    private static bool ShouldDeleteRuntime(RetentionFileInventory file, ArtifactRetentionRule rule, DateTimeOffset now)
    {
        var value = ArtifactClassifier.Normalize(file.RelativePath);
        if (value.Equals(".metadata/attempt-authority.json", StringComparison.Ordinal)) return false;
        var family = ArtifactClassifier.Family(file.RelativePath);
        var days = rule.DeleteAfterDaysPerFamily != null
                   && rule.DeleteAfterDaysPerFamily.TryGetValue(family, out var familyDays)
            ? familyDays
            : rule.DeleteAfterDays!.Value;
        return now - file.LastModified >= TimeSpan.FromDays(days);
    }

    private static bool IsNeverArchiveLane(string lane, IReadOnlyList<string> configured)
    {
        if (configured.Contains(lane, StringComparer.OrdinalIgnoreCase)) return true;
        var prefix = lane.TakeWhile(char.IsDigit).ToArray();
        return prefix.Length > 0 && int.TryParse(prefix, out var number) && number <= 5;
    }

    private static bool IsHotStubFile(string path)
    {
        var value = ArtifactClassifier.Normalize(path);
        return value.EndsWith("/status.md", StringComparison.Ordinal)
               || value.Equals("status.md", StringComparison.Ordinal)
               || value.Contains("/.retention-excerpts/", StringComparison.Ordinal)
               || value.StartsWith(".retention-excerpts/", StringComparison.Ordinal)
               || value.EndsWith("/archive-manifest.json", StringComparison.Ordinal)
               || value.Equals("archive-manifest.json", StringComparison.Ordinal);
    }

    private static void Add(
        ICollection<RetentionAction> actions,
        RetentionActionKind kind,
        string ruleId,
        IReadOnlyCollection<RetentionFileInventory> files)
    {
        if (files.Count == 0) return;
        actions.Add(new RetentionAction(
            kind, ruleId,
            files.Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToList(),
            files.Sum(file => file.Size)));
    }
}
