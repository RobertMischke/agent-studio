using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Generates the demo store twice, once per independent root, and keeps both
/// for the lifetime of the test class. The stores are immutable input, so
/// seeding once and sharing it keeps the class to two node runs instead of two
/// per fact.
/// </summary>
public sealed class PinnedDemoSeedFixture : IDisposable
{
    public string First { get; } = NewRoot();
    public string Second { get; } = NewRoot();

    /// <summary>False when node is unavailable, which every fact asserts on.</summary>
    public bool Seeded { get; }

    public PinnedDemoSeedFixture() => Seeded = Seed(First) && Seed(Second);

    public void Dispose()
    {
        try { Directory.Delete(First, true); } catch (IOException) { }
        try { Directory.Delete(Second, true); } catch (IOException) { }
    }

    private static bool Seed(string root)
    {
        var script = Path.Combine(RepoRoot(), "scripts", "seed-demo-workspace.mjs");
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        try
        {
            using var process = Process.Start(start);
            if (process == null) return false;
            // Drain both pipes concurrently: a seed that fails loudly can fill
            // the stderr buffer, and a sequential read would deadlock instead
            // of reporting the cause.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000), "seed-demo-workspace.mjs did not exit within 120s.");
            Task.WaitAll(output, error);
            Assert.True(process.ExitCode == 0, $"seed-demo-workspace.mjs failed: {error.Result}");
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "pinned-demo-seed-" + Guid.NewGuid().ToString("N"));

    internal static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory })
        {
            var current = start;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
                current = Path.GetDirectoryName(current);
            }
        }
        throw new InvalidOperationException("agent-taskboard.sln was not found above the test source or output directory.");
    }
}

