using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Retention;

namespace AgentStudio.TaskServer;

public sealed record RetentionWorkspaceMetrics(
    int Tasks,
    long HotBytes,
    long ColdBytes,
    long GitDirectoryBytes,
    long WorkingTreeBytes);
public sealed record RetentionReport(
    string Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int PolicyVersion,
    RetentionWorkspaceMetrics Before,
    RetentionWorkspaceMetrics After,
    RetentionPlan? Plan,
    object? Result,
    IReadOnlyDictionary<string, RetentionRuleReport> Rules,
    IReadOnlyDictionary<string, RetentionProjectReport> Projects,
    IReadOnlyList<RetentionTopTask> TopTasks,
    string? ReportPath);
public sealed record RetentionRuleReport(int Actions, long Bytes);
public sealed record RetentionProjectReport(int Tasks, int Actions, long ArchiveBytes, long DeleteBytes, long RefusedBytes);
public sealed record RetentionTopTask(string Project, string TaskKey, int Actions, long Bytes);

public static class RetentionCli
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static async Task<int> RunAsync(RetentionCommandOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            if (options.Operation == "verify-full")
            {
                var verified = await FullBackupService.VerifyAsync(options.OutputPath!, cancellationToken);
                await WriteAsync(output, options.Json, verified, $"Verified full backup: {options.OutputPath}");
                return 0;
            }
            if (options.Operation == "restore-full")
            {
                await FullBackupService.RestoreAsync(options.OutputPath!, options.RestoreDestination!, cancellationToken);
                await WriteAsync(output, options.Json, new { restored = options.RestoreDestination }, $"Restored full backup into {options.RestoreDestination}");
                return 0;
            }

            var policy = await LoadPolicyAsync(options.Policy, cancellationToken);
            var errors = policy.Validate();
            if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
            var archivePath = options.ArchivePath ?? Environment.GetEnvironmentVariable("ARCHIVE_PATH");
            var store = new FileTreeRetentionStore(options.Workspace, archivePath);
            if (options.Operation == "backup-full")
            {
                var backup = await new FullBackupService(store).CreateAsync(options.OutputPath!, cancellationToken);
                await WriteAsync(output, options.Json, backup, $"Created full backup: {backup.BackupDirectory}");
                return 0;
            }
            if (options.Operation == "restore")
            {
                var restore = await store.RestoreAsync(options.Task!, options.Project, cancellationToken);
                await AppendAuditAsync(store.WorkspacePath, "restore", options.Task!, restore, cancellationToken);
                await WriteAsync(output, options.Json, restore, $"Restored {restore.TaskKey}: {restore.RestoredFiles} files, {restore.RestoredBytes} bytes.");
                return 0;
            }

            var startedAt = DateTimeOffset.UtcNow;
            var inventory = await store.EnumerateTasksAndFilesAsync(options.Project, options.Task, cancellationToken);
            var before = Measure(store, inventory);
            var plan = RetentionPlanner.Plan(inventory, policy, startedAt);
            RetentionRunResult? run = null;
            if (options.Operation == "apply")
            {
                lock (RetentionRepositoryGate.For(store.WorkspacePath))
                {
                    FileTreeRetentionGit.RequireCleanIndex(store.WorkspacePath);
                    FileTreeRetentionGit.EnsureRuntimeIgnored(store.WorkspacePath);
                    run = new RetentionExecutor(store, policy)
                        .ApplyAsync(plan, "task-server-retention-cli", cancellationToken)
                        .GetAwaiter().GetResult();
                    FileTreeRetentionGit.CommitPlan(store.WorkspacePath, plan);
                }
            }
            var afterInventory = await store.EnumerateTasksAndFilesAsync(options.Project, options.Task, cancellationToken);
            var reportWithoutPath = BuildReport(
                options.Operation, startedAt, DateTimeOffset.UtcNow, policy.Version, before,
                Measure(store, afterInventory), plan, run, null);
            var reportPath = await WriteReportAsync(store.WorkspacePath, reportWithoutPath, cancellationToken);
            var report = reportWithoutPath with { ReportPath = reportPath };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine, cancellationToken);
            if (options.Operation == "apply")
                await AppendAuditAsync(store.WorkspacePath, "apply", options.Task, report, cancellationToken);
            await WriteAsync(output, options.Json, report,
                $"Retention {options.Operation}: {plan.ActionCount} actions, {plan.ArchiveBytes} archive bytes, {plan.DeleteBytes} delete bytes, {plan.RefusedBytes} refused bytes. Report: {reportPath}");
            return 0;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"Retention command failed: {exception.Message}");
            return 1;
        }
    }

    private static RetentionReport BuildReport(
        string mode,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        int policyVersion,
        RetentionWorkspaceMetrics before,
        RetentionWorkspaceMetrics after,
        RetentionPlan plan,
        RetentionRunResult? result,
        string? reportPath)
    {
        var actions = plan.Tasks.SelectMany(task => task.Actions.Select(action => (task, action))).ToList();
        var rules = actions.GroupBy(item => item.action.RuleId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new RetentionRuleReport(group.Count(), group.Sum(item => item.action.Bytes)), StringComparer.Ordinal);
        var projects = plan.Tasks.GroupBy(task => task.Project, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new RetentionProjectReport(
                group.Count(), group.Sum(task => task.Actions.Count), group.Sum(task => task.ArchiveBytes),
                group.Sum(task => task.DeleteBytes), group.Sum(task => task.RefusedBytes)), StringComparer.OrdinalIgnoreCase);
        var top = plan.Tasks.OrderByDescending(task => task.ArchiveBytes + task.DeleteBytes + task.RefusedBytes).Take(20)
            .Select(task => new RetentionTopTask(task.Project, task.TaskKey, task.Actions.Count,
                task.ArchiveBytes + task.DeleteBytes + task.RefusedBytes)).ToList();
        return new RetentionReport(mode, startedAt, finishedAt, policyVersion, before, after, plan, result, rules, projects, top, reportPath);
    }

    private static RetentionWorkspaceMetrics Measure(FileTreeRetentionStore store, IReadOnlyList<RetentionTaskInventory> inventory) =>
        new(
            inventory.Count(task => task.TaskKey != "__workspace-runtime__"),
            inventory.Sum(task => task.Files.Sum(file => file.Size)),
            DirectoryBytes(store.ArchivePath),
            DirectoryBytes(Path.Combine(store.WorkspacePath, ".git")),
            Directory.EnumerateFiles(store.WorkspacePath, "*", SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(store.WorkspacePath, file).StartsWith($".git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Sum(file => new FileInfo(file).Length));

    private static long DirectoryBytes(string path) => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
        : 0;

    private static async Task<RetentionPolicy> LoadPolicyAsync(string value, CancellationToken cancellationToken)
    {
        if (value.Equals("default", StringComparison.OrdinalIgnoreCase)) return RetentionPolicy.Default;
        await using var stream = File.OpenRead(Path.GetFullPath(value));
        return await JsonSerializer.DeserializeAsync<RetentionPolicy>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("Retention policy is empty or invalid.");
    }

    private static async Task<string> WriteReportAsync(string workspace, RetentionReport report, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(workspace, ".metadata", "retention-reports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{report.StartedAt.UtcDateTime:yyyyMMdd'T'HHmmssfff'Z'}-{report.Mode}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine, cancellationToken);
        return path;
    }

    private static async Task AppendAuditAsync(string workspace, string action, string? task, object detail, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workspace, ".metadata", "retention-audit.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, actor = "task-server-retention-cli", action, task, detail }, JsonOptions);
        await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
    }

    private static async Task WriteAsync(TextWriter output, bool json, object value, string summary)
        => await output.WriteLineAsync(json ? JsonSerializer.Serialize(value, JsonOptions) : summary);

}
