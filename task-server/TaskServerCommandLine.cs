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
    string? Workspace,
    string? Policy,
    string? Project,
    string? Task,
    string? Output,
    bool Json);

public sealed record TaskServerCommandLine(
    TaskServerCommandKind Kind,
    string? BackupName,
    string[] HostArguments,
    RetentionCommandOptions? Retention = null)
{
    public static TaskServerCommandLine Parse(string[] args)
    {
        if (args is ["--version"] or ["-V"])
            return new TaskServerCommandLine(TaskServerCommandKind.Version, null, []);
        if (args.Length > 0 && string.Equals(args[0], "retention", StringComparison.OrdinalIgnoreCase))
            return ParseRetention(args[1..]);
        if (args.Length == 0 || !string.Equals(args[0], "backup", StringComparison.OrdinalIgnoreCase))
            return new TaskServerCommandLine(TaskServerCommandKind.Serve, null, args);

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
        return new TaskServerCommandLine(
            TaskServerCommandKind.Backup,
            name,
            hostArguments.ToArray());
    }

    private static TaskServerCommandLine ParseRetention(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("retention requires plan, apply, restore, backup-full, verify-full, or restore-full.");
        var operation = args[0].ToLowerInvariant();
        if (operation is not ("plan" or "apply" or "restore" or "backup-full" or "verify-full" or "restore-full"))
            throw new ArgumentException($"Unknown retention operation '{args[0]}'.");
        string? workspace = null;
        string? policy = null;
        string? project = null;
        string? task = null;
        string? output = null;
        var json = false;
        for (var index = 1; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{args[index]} requires a value.");
            var value = args[++index];
            switch (args[index - 1].ToLowerInvariant())
            {
                case "--workspace": workspace = value; break;
                case "--policy": policy = value; break;
                case "--project": project = value; break;
                case "--task": task = value; break;
                case "--out": output = value; break;
                default: throw new ArgumentException($"Unknown retention option '{args[index - 1]}'.");
            }
        }
        if (operation is "plan" or "apply" or "restore" or "backup-full" or "restore-full"
            && string.IsNullOrWhiteSpace(workspace))
            throw new ArgumentException($"retention {operation} requires --workspace.");
        if (operation == "restore" && string.IsNullOrWhiteSpace(task))
            throw new ArgumentException("retention restore requires --task.");
        if (operation is "backup-full" or "verify-full" or "restore-full" && string.IsNullOrWhiteSpace(output))
            throw new ArgumentException($"retention {operation} requires --out.");
        return new TaskServerCommandLine(
            TaskServerCommandKind.Retention, null, [],
            new RetentionCommandOptions(operation, workspace, policy, project, task, output, json));
    }
}
