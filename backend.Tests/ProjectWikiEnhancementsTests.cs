using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the wiki-enhancement backend surface: frontmatter provenance
/// parsing, the user-organisation manifest round-trip + sanitisation, the
/// model-trailer extraction, and per-file git history. The pure parsers are
/// asserted directly; the manifest + git lookups use a temp project / repo.
/// </summary>
public class ProjectWikiEnhancementsTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectWikiEnhancementsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-enh-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ---- ParseWikiMetadata (pure) ----

    [Fact]
    public void ParseWikiMetadata_ExtractsProvenanceFields()
    {
        var md = "---\n"
            + "model: Claude Opus 4.8\n"
            + "last-distilled: 2026-06-09T10:00:00Z\n"
            + "status: accept\n"
            + "task-key: ASS-1709\n"
            + "run-count: 3\n"
            + "why: \"distilled after review\"\n"
            + "---\n\n# Title\nbody";

        var meta = ProjectDocsService.ParseWikiMetadata(md);

        Assert.True(meta.HasFrontmatter);
        Assert.Equal("Claude Opus 4.8", meta.Model);
        Assert.Equal("2026-06-09T10:00:00Z", meta.UpdatedAt);
        Assert.Equal("accept", meta.Status);
        Assert.Equal("ASS-1709", meta.TaskKey);
        Assert.Equal("3", meta.RunCount);
        Assert.Equal("distilled after review", meta.Reason);
    }

    [Fact]
    public void ParseWikiMetadata_NoFrontmatter_ReturnsEmpty()
    {
        var meta = ProjectDocsService.ParseWikiMetadata("# Just a heading\n\nNo frontmatter here.");

        Assert.False(meta.HasFrontmatter);
        Assert.Null(meta.Model);
        Assert.Null(meta.UpdatedAt);
    }

    [Fact]
    public void ParseWikiMetadata_FallsBackToReasonSynonyms()
    {
        var meta = ProjectDocsService.ParseWikiMetadata("---\nsummary: keeps the rationale\n---\n# t");
        Assert.Equal("keeps the rationale", meta.Reason);
    }

    // ---- SanitizeOrganization (pure) ----

    [Fact]
    public void SanitizeOrganization_DropsBadNodes_NormalisesTitlesAndPaths()
    {
        var org = new WikiOrganization(7, new List<WikiOrgNode>
        {
            new("g1", "group", "  Architecture  ", "stray/path.md", null, 0),
            new("d1", "doc", null, "domain\\runner.md", "g1", 1),
            new("", "doc", "no id", "x.md", null, 2),
            new("bad", "folder", "wrong type", null, null, 3),
        });

        var clean = ProjectDocsService.SanitizeOrganization(org);

        Assert.Equal(1, clean.Version);
        Assert.Equal(2, clean.Nodes.Count);

        var g = clean.Nodes.Single(n => n.Id == "g1");
        Assert.Equal("Architecture", g.Title);
        Assert.Null(g.RelPath); // a group never carries a relPath

        var d = clean.Nodes.Single(n => n.Id == "d1");
        Assert.Equal("domain/runner.md", d.RelPath); // backslash normalised
        Assert.Equal("g1", d.ParentId);
    }

    [Fact]
    public void SanitizeOrganization_NullInput_ReturnsEmptyVersionedManifest()
    {
        var clean = ProjectDocsService.SanitizeOrganization(null);
        Assert.Empty(clean.Nodes);
        Assert.Equal(1, clean.Version);
    }

    // ---- Organisation manifest round-trip (filesystem) ----

    [Fact]
    public void WriteThenGetWikiOrganization_RoundTrips()
    {
        var projectRoot = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(projectRoot);
        var docs = BuildDocsService(("Proj", projectRoot));

        var org = new WikiOrganization(1, new List<WikiOrgNode>
        {
            new("g1", "group", "Themes", null, null, 0),
            new("d1", "doc", "Renamed", "guide.md", "g1", 0),
        });

        Assert.True(docs.WriteWikiOrganization("Proj", org));

        var read = docs.GetWikiOrganization("Proj");
        Assert.NotNull(read);
        Assert.Equal(2, read!.Nodes.Count);
        Assert.Equal("Themes", read.Nodes.Single(n => n.Id == "g1").Title);
        Assert.Equal("Renamed", read.Nodes.Single(n => n.Id == "d1").Title);
        Assert.True(File.Exists(Path.Combine(projectRoot, "docs", ".wiki-organization.json")));
    }

    [Fact]
    public void GetWikiOrganization_NoManifest_ReturnsEmptyButValid()
    {
        var projectRoot = Path.Combine(_tempDir, "empty-proj");
        Directory.CreateDirectory(projectRoot);
        var docs = BuildDocsService(("Empty", projectRoot));

        var org = docs.GetWikiOrganization("Empty");

        Assert.NotNull(org);
        Assert.Empty(org!.Nodes);
    }

    [Fact]
    public void GetWikiOrganization_UnknownProject_ReturnsNull()
    {
        var docs = BuildDocsService(("Known", Path.Combine(_tempDir, "known")));
        Assert.Null(docs.GetWikiOrganization("Nope"));
    }

    // ---- ParseModelTrailer (pure) ----

    [Theory]
    [InlineData("Claude Opus 4.8 <noreply@anthropic.com>", "Claude Opus 4.8")]
    [InlineData("Codex <x@y.z>\nClaude <a@b.c>", "Codex")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void ParseModelTrailer_StripsAddress_TakesFirst(string input, string? expected)
    {
        Assert.Equal(expected, GitService.ParseModelTrailer(input));
    }

    // ---- GetFileHistory + GetLatestModelForPath (real git) ----

    [Fact]
    public void GetFileHistory_ReturnsCommitsNewestFirst_AndModelFromTrailer()
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        var docPath = Path.Combine(repoRoot, "docs", "wiki", "note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);

        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");

        File.WriteAllText(docPath, "# Note\nfirst");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "create note");

        File.WriteAllText(docPath, "# Note\nsecond");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "update note",
            "-m", "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>");

        var git = BuildGitService(("Repo", repoRoot));

        var history = git.GetFileHistory(repoRoot, "docs/wiki/note.md");
        Assert.Equal(2, history.Count);
        Assert.Equal("update note", history[0].Subject); // newest first
        Assert.Equal("create note", history[1].Subject);

        var model = git.GetLatestModelForPath(repoRoot, "docs/wiki/note.md");
        Assert.Equal("Claude Opus 4.8", model);
    }

    [Fact]
    public void GetFileHistory_UnknownPath_ReturnsEmpty()
    {
        var repoRoot = Path.Combine(_tempDir, "repo2");
        Directory.CreateDirectory(repoRoot);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "seed");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "seed");

        var git = BuildGitService(("Repo2", repoRoot));
        Assert.Empty(git.GetFileHistory(repoRoot, "docs/does-not-exist.md"));
    }

    // ---- helpers ----

    private ProjectDocsService BuildDocsService(params (string Name, string RootPath)[] entries)
        => new(BuildScanner(entries), NullLogger<ProjectDocsService>.Instance);

    private GitService BuildGitService(params (string Name, string RootPath)[] entries)
        => new(NullLogger<GitService>.Instance, BuildScanner(entries), BuildConfig(entries));

    private static IConfiguration BuildConfig((string Name, string RootPath)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"WatchPaths:{i}:Name"] = entries[i].Name;
            dict[$"WatchPaths:{i}:RootPath"] = entries[i].RootPath;
            dict[$"WatchPaths:{i}:Path"] = Path.Combine(entries[i].RootPath, ".orchestrator", "jobs");
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static TaskScannerService BuildScanner((string Name, string RootPath)[] entries)
    {
        var config = BuildConfig(entries);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private static void RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }

    private static void RunGitArgs(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }
}
