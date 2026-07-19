using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Registry;

/// <summary>
/// A single detected suggestion for a project URL, offered to the UI as a
/// one-click "fill from suggestion" chip. Never auto-applied - the user picks
/// which (if any) to turn into a <see cref="ProjectUrlRecord"/>.
/// </summary>
public sealed record ProjectUrlSuggestion
{
    public string Label { get; init; } = "";
    /// <summary>Derived <c>http://localhost:{port}</c> when a port is known; null otherwise.</summary>
    public string? Url { get; init; }
    public string Command { get; init; } = "";
    public string? Cwd { get; init; }
    public int? Port { get; init; }
    /// <summary><c>package-json</c> | <c>angular-json</c> | <c>readme</c>.</summary>
    public string Source { get; init; } = "package-json";
}

/// <summary>
/// Reads a project's repository (<c>package.json</c> scripts, Angular
/// <c>angular.json</c> per-project ports, and <c>README.md</c> fenced run
/// commands) and produces <see cref="ProjectUrlSuggestion"/>s. Pure read-only:
/// never writes, never spawns, never mutates the registry. All parsing is
/// best-effort - malformed or missing files yield fewer suggestions, never an
/// exception.
/// </summary>
public sealed class ProjectUrlDetectionService
{
    private static readonly JsonDocumentOptions JsonOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // Run-command keywords that mark a script/README line as a startable server.
    private static readonly string[] RunKeywords =
        ["ng serve", "vite", "next dev", "next start", "http-server", "serve", "dotnet run",
         "npm start", "npm run", "npx", "nx serve", "react-scripts start"];

    // Port extraction: --port 4200, --port=4200, -p 4200, :4200, PORT=4200.
    private static readonly Regex PortRegex = new(
        @"(?:--port[=\s]+|(?<!\d)-p[=\s]+|localhost:|127\.0\.0\.1:|PORT[=\s]+)(\d{2,5})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger<ProjectUrlDetectionService> _logger;

    public ProjectUrlDetectionService(ILogger<ProjectUrlDetectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Detect suggestions for a project. Uses <see cref="ProjectRecord.RepositoryPath"/>
    /// (falling back to <see cref="ProjectRecord.RootPath"/>) as the repo root.
    /// Returns an empty list when neither is set or the folder is missing.
    /// </summary>
    public IReadOnlyList<ProjectUrlSuggestion> Detect(ProjectRecord project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var repoRoot = project.RepositoryPath ?? project.RootPath;
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return [];

        var results = new List<ProjectUrlSuggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try { DetectFromPackageJson(repoRoot, results, seen); }
        catch (Exception ex) { _logger.LogDebug(ex, "url-detect-package-json-failed root={Root}", repoRoot); }

        try { DetectFromAngularJson(repoRoot, results, seen); }
        catch (Exception ex) { _logger.LogDebug(ex, "url-detect-angular-json-failed root={Root}", repoRoot); }

        try { DetectFromReadme(repoRoot, results, seen); }
        catch (Exception ex) { _logger.LogDebug(ex, "url-detect-readme-failed root={Root}", repoRoot); }

        _logger.LogInformation(
            "url-suggestions-detected project={Id} root={Root} count={Count}",
            project.Id, repoRoot, results.Count);
        return results;
    }

    private static void DetectFromPackageJson(string repoRoot, List<ProjectUrlSuggestion> results, HashSet<string> seen)
    {
        var path = Path.Combine(repoRoot, "package.json");
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path), JsonOpts);
        if (!doc.RootElement.TryGetProperty("scripts", out var scripts) ||
            scripts.ValueKind != JsonValueKind.Object)
            return;

        foreach (var script in scripts.EnumerateObject())
        {
            var name = script.Name;
            var command = script.Value.GetString() ?? "";
            if (!LooksRunnable(name, command)) continue;

            var port = ExtractPort(command);
            var runCommand = string.Equals(name, "start", StringComparison.OrdinalIgnoreCase)
                ? "npm start"
                : $"npm run {name}";
            var key = $"pkg:{name}";
            if (!seen.Add(key)) continue;
            results.Add(new ProjectUrlSuggestion
            {
                Label = Humanise(name),
                Url = port.HasValue ? $"http://localhost:{port.Value}" : null,
                Command = runCommand,
                Cwd = repoRoot,
                Port = port,
                Source = "package-json",
            });
        }
    }

