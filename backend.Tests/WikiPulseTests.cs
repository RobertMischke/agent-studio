using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the wiki Pulse landing composition (PULSE-1): the deterministic
/// task-key + drift-band pure helpers, and the end-to-end
/// <see cref="ProjectDocsService.GetWikiPulse"/> over a real temp git repo -
/// change-feed top-folder + task-key enrichment, inbox detection of loose /
/// unfiled pages, the per-top-folder drift grade bar, the folder-independent
/// human-action frontmatter convention, and the graceful empty states.
/// </summary>
public class WikiPulseTests : IDisposable
{
    private readonly string _tempDir;

    public WikiPulseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-pulse-tests-" + Guid.NewGuid().ToString("N"));
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

    // ---- Pure helpers ----

    [Theory]
    [InlineData("AGT-2014 land the pulse view", "AGT-2014")]
    [InlineData("wiki: update docs/guide.md", null)]
    [InlineData("Fix flaky ASS-1709 spec", "ASS-1709")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractTaskKeyFromSubject_ParsesFirstKeyOrNull(string? subject, string? expected)
    {
        Assert.Equal(expected, ProjectDocsService.ExtractTaskKeyFromSubject(subject));
    }

    [Theory]
    [InlineData(0, "Fresh")]
    [InlineData(9, "Fresh")]
    [InlineData(10, "Aging")]
    [InlineData(49, "Aging")]
    [InlineData(50, "Stale")]
    [InlineData(500, "Stale")]
    public void DriftBand_UsesFreshAgingStaleThresholds(int commits, string expected)
    {
        Assert.Equal(expected, ProjectDocsService.DriftBand(commits));
    }

    // ---- GetWikiPulse (real git) ----

    [Fact]
    public void GetWikiPulse_ComposesFeedEnrichmentInboxAndTopFolderDriftGrades()
    {
        var repoRoot = Path.Combine(_tempDir, "pulse-repo");
        var docsDir = Path.Combine(repoRoot, "docs");
        var conceptsDir = Path.Combine(docsDir, "20-concepts");
        var opsDir = Path.Combine(docsDir, "operations");
        Directory.CreateDirectory(conceptsDir);
        Directory.CreateDirectory(opsDir);
        Directory.CreateDirectory(Path.Combine(docsDir, "empty-folder")); // no pages -> no drift group
        Directory.CreateDirectory(Path.Combine(repoRoot, "backend"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "frontend"));

        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");

        // --- Seed commit (2026-01-01): two real top-level docs folders + a loose
        // root page + two code roots. active-stream.md carries a frontmatter task key.
        File.WriteAllText(Path.Combine(docsDir, "README.md"), "# Readme\n");
        File.WriteAllText(Path.Combine(docsDir, "loose-note.md"), "# Loose note\n");
        File.WriteAllText(Path.Combine(conceptsDir, "active-stream.md"),
            "---\ntask-key: AGT-2014\n---\n# Active stream\n");
        File.WriteAllText(Path.Combine(repoRoot, "backend", "svc.cs"), "// v0\n");
        File.WriteAllText(Path.Combine(repoRoot, "frontend", "app.ts"), "// v0\n");
        RunGit(repoRoot, "add -A");
        Commit(repoRoot, "2026-01-01T00:00:00", "seed docs and code");

        // --- 10 code commits after active-stream's last update -> Aging (>=10).
        for (var i = 1; i <= 10; i++)
        {
            File.AppendAllText(Path.Combine(repoRoot, "backend", "svc.cs"), $"// change {i}\n");
            RunGit(repoRoot, "add -A");
            Commit(repoRoot, $"2026-02-{i:00}T00:00:00", $"backend change {i}");
        }

        // --- An operations page added AFTER all code churn -> Fresh (0 commits
        // since). Its human-action frontmatter raises the folder-independent
        // Pulse warning; its task key comes from the commit subject.
        File.WriteAllText(Path.Combine(opsDir, "signal.md"),
            "---\nstatus: active\nhuman-action: Investigate the latency regression.\n---\n# Latency signal\n\n[Missing runbook](missing.md)\n");
        RunGit(repoRoot, "add -A");
        Commit(repoRoot, "2026-06-01T00:00:00", "AGT-2020 add latency signal");

        var docs = BuildDocsService(("Pulse", repoRoot));
        var git = BuildGitService(("Pulse", repoRoot));

        var pulse = docs.GetWikiPulse("Pulse", git);

        Assert.NotNull(pulse);
        Assert.True(pulse!.Exists);

        // ----- Change feed: enriched with the top-folder badge + task key -----
        Assert.True(pulse.Feed.Available);
        var active = pulse.Feed.Items.Single(i => i.RelPath == "20-concepts/active-stream.md");
        Assert.Equal("20-concepts", active.AreaSlug);
        Assert.Equal("concepts", active.AreaTitle); // order prefix stripped
        Assert.Equal("AGT-2014", active.TaskKey);   // from frontmatter

        var signal = pulse.Feed.Items.Single(i => i.RelPath == "operations/signal.md");
        Assert.Equal("operations", signal.AreaSlug);
        Assert.Equal("AGT-2020", signal.TaskKey); // from commit subject

        // A loose root page carries no area badge.
        var loose = pulse.Feed.Items.Single(i => i.RelPath == "loose-note.md");
        Assert.Null(loose.AreaSlug);

        // ----- Inbox: only the loose root page, README excluded -----
        Assert.True(pulse.Inbox.Available);
        var inboxPaths = pulse.Inbox.Items.Select(i => i.RelPath).ToHashSet();
        Assert.Contains("loose-note.md", inboxPaths);
        Assert.DoesNotContain("README.md", inboxPaths);
        Assert.DoesNotContain("20-concepts/active-stream.md", inboxPaths);
        Assert.Equal(1, pulse.Inbox.Count);

        // ----- Drift grade bar: real top folders with pages, alphabetical -----
        Assert.True(pulse.Drift.Available);
        Assert.Equal(["20-concepts", "operations"], pulse.Drift.Areas.Select(a => a.Slug).ToArray());

        var concepts = pulse.Drift.Areas.Single(a => a.Slug == "20-concepts");
        Assert.Equal("concepts", concepts.Title);
        Assert.Equal("Aging", concepts.Grade);
        Assert.Equal(1, concepts.PageCount);
        Assert.Equal(1, concepts.GradedPageCount);
        Assert.Equal(10, concepts.WorstCommitCount);
        Assert.Equal(1, concepts.AgingCount);

        var operations = pulse.Drift.Areas.Single(a => a.Slug == "operations");
        Assert.Equal("Fresh", operations.Grade);
        Assert.Equal(0, operations.WorstCommitCount);
        Assert.Equal(1, operations.FreshCount);

        // A folder without pages never appears as a drift group.
        Assert.DoesNotContain(pulse.Drift.Areas, a => a.Slug == "empty-folder");

        Assert.Equal("Aging", pulse.Drift.OverallGrade); // worst folder
        Assert.Equal(1, pulse.Drift.Counts.Fresh);
        Assert.Equal(1, pulse.Drift.Counts.Aging);
        Assert.Equal(0, pulse.Drift.Counts.Stale);
        Assert.Equal(2, pulse.Drift.Counts.Graded);

        // human-action is a frontmatter convention, independent of the folder.
        Assert.Contains(pulse.Warnings.Items, w => w.Kind == "human-action"
            && w.Status == "active" && w.HumanAction.Contains("Investigate"));
        Assert.Contains(pulse.Warnings.Items, w => w.Kind == "dead-link" && w.Detail == "missing.md");
    }

    [Fact]
    public void GetWikiPulse_DriftFollowsSavedRootFolderOrder_UnlistedBehind()
    {
        var repoRoot = Path.Combine(_tempDir, "pulse-order");
        var docsDir = Path.Combine(repoRoot, "docs");
        foreach (var folder in new[] { "alpha", "bravo", "zulu", "10-later", "2-early" })
        {
            Directory.CreateDirectory(Path.Combine(docsDir, folder));
            File.WriteAllText(Path.Combine(docsDir, folder, "page.md"), $"# {folder}\n");
        }
        // Saved category drag-order for the docs root: zulu first, then bravo;
        // the unlisted rest sorts behind in the tree's default order (numeric
        // NN- prefix, then name), so 2-early precedes 10-later precedes alpha.
        File.WriteAllText(Path.Combine(docsDir, ".wiki-order.json"),
            """{ "schemaVersion": "wiki-folder-order/v1", "folderOrder": { "": ["zulu", "bravo"] } }""");
        // Hidden entries (dot-prefixed folders/files) are config sidecars the
        // tree never shows: neither a drift group nor a page count may include
        // them.
        Directory.CreateDirectory(Path.Combine(docsDir, ".curator"));
        File.WriteAllText(Path.Combine(docsDir, ".curator", "context.json"), "{ \"title\": \"curator\" }\n");
        File.WriteAllText(Path.Combine(docsDir, "zulu", ".retro-pilot.json"), "{ \"title\": \"retro\" }\n");
        Directory.CreateDirectory(Path.Combine(repoRoot, "backend"));
        File.WriteAllText(Path.Combine(repoRoot, "backend", "svc.cs"), "// v0\n");

        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        RunGit(repoRoot, "add -A");
        Commit(repoRoot, "2026-01-01T00:00:00", "seed");

        var docs = BuildDocsService(("Order", repoRoot));
        var git = BuildGitService(("Order", repoRoot));

        var pulse = docs.GetWikiPulse("Order", git);

        Assert.NotNull(pulse);
        Assert.True(pulse!.Drift.Available);
        Assert.Equal(
            ["zulu", "bravo", "2-early", "10-later", "alpha"],
            pulse.Drift.Areas.Select(a => a.Slug).ToArray());
        // The hidden sidecars are invisible everywhere: no .curator drift group,
        // no extra zulu page, no feed row.
        Assert.Equal(1, pulse.Drift.Areas.Single(a => a.Slug == "zulu").PageCount);
        Assert.DoesNotContain(pulse.Feed.Items, i => i.RelPath.Contains(".curator") || i.RelPath.Contains(".retro-pilot"));
    }

    [Fact]
    public void GetWikiPulse_HumanActionConvention_FiresAnywhere_AndRequiresLiveStatus()
    {
        var repoRoot = Path.Combine(_tempDir, "pulse-human-action");
        var docsDir = Path.Combine(repoRoot, "docs");
        var deepDir = Path.Combine(docsDir, "operations", "runbooks");
        Directory.CreateDirectory(deepDir);
        // Live signal deep in an arbitrary folder -> warning.
        File.WriteAllText(Path.Combine(deepDir, "observed.md"),
            "---\nstatus: observed\nhuman-action: Rotate the token.\n---\n# Observed\n");
        // Resolved signal -> no warning despite the human-action field.
        File.WriteAllText(Path.Combine(deepDir, "resolved.md"),
            "---\nstatus: resolved\nhuman-action: Already handled.\n---\n# Resolved\n");
        // Live status without a human-action field -> no warning.
        File.WriteAllText(Path.Combine(docsDir, "operations", "no-action.md"),
            "---\nstatus: active\n---\n# No action\n");

        var docs = BuildDocsService(("HumanAction", repoRoot));
        var git = BuildGitService(("HumanAction", repoRoot));

        var pulse = docs.GetWikiPulse("HumanAction", git);

        Assert.NotNull(pulse);
        Assert.True(pulse!.Warnings.Available);
        var warning = Assert.Single(pulse.Warnings.Items, w => w.Kind == "human-action");
        Assert.Equal("operations/runbooks/observed.md", warning.RelPath);
        Assert.Equal("observed", warning.Status);
        Assert.Equal("Rotate the token.", warning.HumanAction);
    }

    [Fact]
    public void GetWikiTree_HasNoPinnedFolder_SiblingsSortByPrefixAndName()
    {
        var projectRoot = Path.Combine(_tempDir, "tree-no-pin");
        var docsDir = Path.Combine(projectRoot, "docs");
        foreach (var folder in new[] { "concepts", "architecture", "engineering-workstream" })
        {
            Directory.CreateDirectory(Path.Combine(docsDir, folder));
            File.WriteAllText(Path.Combine(docsDir, folder, "page.md"), $"# {folder}\n");
        }

        var docs = BuildDocsService(("Tree", projectRoot));
        var tree = docs.GetWikiTree("Tree");

        Assert.NotNull(tree);
        // No frame pin: plain alphabetical folder order, and no relabelling of
        // the historical engineering-workstream folder name.
        Assert.Equal(
            ["architecture", "concepts", "engineering-workstream"],
            tree!.Root.Where(n => n.Type == "folder").Select(n => n.Name).ToArray());
        Assert.Equal("engineering-workstream",
            tree.Root.Single(n => n.Name == "engineering-workstream").Title);
    }

    [Fact]
    public void GetWikiPulse_NoDocsFolder_DegradesToEmptyStatesWithReasons()
    {
        var projectRoot = Path.Combine(_tempDir, "no-docs");
        Directory.CreateDirectory(projectRoot);
        var docs = BuildDocsService(("NoDocs", projectRoot));
        var git = BuildGitService(("NoDocs", projectRoot));

        var pulse = docs.GetWikiPulse("NoDocs", git);

        Assert.NotNull(pulse);
        Assert.False(pulse!.Exists);
        Assert.False(pulse.Feed.Available);
        Assert.False(pulse.Inbox.Available);
        Assert.False(pulse.Drift.Available);
        Assert.NotNull(pulse.Feed.Reason);
        Assert.Empty(pulse.Feed.Items);
    }

    [Fact]
    public void GetWikiPulse_EmptyInbox_IsHealthyAvailableState()
    {
        var projectRoot = Path.Combine(_tempDir, "clean-inbox");
        var conceptsDir = Path.Combine(projectRoot, "docs", "concepts");
        Directory.CreateDirectory(conceptsDir);
        // Only a conventional root index + a filed subpage: nothing to sort.
        File.WriteAllText(Path.Combine(projectRoot, "docs", "README.md"), "# Home\n");
        File.WriteAllText(Path.Combine(conceptsDir, "overview.md"), "# Overview\n");
        var docs = BuildDocsService(("Clean", projectRoot));
        var git = BuildGitService(("Clean", projectRoot));

        var pulse = docs.GetWikiPulse("Clean", git);

        Assert.NotNull(pulse);
        Assert.True(pulse!.Inbox.Available);
        Assert.Equal(0, pulse.Inbox.Count);
        Assert.Empty(pulse.Inbox.Items);
    }

    [Fact]
    public void GetWikiPulse_UnknownProject_ReturnsNull()
    {
        var docs = BuildDocsService(("Known", Path.Combine(_tempDir, "known")));
        var git = BuildGitService(("Known", Path.Combine(_tempDir, "known")));
        Assert.Null(docs.GetWikiPulse("Nope", git));
    }

    // ---- helpers ----

    private ProjectDocsService BuildDocsService(params (string Name, string RootPath)[] entries)
        => new(BuildScanner(entries),
               new ProjectRegistry(BuildConfig(entries), NullLogger<ProjectRegistry>.Instance),
               NullLogger<ProjectDocsService>.Instance);

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
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }

    /// <summary>Commit staged changes with a fixed author date so the drift
    /// heuristic's "commits since page update" counting is deterministic.</summary>
    private static void Commit(string cwd, string isoDate, string message)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_COMMITTER_DATE"] = isoDate;
        foreach (var a in new[] { "commit", "-q", $"--date={isoDate}", "-m", message })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }
}
