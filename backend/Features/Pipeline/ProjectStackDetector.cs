namespace AgentStudio.Pipeline;

/// <summary>
/// Derives a project's stack from repository markers. The bounded scan follows
/// conventions instead of project settings: angular.json means Angular,
/// package.json means Node, and solution/project files mean .NET.
/// </summary>
public static class ProjectStackDetector
{
    private const int MaxDepth = 3;
    private const int MaxDirectories = 512;
    private const int MaxEntries = 8_192;
    private static readonly string[] StackOrder =
    [
        PipelineStepStacks.Angular,
        PipelineStepStacks.DotNet,
        PipelineStepStacks.Node,
    ];
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".angular", ".idea", ".vs", ".vscode", "bin", "coverage",
        "dist", "node_modules", "obj", "out", "packages", "test-results",
    };

    public static IReadOnlyList<string> Detect(string? repositoryPath)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(repositoryPath, file =>
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(file);
            if (name.Equals("angular.json", StringComparison.OrdinalIgnoreCase))
                found.Add(PipelineStepStacks.Angular);
            if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                found.Add(PipelineStepStacks.Node);
            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                found.Add(PipelineStepStacks.DotNet);
            return found.Count == StackOrder.Length;
        });
        return StackOrder.Where(found.Contains).ToArray();
    }

    public static bool Applies(string? appliesTo, IReadOnlyCollection<string> detectedStacks)
    {
        var required = string.IsNullOrWhiteSpace(appliesTo)
            ? PipelineStepStacks.Any
            : appliesTo.Trim().ToLowerInvariant();
        return required == PipelineStepStacks.Any
            || detectedStacks.Contains(required, StringComparer.OrdinalIgnoreCase);
    }

    internal static string? FindDirectoryContaining(string? repositoryPath, string markerName)
    {
        string? match = null;
        Visit(repositoryPath, file =>
        {
            if (!Path.GetFileName(file).Equals(markerName, StringComparison.OrdinalIgnoreCase)) return false;
            match = Path.GetDirectoryName(file);
            return true;
        });
        return match;
    }

    private static void Visit(string? repositoryPath, Func<string, bool> visitor)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath)) return;
        var root = Path.GetFullPath(repositoryPath);
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        var scannedEntries = 0;

        while (queue.Count > 0 && visited++ < MaxDirectories)
        {
            var (directory, depth) = queue.Dequeue();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(directory)
                    .Take(MaxEntries + 1)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                directories = depth < MaxDepth
                    ? Directory.EnumerateDirectories(directory)
                        .Take(MaxEntries + 1)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(ex, $"ProjectStackDetector: skipped {directory}");
                continue;
            }

            foreach (var file in files)
            {
                if (++scannedEntries > MaxEntries) return;
                if (visitor(file)) return;
            }

            foreach (var child in directories)
            {
                if (++scannedEntries > MaxEntries) return;
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || IgnoredDirectories.Contains(name)) continue;
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SilentCatch.Note(ex, $"ProjectStackDetector: skipped attributes for {child}");
                    continue;
                }
                queue.Enqueue((child, depth + 1));
            }
        }
    }
}
