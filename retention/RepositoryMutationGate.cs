using System.Collections.Concurrent;

namespace AgentStudio.Retention;

public static class RepositoryMutationGate
{
    private static readonly ConcurrentDictionary<string, object> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    public static object For(string repositoryRoot)
    {
        var key = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Gates.GetOrAdd(key, static _ => new object());
    }
}
