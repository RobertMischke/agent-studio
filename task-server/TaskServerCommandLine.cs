namespace AgentStudio.TaskServer;

public enum TaskServerCommandKind
{
    Serve,
    Version,
    Backup,
    Retention,
}

public sealed record RetentionCommandOptions(
    string Operation,
    string Workspace,
    string Policy,
    string? Project,
    string? Task,
    string? ArchivePath,
    string? OutputPath,
    string? RestoreDestination,
    bool Json);

public sealed record TaskServerCommandLine(
    TaskServerCommandKind Kind,
    string? BackupName,
    RetentionCommandOptions? Retention,
    string[] HostArguments)
{
    public static TaskServerCommandLine Parse(string[] args)
    {
        if (args is ["--version"] or ["-V"])
            return new TaskServerCommandLine(TaskServerCommandKind.Version, null, null, []);
        if (args.Length > 0 && string.Equals(args[0], "retention", StringComparison.OrdinalIgnoreCase))
            return new TaskServerCommandLine(TaskServerCommandKind.Retention, null, ParseRetention(args[1..]), []);
        if (args.Length == 0 || !string.Equals(args[0], "backup", StringComparison.OrdinalIgnoreCase))
            return new TaskServerCommandLine(TaskServerCommandKind.Serve, null, null, args);

        string? name = null;
        var hostArguments = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--name", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("backup --name requires a value.");
                name = args[++index];
                continue;
            }
            hostArguments.Add(args[index]);
        }
        return new TaskServerCommandLine(TaskServerCommandKind.Backup, name, null, hostArguments.ToArray());
    }

    private static RetentionCommandOptions ParseRetention(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("retention requires plan, apply, restore, backup-full, verify-full, or restore-full.");
        var operation = args[0].ToLowerInvariant();
        if (operation is not ("plan" or "apply" or "restore" or "backup-full" or "verify-full" or "restore-full"))
            throw new ArgumentException($"Unknown retention operation: {args[0]}");
        string? workspace = null;
        string policy = "default";
        string? project = null;
        string? task = null;
        string? archivePath = null;
        string? output = null;
        string? into = null;
        var json = false;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.Equals("--json", StringComparison.OrdinalIgnoreCase)) { json = true; continue; }
            if (index + 1 >= args.Length) throw new ArgumentException($"{argument} requires a value.");
            var value = args[++index];
            switch (argument.ToLowerInvariant())
            {
                case "--workspace": workspace = value; break;
                case "--policy": policy = value; break;
                case "--project": project = value; break;
                case "--task": task = value; break;
                case "--archive": archivePath = value; break;
                case "--out": output = value; break;
                case "--into": into = value; break;
                default: throw new ArgumentException($"Unknown retention option: {argument}");
            }
        }
        if (operation is "verify-full" or "restore-full")
        {
            if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException($"retention {operation} requires --out <backup-set>.");
            workspace ??= Directory.GetCurrentDirectory();
        }
        else if (string.IsNullOrWhiteSpace(workspace))
            throw new ArgumentException($"retention {operation} requires --workspace <path>.");
        if (operation == "restore" && string.IsNullOrWhiteSpace(task))
            throw new ArgumentException("retention restore requires --task <key>.");
        if (operation == "backup-full" && string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("retention backup-full requires --out <backup-path>.");
        if (operation == "restore-full" && string.IsNullOrWhiteSpace(into))
            throw new ArgumentException("retention restore-full requires --into <empty-directory>.");
        return new RetentionCommandOptions(operation, workspace!, policy, project, task, archivePath, output, into, json);
    }
}
