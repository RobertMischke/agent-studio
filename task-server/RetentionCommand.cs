using AgentStudio.Retention;
using System.Text.Json;

namespace AgentStudio.TaskServer;

public sealed record RetentionWorkspaceSnapshot(
    int Tasks,
    long HotTaskBytes,
    long ColdBytes,
    long GitWorkingTreeBytes);

public sealed record RetentionReportGroup(string Name, int Actions, long Bytes);

public sealed record RetentionTopTask(string Project, string TaskKey, int Actions, long Bytes);

public sealed record RetentionRunReport(
    string Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int PolicyVersion,
    RetentionWorkspaceSnapshot Before,
    RetentionWorkspaceSnapshot After,
    int ActionCount,
    long ArchiveBytes,
    long DeleteBytes,
    long RefusedBytes,
    IReadOnlyList<RetentionReportGroup> ByRule,
    IReadOnlyList<RetentionReportGroup> ByProject,
    IReadOnlyList<RetentionTopTask> TopTasks,
    RetentionExecutionResult? Execution,
    string ReportPath);

public static class RetentionCommand
{
    public static async Task<int> ExecuteAsync(
        RetentionCommandOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (options.Operation == "verify-full")
            {
                var verified = await new FullBackupService().VerifyAsync(options.Output!, cancellationToken);
                WriteOutput(options.Json, verified, $"Verified full backup {verified.SetSha256} ({verified.Files.Count} files).");
                return 0;
            }
            if (options.Operation == "restore-full")
            {
                await new FullBackupService().RestoreAsync(options.Output!, options.Workspace!, cancellationToken);
                WriteOutput(options.Json, new { restoredTo = Path.GetFullPath(options.Workspace!) },
                    $"Restored full backup to {Path.GetFullPath(options.Workspace!)}.");
                return 0;
            }

            var store = new FileTreeRetentionStore(options.Workspace!);
            if (options.Operation == "backup-full")
            {
                var result = await new FullBackupService().CreateAsync(store, options.Output!, cancellationToken);
                WriteOutput(options.Json, result,
                    $"Created full backup {result.BackupDirectory}: {result.FileCount} files, {result.TotalBytes} bytes.");
                return 0;
            }
            if (options.Operation == "restore")
            {
                var committer = new RetentionGitCommitter();
                var repositoryRoot = committer.ResolveRepositoryRoot(store.WorkspaceRoot)
                    ?? throw new InvalidOperationException("Workspace is not a Git repository; restore did not run.");
                RetentionArchiveManifest manifest;
                lock (RepositoryMutationGate.For(repositoryRoot))
                    manifest = store.RestoreAsync(options.Task!, options.Project, cancellationToken).GetAwaiter().GetResult();
                var commit = committer.CommitProject(
                    store.WorkspaceRoot, manifest.Project, 1, manifest.TotalBytes,
                    $"retention: restored {manifest.TaskKey}, {manifest.TotalBytes} bytes");
                if (!commit.Success)
                    throw new InvalidOperationException(commit.Error);
                WriteOutput(options.Json, new { manifest.TaskKey, manifest.TotalBytes, manifest.RestoredAt, commit.Sha },
                    $"Restored {manifest.TaskKey}: {manifest.TotalBytes} bytes.");
                return 0;
            }

            var policy = string.IsNullOrWhiteSpace(options.Policy)
                         || options.Policy.Equals("default", StringComparison.OrdinalIgnoreCase)
                ? RetentionPolicy.Default()
                : RetentionPolicy.Load(options.Policy);
            var policyErrors = policy.Validate();
            if (policyErrors.Count > 0)
                throw new InvalidDataException(string.Join(" ", policyErrors));
            var startedAt = DateTimeOffset.UtcNow;
            var inventory = await store.EnumerateTasksAndFilesAsync(
                options.Project, options.Task, cancellationToken);
            var before = Snapshot(store, inventory);
            var plan = new RetentionPlanner().Plan(policy, inventory, startedAt);
            RetentionExecutionResult? execution = null;
            if (options.Operation == "apply")
            {
                var committer = new RetentionGitCommitter();
                var repositoryRoot = committer.ResolveRepositoryRoot(store.WorkspaceRoot)
                    ?? throw new InvalidOperationException("Workspace is not a Git repository; apply did not run.");
                lock (RepositoryMutationGate.For(repositoryRoot))
                {
                    execution = new RetentionExecutor(store).ApplyAsync(
                        plan, policy, Environment.UserName, cancellationToken).GetAwaiter().GetResult();
                }
                foreach (var project in execution.ChangedProjects.Where(project => project != "_workspace"))
                {
                    var projectActions = plan.Actions.Where(action =>
                        action.Project.Equals(project, StringComparison.OrdinalIgnoreCase)
                        && action.Kind is RetentionActionKind.ArchiveHeavy or RetentionActionKind.ArchiveTask).ToArray();
                    var commit = committer.CommitProject(
                        store.WorkspaceRoot,
                        project,
                        projectActions.Select(action => action.TaskKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        projectActions.Sum(action => action.Bytes));
                    if (!commit.Success)
                        throw new InvalidOperationException($"Retention commit failed for {project}: {commit.Error}");
                }
                var runtimeCommit = committer.CommitRuntimeExclusions(store.WorkspaceRoot);
                if (!runtimeCommit.Success)
                    throw new InvalidOperationException($"Runtime exclusion commit failed: {runtimeCommit.Error}");
                await WriteAuditAsync(store.WorkspaceRoot, startedAt, plan, execution, cancellationToken);
            }

            var afterInventory = await store.EnumerateTasksAndFilesAsync(
                options.Project, options.Task, cancellationToken);
            var after = Snapshot(store, afterInventory);
            var reportPath = ReportPath(store.WorkspaceRoot, startedAt, options.Operation);
            var report = new RetentionRunReport(
                options.Operation,
                startedAt,
                DateTimeOffset.UtcNow,
                policy.Version,
                before,
                after,
                plan.Actions.Count,
                plan.ArchiveBytes,
                plan.DeleteBytes,
                plan.RefusedBytes,
                Group(plan.Actions, action => action.RuleId),
                Group(plan.Actions, action => action.Project),
                plan.Actions.Where(action => action.TaskKey is not null)
                    .GroupBy(action => (action.Project, action.TaskKey!))
                    .Select(group => new RetentionTopTask(
                        group.Key.Project, group.Key.Item2, group.Count(), group.Sum(action => action.Bytes)))
                    .OrderByDescending(item => item.Bytes)
                    .Take(20)
                    .ToArray(),
                execution,
                reportPath);
            await WriteReportAsync(report, cancellationToken);
            WriteOutput(options.Json, report,
                $"Retention {options.Operation}: {report.ActionCount} actions, "
                + $"{report.ArchiveBytes} archive bytes, {report.DeleteBytes} delete bytes. Report: {reportPath}");
            return execution is { Errors.Count: > 0 } ? 1 : 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Retention command failed: {exception.Message}");
            return 1;
        }
    }

