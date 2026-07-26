namespace AgentStudio.TaskServer;

public enum TaskServerCommandKind
{
    Serve,
    Version,
    Backup,
}

public sealed record TaskServerCommandLine(
    TaskServerCommandKind Kind,
    string? BackupName,
    string[] HostArguments)
{
    public static TaskServerCommandLine Parse(string[] args)
    {
        if (args is ["--version"] or ["-V"])
            return new TaskServerCommandLine(TaskServerCommandKind.Version, null, []);
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
}
