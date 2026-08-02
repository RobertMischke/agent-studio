using System.Reflection;

namespace AgentStudio.TaskServer;

public sealed record TaskServerBuildIdentity(string Release, string GitSha)
{
    public static TaskServerBuildIdentity Current { get; } = Read();

    public string DisplayVersion => $"{Release}+sha.{GitSha}";

    private static TaskServerBuildIdentity Read()
    {
        var assembly = typeof(TaskServerBuildIdentity).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var release = informational?.Split('+', 2)[0]
                      ?? assembly.GetName().Version?.ToString(3)
                      ?? "unknown";
        var metadataSha = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, "RepositoryCommit", StringComparison.Ordinal))
            ?.Value;
        var informationalSha = informational?
            .Split('+', 2)
            .Skip(1)
            .Select(value => value.StartsWith("sha.", StringComparison.Ordinal)
                ? value[4..]
                : value)
            .FirstOrDefault();
        var sha = FirstNonEmpty(metadataSha, informationalSha, "unknown");
        return new TaskServerBuildIdentity(release, sha);
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.First(value => !string.IsNullOrWhiteSpace(value))!;
}
