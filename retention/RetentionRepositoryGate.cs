using System.Collections.Concurrent;

namespace AgentStudio.Retention;

public static class RetentionRepositoryGate
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static object For(string repositoryRoot) =>
        Gates.GetOrAdd(Path.GetFullPath(repositoryRoot), static _ => new object());
}