    private static RetentionWorkspaceSnapshot Snapshot(
        FileTreeRetentionStore store,
        IReadOnlyList<RetentionTaskInventory> tasks) => new(
            tasks.Count(task => task.Key != "__workspace__"),
            tasks.Where(task => task.Key != "__workspace__").Sum(task => task.Files.Sum(file => file.Size)),
            DirectoryBytes(store.ArchiveRoot, excludeGit: false),
            DirectoryBytes(store.WorkspaceRoot, excludeGit: true));

    private static IReadOnlyList<RetentionReportGroup> Group(
        IReadOnlyList<RetentionAction> actions,
        Func<RetentionAction, string> key) => actions
        .GroupBy(key, StringComparer.OrdinalIgnoreCase)
        .Select(group => new RetentionReportGroup(group.Key, group.Count(), group.Sum(action => action.Bytes)))
        .OrderByDescending(group => group.Bytes)
        .ToArray();

    private static long DirectoryBytes(string root, bool excludeGit)
    {
        if (!Directory.Exists(root))
            return 0;
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (excludeGit && Path.GetFileName(child).Equals(".git", StringComparison.OrdinalIgnoreCase))
                    continue;
                pending.Push(child);
            }
            foreach (var path in Directory.EnumerateFiles(directory))
                bytes += new FileInfo(path).Length;
        }
        return bytes;
    }

    private static string ReportPath(string workspaceRoot, DateTimeOffset at, string mode) =>
        Path.Combine(workspaceRoot, ".metadata", "retention-reports",
            $"{at.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{mode}.json");

    private static async Task WriteReportAsync(RetentionRunReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(report.ReportPath)!);
        var json = JsonSerializer.Serialize(report, RetentionPolicy.JsonOptions);
        await File.WriteAllTextAsync(report.ReportPath, json, cancellationToken);
        var results = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR");
        if (!string.IsNullOrWhiteSpace(results))
        {
            Directory.CreateDirectory(results);
            await File.WriteAllTextAsync(Path.Combine(results, Path.GetFileName(report.ReportPath)), json, cancellationToken);
        }
    }

    private static async Task WriteAuditAsync(
        string workspaceRoot,
        DateTimeOffset startedAt,
        RetentionPlan plan,
        RetentionExecutionResult execution,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(workspaceRoot, ".metadata", "retention-audit.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(new
        {
            eventType = "retention.apply.completed",
            startedAt,
            completedAt = DateTimeOffset.UtcNow,
            policyVersion = plan.PolicyVersion,
            plannedActions = plan.Actions.Count,
            execution,
        }, RetentionPolicy.JsonOptions);
        await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
    }

    private static void WriteOutput(bool json, object value, string text)
    {
        Console.WriteLine(json
            ? JsonSerializer.Serialize(value, RetentionPolicy.JsonOptions)
            : text);
    }
}