    private static void DetectFromAngularJson(string repoRoot, List<ProjectUrlSuggestion> results, HashSet<string> seen)
    {
        var path = Path.Combine(repoRoot, "angular.json");
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path), JsonOpts);
        if (!doc.RootElement.TryGetProperty("projects", out var projects) ||
            projects.ValueKind != JsonValueKind.Object)
            return;

        foreach (var proj in projects.EnumerateObject())
        {
            // architect.serve.options.port (Angular < 17) or
            // targets.serve.options.port (newer schema).
            var port = TryReadServePort(proj.Value, "architect")
                    ?? TryReadServePort(proj.Value, "targets");
            if (!port.HasValue) continue;
            var key = $"ng:{proj.Name}";
            if (!seen.Add(key)) continue;
            results.Add(new ProjectUrlSuggestion
            {
                Label = Humanise(proj.Name),
                Url = $"http://localhost:{port.Value}",
                Command = $"ng serve {proj.Name}",
                Cwd = repoRoot,
                Port = port,
                Source = "angular-json",
            });
        }
    }

    private static int? TryReadServePort(JsonElement projectElement, string targetsKey)
    {
        if (!projectElement.TryGetProperty(targetsKey, out var targets) ||
            targets.ValueKind != JsonValueKind.Object)
            return null;
        if (!targets.TryGetProperty("serve", out var serve) ||
            !serve.TryGetProperty("options", out var options) ||
            !options.TryGetProperty("port", out var portEl))
            return null;
        return portEl.ValueKind == JsonValueKind.Number && portEl.TryGetInt32(out var p) ? p : null;
    }

    private static void DetectFromReadme(string repoRoot, List<ProjectUrlSuggestion> results, HashSet<string> seen)
    {
        var path = Directory.EnumerateFiles(repoRoot)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "README.md", StringComparison.OrdinalIgnoreCase));
        if (path == null) return;

        var text = File.ReadAllText(path);
        foreach (var block in ExtractFencedBlocks(text))
        {
            foreach (var rawLine in block.Split('\n'))
            {
                var line = rawLine.Trim().TrimStart('$', ' ', '>').Trim();
                if (line.Length == 0) continue;
                if (!RunKeywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                // Skip install-only lines that merely mention npm.
                if (line.StartsWith("npm install", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("npm i ", StringComparison.OrdinalIgnoreCase))
                    continue;

                var port = ExtractPort(line) ?? ExtractPort(block);
                var key = $"readme:{line}";
                if (!seen.Add(key)) continue;
                results.Add(new ProjectUrlSuggestion
                {
                    Label = "From README",
                    Url = port.HasValue ? $"http://localhost:{port.Value}" : null,
                    Command = line,
                    Cwd = repoRoot,
                    Port = port,
                    Source = "readme",
                });
            }
        }
    }

    private static IEnumerable<string> ExtractFencedBlocks(string markdown)
    {
        // Match ``` ... ``` fenced blocks (with optional language hint).
        foreach (Match m in Regex.Matches(markdown, "```[^\n]*\n(.*?)```", RegexOptions.Singleline))
            yield return m.Groups[1].Value;
    }

    private static bool LooksRunnable(string name, string command)
    {
        var lname = name.ToLowerInvariant();
        if (lname is "start" or "dev" or "serve") return true;
        if (lname.StartsWith("serve", StringComparison.Ordinal) ||
            lname.StartsWith("start", StringComparison.Ordinal) ||
            lname.StartsWith("dev", StringComparison.Ordinal) ||
            lname.Contains("website") || lname.Contains("preview") || lname.Contains("workbench"))
            return true;
        return RunKeywords.Any(k => command.Contains(k, StringComparison.OrdinalIgnoreCase))
            && command.Contains("port", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ExtractPort(string text)
    {
        var m = PortRegex.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var port) && port is > 0 and <= 65535)
            return port;
        return null;
    }

    private static string Humanise(string scriptName)
    {
        var cleaned = scriptName.Replace(':', ' ').Replace('-', ' ').Replace('_', ' ').Trim();
        if (cleaned.Length == 0) return scriptName;
        return char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
    }
}
