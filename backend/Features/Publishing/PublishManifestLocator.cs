using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Publishing;

/// <summary>
/// A located package manifest: the ecosystem, the package name, and the
/// repo-relative source root (the directory that contains the manifest, forward
/// slashed, "" for the repository root). The source root is the include-prefix
/// for the package's pending-delta path scope ("Package-Quellpfade").
/// </summary>
public sealed record ManifestInfo(string Ecosystem, string? PackageName, string SourceRootRelDir);

/// <summary>
/// PUB-1 - locates a project's package manifest on disk so the derivation can
/// name the package and scope its source paths. Reads the repository working tree
/// directly (never the task-storage tree, so the TaskFolderAccess isolation rule
/// does not apply): npm from <c>package.json</c>, NuGet from a packable
/// <c>.csproj</c>. Bounded BFS that skips build/vendor noise and the website
/// folder so a website's own <c>package.json</c> never masquerades as the
/// distributable package. Prefers the shallowest manifest (repo root or a single
/// <c>src/</c> hop), matching how single-package repos are laid out.
/// </summary>
public static class PublishManifestLocator
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", ".github", "dist", "build", "out",
        "coverage", ".orchestrator", "test-results", "playwright-report",
    };

    private const int MaxDepth = 4;

    /// <summary>
    /// Finds the npm package manifest, skipping the given website roots (a
    /// website's own package.json is not the distributable). Returns null when no
    /// package.json is found. When several exist, the shallowest wins.
    /// </summary>
    public static ManifestInfo? LocateNpm(string repoRoot, IReadOnlyCollection<string> websiteRoots)
    {
        var best = EnumerateManifests(repoRoot, "package.json", websiteRoots)
            .OrderBy(f => Depth(repoRoot, f))
            .FirstOrDefault();
        if (best == null) return null;

        string? name = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(best));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("name", out var n) &&
                n.ValueKind == JsonValueKind.String)
            {
                name = n.GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A malformed / unreadable manifest still yields a target (scoped to
            // its folder); the name is simply unknown.
            AgentStudio.Diagnostics.SilentCatch.Note(ex, "PublishManifestLocator: unreadable package.json");
        }

        return new ManifestInfo(PublishEcosystems.Npm, name, RelDir(repoRoot, best));
    }

    /// <summary>
    /// Finds a packable NuGet manifest: a <c>.csproj</c> that is explicitly
    /// packable (<c>IsPackable</c>, <c>PackageId</c>, <c>GeneratePackageOnBuild</c>,
    /// or a <c>Version</c>/<c>PackageVersion</c>) and is not obviously a test
    /// project. Returns null when none qualifies. The shallowest qualifying
    /// project wins. The package name is the <c>PackageId</c> if present, else the
    /// project file name.
    /// </summary>
    public static ManifestInfo? LocateNuGet(string repoRoot, IReadOnlyCollection<string> websiteRoots)
    {
        var candidates = EnumerateManifests(repoRoot, "*.csproj", websiteRoots)
            .OrderBy(f => Depth(repoRoot, f))
            .ToList();

        foreach (var csproj in candidates)
        {
            string text;
            try { text = File.ReadAllText(csproj); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            if (IsTestProject(csproj, text)) continue;
            if (!IsPackable(text)) continue;

            var packageId = FirstGroup(text, @"<PackageId>\s*([^<\s]+)\s*</PackageId>")
                            ?? Path.GetFileNameWithoutExtension(csproj);
            return new ManifestInfo(PublishEcosystems.NuGet, packageId, RelDir(repoRoot, csproj));
        }
        return null;
    }

    private static bool IsPackable(string csprojText)
    {
        if (Regex.IsMatch(csprojText, @"<IsPackable>\s*false\s*</IsPackable>", RegexOptions.IgnoreCase))
            return false;
        return Regex.IsMatch(csprojText, @"<IsPackable>\s*true\s*</IsPackable>", RegexOptions.IgnoreCase)
            || Regex.IsMatch(csprojText, @"<GeneratePackageOnBuild>\s*true\s*</GeneratePackageOnBuild>", RegexOptions.IgnoreCase)
            || csprojText.Contains("<PackageId>", StringComparison.OrdinalIgnoreCase)
            || csprojText.Contains("<PackageVersion>", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(csprojText, @"<Version>\s*\d", RegexOptions.IgnoreCase);
    }

    private static bool IsTestProject(string path, string text)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase))
            return true;
        return text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(text, @"<IsTestProject>\s*true", RegexOptions.IgnoreCase);
    }

    private static IEnumerable<string> EnumerateManifests(
        string repoRoot, string pattern, IReadOnlyCollection<string> websiteRoots)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) yield break;
        var websiteSet = new HashSet<string>(
            websiteRoots.Select(w => w.Replace('\\', '/').Trim('/')),
            StringComparer.OrdinalIgnoreCase);

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((repoRoot, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, pattern); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }
            foreach (var f in files) yield return f;

            if (depth >= MaxDepth) continue;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { subs = []; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (SkipDirs.Contains(name)) continue;
                var rel = RelDir(repoRoot, Path.Combine(sub, "x"));
                if (websiteSet.Contains(rel.TrimEnd('/'))) continue;
                queue.Enqueue((sub, depth + 1));
            }
        }
    }

    private static int Depth(string repoRoot, string file)
    {
        var rel = RelDir(repoRoot, file);
        return rel.Length == 0 ? 0 : rel.Count(c => c == '/') + 1;
    }

    /// <summary>Repo-relative directory of a file, forward-slashed, "" for the repo root.</summary>
    internal static string RelDir(string repoRoot, string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? repoRoot;
        var rel = Path.GetRelativePath(Path.GetFullPath(repoRoot), dir).Replace('\\', '/');
        return rel is "." ? string.Empty : rel;
    }

    private static string? FirstGroup(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}