/// <summary>
/// Acceptance for the public-demo seed content (AGT-W34 slice S1). The pinned
/// seed is only useful if the product can actually discover what it writes, so
/// these tests run the real generator and then read its output back through the
/// production Dossier catalogue and task scanner rather than re-parsing JSON by
/// hand. Byte-identical regeneration is asserted first: every other guarantee
/// here depends on the store being reproducible.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class PinnedDemoSeedContentTests : IClassFixture<PinnedDemoSeedFixture>
{
    private static readonly string[] AllLanes =
    [
        "0-backlog", "1-preparation", "2-ready", "3-progress", "4-auto-review",
        "5-human-review", "5e-escalated", "6-completed", "7-archive",
    ];

    private readonly string _first;
    private readonly string _second;
    private readonly bool _seeded;

    public PinnedDemoSeedContentTests(PinnedDemoSeedFixture fixture)
    {
        _first = fixture.First;
        _second = fixture.Second;
        _seeded = fixture.Seeded;
    }

    [Fact]
    public void Seed_RegeneratesByteIdenticalContent()
    {
        Assert.True(_seeded, "The demo seed script did not run; node must be on PATH.");

        var left = HashTree(_first);
        var right = HashTree(_second);

        Assert.Equal(left.Keys.OrderBy(x => x, StringComparer.Ordinal), right.Keys.OrderBy(x => x, StringComparer.Ordinal));
        foreach (var (relPath, hash) in left) Assert.Equal(hash, right[relPath]);
    }

    [Fact]
    public void Seed_CoversEveryBoardLaneInTheDemoProjects()
    {
        Assert.True(_seeded, "The demo seed script did not run; node must be on PATH.");

        var lanes = ReadTaskJson(_first).Select(task => task.GetProperty("state").GetString()).ToHashSet();

        foreach (var lane in AllLanes) Assert.Contains(lane, lanes);
    }

    [Fact]
    public void Seed_DiscoversSixDossiersAcrossEveryLifecycleState()
    {
        Assert.True(_seeded, "The demo seed script did not run; node must be on PATH.");

        var items = Catalogue().List("Demo App", includeHistory: true)!.Items;

        Assert.All(items, item => Assert.True(item.Valid, $"{item.Id}: {item.Error}"));
        Assert.Equal(
            new[] { "DEMO-W1", "DEMO-W2", "DEMO-W3", "DEMO-W4", "DEMO-W5", "DEMO-W6" },
            items.Select(item => item.Key).OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "active", "archived", "decided", "decided", "decision-pending", "documented" },
            items.Select(item => item.Status).OrderBy(status => status, StringComparer.Ordinal));
        // The five stored lifecycle states the viewer and the history list read.
        Assert.Equal(
            new[] { "decided", "documented", "done", "in-progress", "review-requested" },
            items.Select(item => item.LifecycleState).Distinct().OrderBy(state => state, StringComparer.Ordinal));
    }

    [Fact]
    public void Seed_MakesTheGalleryVisibleFromBothDirections()
    {
        Assert.True(_seeded, "The demo seed script did not run; node must be on PATH.");
        var catalogue = Catalogue();
        var items = catalogue.List("Demo App", includeHistory: true)!.Items.ToDictionary(item => item.Key!, StringComparer.Ordinal);

        // Descriptor keys are the compatibility bridge the viewer renders as
        // card points; references.workbenches is the canonical reverse edge.
        Assert.Equal(["DEMO-10"], items["DEMO-W1"].SourceTaskKeys);
        Assert.Equal(
            new[] { "DEMO-12", "DEMO-13", "DEMO-14", "DEMO-15" },
            items["DEMO-W4"].RelatedTaskKeys);
        Assert.Empty(items["DEMO-W3"].RelatedTaskKeys);
        Assert.Equal(
            new[] { "DEMO-W1", "DEMO-W2", "DEMO-W3", "DEMO-W4", "DEMO-W5", "DEMO-W6" },
            catalogue.KnownKeys("Demo App").OrderBy(key => key, StringComparer.Ordinal));

        var referencedKeys = ReadTaskJson(_first)
            .Where(task => task.TryGetProperty("references", out _))
            .SelectMany(task => task.GetProperty("references").GetProperty("workbenches")
                .EnumerateArray().Select(value => value.GetString()!))
            .Distinct()
            .OrderBy(key => key, StringComparer.Ordinal);
        Assert.Equal(
            new[] { "DEMO-W1", "DEMO-W2", "DEMO-W3", "DEMO-W4", "DEMO-W5", "DEMO-W6" },
            referencedKeys);
    }

    [Fact]
    public void Seed_RecordsDecisionReceiptsAndOpenDecisionPoints()
    {
        Assert.True(_seeded, "The demo seed script did not run; node must be on PATH.");

        var items = Catalogue().List("Demo App", includeHistory: true)!.Items.ToDictionary(item => item.Key!, StringComparer.Ordinal);

        Assert.Null(items["DEMO-W1"].Decision);
        Assert.Null(items["DEMO-W2"].Decision);
        Assert.Equal(2, items["DEMO-W2"].OpenDecisionCount);

        var archived = items["DEMO-W6"].Decision;
        Assert.NotNull(archived);
        Assert.Equal("archive", archived!.Outcome);
        Assert.Equal("succeeded", archived.State);
        Assert.False(string.IsNullOrWhiteSpace(archived.Reason));
        Assert.False(string.IsNullOrWhiteSpace(archived.ConfirmedBy));
        Assert.Equal("2026-08-09T08:48:00Z", archived.DecidedAt);
        Assert.Empty(archived.SpawnedTaskKeys);

        Assert.Equal(
            new[] { "DEMO-12", "DEMO-13", "DEMO-14", "DEMO-15" },
            items["DEMO-W4"].Decision!.SpawnedTaskKeys);
        Assert.Empty(items["DEMO-W3"].Decision!.SpawnedTaskKeys);
    }

    [Fact]
    public void Seed_KeepsTheWikiTreesSelfContainedAndOffline()
    {
        Assert.True(_seeded, "The demo seed script did not run; node must be on PATH.");

        var pages = DocumentFiles(_first).ToList();
        Assert.Equal(7, pages.Count(page => page.Project == "demo-app" && page.RelPath.EndsWith(".md", StringComparison.Ordinal)));
        Assert.Equal(6, pages.Count(page => page.Project == "demo-platform" && page.RelPath.EndsWith(".md", StringComparison.Ordinal)));

        foreach (var page in pages)
        {
            var text = File.ReadAllText(page.FullPath);
            Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http://", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);
            // Every reader-visible document declares its provenance, markdown
            // in its footer and a Dossier in its header and closing note. The
            // Dossier descriptors alongside them are metadata, not pages.
            if (page.RelPath.EndsWith(".md", StringComparison.Ordinal)
                || page.RelPath.EndsWith(".html", StringComparison.Ordinal))
                Assert.Contains("Pinned demo data", text, StringComparison.Ordinal);
        }

        foreach (var link in pages.Where(page => page.RelPath.EndsWith(".md", StringComparison.Ordinal))
                     .SelectMany(page => MarkdownLinks(page)))
        {
            Assert.True(File.Exists(link.Target), $"{link.Source} links to a missing page: {link.Target}");
        }
    }

    [Fact]
    public void Seed_LeavesNoSourceRepositoryIdentityInGeneratedContent()
    {
        Assert.True(_seeded, "The demo seed script did not run; node must be on PATH.");

        // The dossier data-boundary tables: the demo content is invented, so no
        // key, project, host, or path from the producing repository may survive.
        string[] forbidden =
        [
            "agent-taskboard", "AGT-", "PROJ-", "WEB-", "ASS-", "agent-studio-workspace",
            "C:\\Projects", "C:/Projects", "/home/", "/Users/", "http://", "https://",
        ];

        // Companion sidecars are scanned too. Their one exemption is the
        // `$schema` identifier, which is a fixed product contract string the
        // backend recognises rather than seeded content.
        foreach (var file in DocumentFiles(_first, includeCompanions: true))
        {
            var text = string.Join('\n', File.ReadAllLines(file.FullPath)
                .Where(line => !line.TrimStart().StartsWith("\"$schema\":", StringComparison.Ordinal)));
            foreach (var needle in forbidden)
                Assert.DoesNotContain(needle, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private WorkbenchCatalogueService Catalogue()
    {
        var demoApp = Path.Combine(_first, "projects", "demo-app");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Demo App",
            ["WatchPaths:0:RootPath"] = demoApp,
            ["WatchPaths:0:Path"] = demoApp,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var mutations = new ManagedRepositoryMutationService(
            git, pushQueue: null, logger: NullLogger<ManagedRepositoryMutationService>.Instance);
        return new WorkbenchCatalogueService(scanner, registry, git, repositoryMutations: mutations);
    }

    private static IEnumerable<JsonElement> ReadTaskJson(string root)
    {
        var projects = Path.Combine(root, "projects");
        foreach (var file in Directory.EnumerateFiles(projects, "task.json", SearchOption.AllDirectories))
            yield return JsonDocument.Parse(File.ReadAllText(file)).RootElement;
    }

    private static IEnumerable<DocumentFile> DocumentFiles(string root, bool includeCompanions = false)
    {
        foreach (var project in new[] { "demo-app", "demo-platform" })
        {
            var docs = Path.Combine(root, "projects", project, "docs");
            foreach (var file in Directory.EnumerateFiles(docs, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(docs, file).Replace('\\', '/');
                if (relPath.StartsWith("app/", StringComparison.Ordinal)) continue;
                var isCompanion = relPath.EndsWith(".meta.json", StringComparison.Ordinal);
                if (isCompanion && !includeCompanions) continue;
                yield return new DocumentFile(project, relPath, file);
            }
        }
    }

    private static IEnumerable<(string Source, string Target)> MarkdownLinks(DocumentFile page)
    {
        var directory = Path.GetDirectoryName(page.FullPath)!;
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(page.FullPath), @"\]\((?<target>[^)\s#]+)\)"))
        {
            var target = match.Groups["target"].Value;
            if (target.Contains("://", StringComparison.Ordinal)) continue;
            yield return (page.RelPath, Path.GetFullPath(Path.Combine(directory, target)));
        }
    }

    private static Dictionary<string, string> HashTree(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
            result[relPath] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return result;
    }

    private sealed record DocumentFile(string Project, string RelPath, string FullPath);
}
