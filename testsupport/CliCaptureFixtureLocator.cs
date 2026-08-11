namespace AgentStudio.TestSupport;

/// <summary>
/// Locates the shared CLI capture corpus by its versioned relative path. Tests
/// may use a unique leaf name for readability, while matrix enumeration always
/// returns the full <c>&lt;cli&gt;/&lt;version&gt;/&lt;file&gt;</c> identity so a second
/// version can coexist without overwriting the first.
/// </summary>
public static class CliCaptureFixtureLocator
{
    public static string Root(string repositoryRoot)
        => Path.Combine(repositoryRoot, "testdata", "cli-fixtures", "streams");

    public static IReadOnlyList<string> AllRelativePaths(string repositoryRoot)
    {
        var root = Root(repositoryRoot);
        return Directory
            .EnumerateFiles(root, "*.fixture", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    public static string Resolve(string repositoryRoot, string nameOrRelativePath)
    {
        var root = Root(repositoryRoot);
        var direct = Path.Combine(root, nameOrRelativePath);
        if (File.Exists(direct)) return direct;

        var matches = Directory
            .EnumerateFiles(root, Path.GetFileName(nameOrRelativePath), SearchOption.AllDirectories)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new FileNotFoundException(
                $"CLI capture fixture '{nameOrRelativePath}' was not found under '{root}'.",
                direct),
            _ => throw new InvalidDataException(
                $"CLI capture fixture name '{nameOrRelativePath}' is ambiguous. Use its versioned relative path."),
        };
    }
}
