using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Docs;

/// <summary>
/// Reads the repository-owned <c>docs/quality</c> style-guide family and
/// projects only guides that apply to the selected project. Applicability is
/// declared in Markdown frontmatter, while technology detection is a bounded,
/// read-only scan of familiar project markers. The same catalogue feeds the
/// Project Hub and the intake prompt enrichment, so navigation and agent
/// context cannot drift into separate implementations.
/// </summary>
public sealed partial class ProjectStyleGuideService
{
    private const int MaxFrontmatterChars = 24_000;
    private const int MaxScanDepth = 3;
    private const int MaxScannedDirectories = 512;
    private const int MaxGuideFiles = 64;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".angular", ".idea", ".vs", ".vscode", "bin", "coverage",
        "dist", "node_modules", "obj", "test-results"
    };

    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly ILogger<ProjectStyleGuideService> _logger;

    public ProjectStyleGuideService(
        TaskScannerService scanner,
        ProjectRegistry registry,
        ILogger<ProjectStyleGuideService> logger)
    {
        _scanner = scanner;
        _registry = registry;
        _logger = logger;
    }

    public ProjectStyleGuideCatalogue? GetCatalogue(string projectName)
    {
        var repositoryRoot = ProjectRepoResolver.ResolveForProject(projectName, _scanner, _registry);
        if (repositoryRoot == null) return null;

        try
        {
            return BuildCatalogue(projectName, repositoryRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex,
                "Failed to build project style-guide catalogue for {ProjectName} at {RepositoryRoot}",
                projectName, repositoryRoot);
            return new ProjectStyleGuideCatalogue(projectName, repositoryRoot, [], [], []);
        }
    }

    /// <summary>Pure-ish test seam over a repository fixture.</summary>
    internal static ProjectStyleGuideCatalogue BuildCatalogue(string projectName, string repositoryRoot)
    {
        var technologies = DetectTechnologies(repositoryRoot);
        var guidesRoot = Path.Combine(repositoryRoot, "docs", "quality");
        if (!Directory.Exists(guidesRoot))
            return new ProjectStyleGuideCatalogue(projectName, repositoryRoot, technologies, [], []);

        var guides = new List<ProjectStyleGuide>();
        var warnings = new List<ProjectStyleGuideWarning>();
        foreach (var path in Directory.EnumerateFiles(guidesRoot, "*.md", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                     .Take(MaxGuideFiles))
        {
            var guide = ParseGuide(path, repositoryRoot);
            if (guide == null)
            {
                if (DeclaresStyleGuide(path))
                {
                    warnings.Add(new ProjectStyleGuideWarning(
                        Path.GetRelativePath(Path.Combine(repositoryRoot, "docs"), path).Replace('\\', '/'),
                        "Invalid style-guide frontmatter; the guide was excluded."));
                }
                continue;
            }
            if (AppliesToProject(guide.AppliesTo, projectName, technologies))
                guides.Add(guide);
        }

        return new ProjectStyleGuideCatalogue(projectName, repositoryRoot, technologies, guides, warnings);
    }

    internal static ProjectStyleGuide? ParseGuide(string path, string repositoryRoot)
    {
        var raw = File.ReadAllText(path);
        if (raw.Length > MaxFrontmatterChars)
            raw = raw[..MaxFrontmatterChars];

        var frontmatter = FrontmatterRegex().Match(raw);
        if (!frontmatter.Success) return null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in frontmatter.Groups["body"].Value.Split('\n'))
        {
            var clean = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(clean) || clean.TrimStart().StartsWith('#')) continue;
            var colon = clean.IndexOf(':');
            if (colon <= 0) continue;
            fields[clean[..colon].Trim()] = Unquote(clean[(colon + 1)..].Trim());
        }

        if (!fields.TryGetValue("styleGuideId", out var id) || string.IsNullOrWhiteSpace(id)) return null;
        if (!fields.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title)) return null;
        if (!fields.TryGetValue("appliesTo", out var appliesRaw) || string.IsNullOrWhiteSpace(appliesRaw)) return null;

        StyleGuideAppliesTo? applies;
        try
        {
            applies = JsonSerializer.Deserialize<StyleGuideAppliesTo>(appliesRaw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
        if (applies == null) return null;

        fields.TryGetValue("promptSummary", out var promptSummary);
        fields.TryGetValue("summary", out var summary);
        fields.TryGetValue("version", out var version);

        var docsRoot = Path.Combine(repositoryRoot, "docs");
        var relPath = Path.GetRelativePath(docsRoot, path).Replace('\\', '/');
        return new ProjectStyleGuide(
            id.Trim(),
            title.Trim(),
            relPath,
            string.IsNullOrWhiteSpace(summary) ? title.Trim() : summary.Trim(),
            string.IsNullOrWhiteSpace(promptSummary) ? summary?.Trim() ?? title.Trim() : promptSummary.Trim(),
            string.IsNullOrWhiteSpace(version) ? "1" : version.Trim(),
            Normalize(applies));
    }

    internal static List<string> DetectTechnologies(string repositoryRoot)
    {
        var technologies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(repositoryRoot)) return [];

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((repositoryRoot, 0));
        var visited = 0;

        while (queue.Count > 0 && visited++ < MaxScannedDirectories)
        {
            var (dir, depth) = queue.Dequeue();
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var extension = Path.GetExtension(file);
                if (string.Equals(name, "angular.json", StringComparison.OrdinalIgnoreCase))
                    technologies.Add("angular");
                if (string.Equals(name, "tsconfig.json", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".ts", StringComparison.OrdinalIgnoreCase))
                    technologies.Add("typescript");
                if (string.Equals(extension, ".scss", StringComparison.OrdinalIgnoreCase))
                    technologies.Add("scss");
                if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    technologies.Add("dotnet");
                    technologies.Add("csharp");
                }
                if (string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase))
                    DetectPackageTechnologies(file, technologies);
            }

            if (depth >= MaxScanDepth) continue;
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                if (!IgnoredDirectories.Contains(Path.GetFileName(child)))
                    queue.Enqueue((child, depth + 1));
            }
        }

        return technologies.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void DetectPackageTechnologies(string path, HashSet<string> technologies)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var sectionName in new[] { "dependencies", "devDependencies", "peerDependencies" })
            {
                if (!doc.RootElement.TryGetProperty(sectionName, out var section)
                    || section.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in section.EnumerateObject())
                {
                    if (property.Name.StartsWith("@angular/", StringComparison.OrdinalIgnoreCase))
                        technologies.Add("angular");
                    if (string.Equals(property.Name, "typescript", StringComparison.OrdinalIgnoreCase))
                        technologies.Add("typescript");
                    if (property.Name is "sass" or "node-sass")
                        technologies.Add("scss");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A malformed package manifest should not hide guides selected by
            // other project markers.
            SilentCatch.Note(ex, $"ProjectStyleGuideService: ignored unreadable package manifest {path}");
        }
    }

    private static bool AppliesToProject(
        StyleGuideAppliesTo appliesTo,
        string projectName,
        IReadOnlyCollection<string> technologies)
    {
        var projectMatch = appliesTo.Projects.Count > 0
            && appliesTo.Projects.Any(value => value == "*"
                || string.Equals(value, projectName, StringComparison.OrdinalIgnoreCase));
        var technologyMatch = appliesTo.Technologies.Count > 0
            && (appliesTo.Technologies.Contains("*", StringComparer.OrdinalIgnoreCase)
                || appliesTo.Technologies.Intersect(technologies, StringComparer.OrdinalIgnoreCase).Any());
        return projectMatch && technologyMatch;
    }

    private static bool DeclaresStyleGuide(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            var buffer = new char[4_096];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read).Contains("styleGuideId:", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, $"ProjectStyleGuideService: could not inspect declared guide {path}");
            return false;
        }
    }

    private static StyleGuideAppliesTo Normalize(StyleGuideAppliesTo appliesTo)
        => new(
            Clean(appliesTo.Projects),
            Clean(appliesTo.Technologies),
            Clean(appliesTo.TaskAreas));

    private static List<string> Clean(IEnumerable<string>? values)
        => (values ?? [])
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Unquote(string value)
        => value.Length >= 2
           && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    [GeneratedRegex(@"\A---\r?\n(?<body>.*?)\r?\n---(?:\r?\n|\z)", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex FrontmatterRegex();
}

public sealed record StyleGuideAppliesTo(
    List<string> Projects,
    List<string> Technologies,
    List<string> TaskAreas)
{
    public StyleGuideAppliesTo() : this([], [], []) { }
}

public sealed record ProjectStyleGuide(
    string Id,
    string Title,
    string RelPath,
    string Summary,
    string PromptSummary,
    string Version,
    StyleGuideAppliesTo AppliesTo);

public sealed record ProjectStyleGuideCatalogue(
    string ProjectName,
    string RepositoryRoot,
    List<string> Technologies,
    List<ProjectStyleGuide> Guides,
    List<ProjectStyleGuideWarning> Warnings);

public sealed record ProjectStyleGuideWarning(string RelPath, string Message);
