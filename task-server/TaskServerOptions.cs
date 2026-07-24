namespace AgentStudio.TaskServer;

public sealed class TaskServerOptions
{
    public const string SectionName = "TaskServer";

    public string DataDirectory { get; set; } = "data";
    public string ListenUrl { get; set; } = "http://127.0.0.1:5071";
    public int MinimumLeaseSeconds { get; set; } = 30;
    public int MaximumLeaseSeconds { get; set; } = 600;
    public int ResultRetentionDays { get; set; } = 30;

    public string ResolveDataDirectory()
    {
        if (string.Equals(DataDirectory, "user-data", StringComparison.OrdinalIgnoreCase))
        {
            var userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(userData, "AgentStudio", "task-server");
        }
        return Path.GetFullPath(Path.IsPathRooted(DataDirectory)
            ? DataDirectory
            : Path.Combine(AppContext.BaseDirectory, DataDirectory));
    }
}
