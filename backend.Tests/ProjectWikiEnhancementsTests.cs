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

    // ---- GetWikiTree (physical docs/ hierarchy) ----

    [Fact]
    public void GetWikiTree_ReflectsPhysicalStructure_FoldersFirst_IncludesHtml_StripsOrderPrefix()
    {
        var projectRoot = Path.Combine(_tempDir, "tree-proj");
        var docsDir = Path.Combine(projectRoot, "docs");
        Directory.CreateDirectory(Path.Combine(docsDir, "01-concepts"));
        File.WriteAllText(Path.Combine(docsDir, "README.md"), "# Index\n");
        File.WriteAllText(Path.Combine(docsDir, "01-concepts", "10-overview.md"), "# Overview\n");
        File.WriteAllText(Path.Combine(docsDir, "01-concepts", "page.html"), "<h1>HTML page</h1>");
        File.WriteAllText(Path.Combine(docsDir, "01-concepts", "page.metadata.json"),
            "{ \"title\": \"Page metadata\", \"drift\": { \"grade\": \"B\" } }");
        File.WriteAllText(Path.Combine(docsDir, "01-concepts", "10-overview.md.report.html"),
            "<h1>Overview report</h1>");
        File.WriteAllText(Path.Combine(docsDir, "01-concepts", "10-overview.md.meta.json"),
            """
            {
              "$schema": "https://agent-taskboard.local/schemas/wiki-document-companion.schema.json",
              "schemaVersion": "wiki-document-companion/v1",
              "title": "Concept overview metadata",
              "source": {
                "path": "docs/01-concepts/10-overview.md",
                "type": "markdown",
                "fingerprint": {
                  "algorithm": "sha256",
                  "hash": "0000000000000000000000000000000000000000000000000000000000000000",
                  "sizeBytes": 1,
                  "lineCount": 1,
                  "capturedAt": "2026-06-12T08:30:00Z"
                }
              },
              "report": {
                "path": "docs/01-concepts/10-overview.md.report.html",
                "generatedAt": "2026-06-12T08:30:00Z",
                "generator": "scripts/wiki/generate-companion-metadata.mjs",
                "template": "wiki-document-companion-report/v1"
              },
              "classification": {
                "owner": "architecture",
                "documentMode": "documentation",
                "temporalState": "present",
                "implementationState": "implemented"
              },
              "review": {
                "date": "2026-06-12",
                "method": "unit test",
                "model": "codex",
                "sourceFingerprint": {
                  "algorithm": "sha256",
                  "hash": "0000000000000000000000000000000000000000000000000000000000000000",
                  "sizeBytes": 1,
                  "lineCount": 1,
                  "capturedAt": "2026-06-12T08:30:00Z"
                },
                "sourceChangedSinceReview": false
              },
              "drift": { "grade": "B", "hasDrift": true, "score": 0.24, "summary": "Light sample drift." },
              "axes": {
                "architectureAlignment": "high",
                "implementationAlignment": "medium",
                "freshness": "medium",
                "operatorUsefulness": "high"
              },
              "duplicates": { "suspected": false, "groupSize": 1, "similarTo": [] },
              "findings": [
                { "id": "drift-summary", "severity": "warn", "axis": "drift", "summary": "Light sample drift." }
              ],
              "nextAction": "Refresh if source changes."
            }
            """);

        var docs = BuildDocsService(("Tree", projectRoot));
        var tree = docs.GetWikiTree("Tree");

        Assert.NotNull(tree);
        Assert.True(tree!.Exists);
        // Folders sort before the loose README file.
        Assert.Equal(2, tree.Root.Count);
        Assert.Equal("folder", tree.Root[0].Type);
        Assert.Equal("concepts", tree.Root[0].Title); // NN- prefix stripped
        Assert.Equal("01-concepts", tree.Root[0].Name);
        Assert.Equal("README.md", tree.Root[1].Name);

        var folder = tree.Root[0];
        Assert.Equal(3, folder.Children.Count);
        // Markdown, HTML, and JSON surface; md '10-overview' sorts by its numeric prefix.
        var types = folder.Children.Select(c => c.Type).ToHashSet();
        Assert.Contains("md", types);
        Assert.Contains("html", types);
        Assert.Contains("json", types);
        Assert.Equal("01-concepts/page.html", folder.Children.Single(c => c.Type == "html").RelPath);
        Assert.Equal("Page metadata", folder.Children.Single(c => c.Type == "json").Title);
        var overview = folder.Children.Single(c => c.Name == "10-overview.md");
        Assert.NotNull(overview.Metadata);
        Assert.Equal("implemented", overview.Metadata!.ImplementationState);
        Assert.True(overview.Metadata.HasDrift);
        Assert.Equal("B", overview.Metadata.DriftGrade);
        Assert.Equal("medium", overview.Metadata.Quality);
        Assert.False(overview.Metadata.DuplicateSuspected);
        Assert.Equal("01-concepts/10-overview.md.report.html", overview.Metadata.ReportPath);
        Assert.Equal("01-concepts/10-overview.md.meta.json", overview.Metadata.CompanionPath);
        Assert.True(overview.Metadata.SourceChangedSinceReview);
        Assert.Equal(1, overview.Metadata.FindingsCount);
    }

    [Fact]
    public void GetWikiTree_NoDocsFolder_ReturnsEmptyButValid()
    {
        var projectRoot = Path.Combine(_tempDir, "no-docs-proj");
        Directory.CreateDirectory(projectRoot);
        var docs = BuildDocsService(("NoDocs", projectRoot));

        var tree = docs.GetWikiTree("NoDocs");

        Assert.NotNull(tree);
        Assert.False(tree!.Exists);
        Assert.Empty(tree.Root);
    }

    [Fact]
    public void GetWikiTree_UnknownProject_ReturnsNull()
    {
        var docs = BuildDocsService(("Known", Path.Combine(_tempDir, "known")));
        Assert.Null(docs.GetWikiTree("Nope"));
    }

    // ---- CreateWikiPage / CreateWikiFolder + commit ----

    [Fact]
    public void CreateWikiPage_WritesFile_AndCommitPathsRecordsIt()
    {
        var repoRoot = Path.Combine(_tempDir, "create-repo");
        Directory.CreateDirectory(repoRoot);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");

        var docs = BuildDocsService(("Create", repoRoot));
        var git = BuildGitService(("Create", repoRoot));

        var result = docs.CreateWikiPage("Create", "guide.md", null);
        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(repoRoot, "docs", "guide.md")));

        var commit = git.CommitPaths(repoRoot, "wiki: create docs/guide.md", new[] { "docs/guide.md" });
        Assert.True(commit.Success, commit.Error);

        var history = git.GetFileHistory(repoRoot, "docs/guide.md");
        Assert.Single(history);
        Assert.Equal("wiki: create docs/guide.md", history[0].Subject);
    }

    [Fact]
    public void CreateWikiPage_RejectsExistingFile_AndNonDocExtension()
    {
        var projectRoot = Path.Combine(_tempDir, "reject-proj");
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        File.WriteAllText(Path.Combine(projectRoot, "docs", "exists.md"), "# x\n");
        var docs = BuildDocsService(("Reject", projectRoot));

        Assert.False(docs.CreateWikiPage("Reject", "exists.md", null).Success);
        Assert.False(docs.CreateWikiPage("Reject", "notes.txt", null).Success);
        Assert.True(docs.CreateWikiPage("Reject", "meta.json", null).Success);
    }

    [Fact]
    public void WriteWikiFile_UpdatesExistingDoc_AndReportsNoop()
    {
        var projectRoot = Path.Combine(_tempDir, "write-proj");
        var docPath = Path.Combine(projectRoot, "docs", "guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);
        File.WriteAllText(docPath, "# Guide\n");
        var docs = BuildDocsService(("Write", projectRoot));

        var changed = docs.WriteWikiFile("Write", "guide.md", "# Guide\n\nUpdated.\n");
        Assert.True(changed.Success);
        Assert.True(changed.Changed);
        Assert.Equal("# Guide\n\nUpdated.\n", File.ReadAllText(docPath));

        var unchanged = docs.WriteWikiFile("Write", "guide.md", "# Guide\n\nUpdated.\n");
        Assert.True(unchanged.Success);
        Assert.False(unchanged.Changed);
    }

    [Fact]
    public void CreateWikiFolder_SeedsGitkeep()
    {
        var projectRoot = Path.Combine(_tempDir, "folder-proj");
        Directory.CreateDirectory(projectRoot);
        var docs = BuildDocsService(("Folder", projectRoot));

        var result = docs.CreateWikiFolder("Folder", "concepts");
        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(projectRoot, "docs", "concepts", ".gitkeep")));
    }

    // ---- MoveAndCommit / RemoveAndCommit / GetFileAtCommit (real git) ----

    [Fact]
    public void MoveAndCommit_RenamesTrackedDoc_AndCommits()
    {
        var repoRoot = Path.Combine(_tempDir, "move-repo");
        var docPath = Path.Combine(repoRoot, "docs", "old.md");
        Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        File.WriteAllText(docPath, "# Doc\n");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "seed");

        var git = BuildGitService(("Move", repoRoot));
        var commit = git.MoveAndCommit(repoRoot, "docs/old.md", "docs/new.md", "wiki: move docs/old.md -> docs/new.md");

        Assert.True(commit.Success, commit.Error);
        Assert.False(File.Exists(docPath));
        Assert.True(File.Exists(Path.Combine(repoRoot, "docs", "new.md")));
    }

    [Fact]
    public void RemoveAndCommit_DeletesTrackedDoc_AndCommits()
    {
        var repoRoot = Path.Combine(_tempDir, "remove-repo");
        var docPath = Path.Combine(repoRoot, "docs", "gone.md");
        Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        File.WriteAllText(docPath, "# Doc\n");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "seed");

        var git = BuildGitService(("Remove", repoRoot));
        var commit = git.RemoveAndCommit(repoRoot, "docs/gone.md", "wiki: delete docs/gone.md");

        Assert.True(commit.Success, commit.Error);
        Assert.False(File.Exists(docPath));
    }

    [Fact]
    public void GetFileAtCommit_ReturnsHistoricContent()
    {
        var repoRoot = Path.Combine(_tempDir, "revision-repo");
        var docPath = Path.Combine(repoRoot, "docs", "note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        File.WriteAllText(docPath, "# Note\nfirst");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "create note");

        var git = BuildGitService(("Revision", repoRoot));
        var history = git.GetFileHistory(repoRoot, "docs/note.md");
        Assert.Single(history);

        File.WriteAllText(docPath, "# Note\nsecond");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "update note");

        var old = git.GetFileAtCommit(repoRoot, history[0].Sha, "docs/note.md");
        Assert.NotNull(old);
        Assert.Contains("first", old);
        Assert.DoesNotContain("second", old);
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
