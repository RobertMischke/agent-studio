using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the wiki Pulse landing composition (PULSE-1): the deterministic
/// task-key + drift-band pure helpers, and the end-to-end
/// <see cref="ProjectDocsService.GetWikiPulse"/> over a real temp git repo -
/// change-feed frame-area + task-key enrichment, inbox detection of loose /
/// unfiled pages, the per-area drift grade bar, and the graceful empty states.
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
    public void GetWikiPulse_ComposesFeedEnrichmentInboxAndDriftGrades()
    {
        var repoRoot = Path.Combine(_tempDir, "pulse-repo");
        var docsDir = Path.Combine(repoRoot, "docs");
        var frameDir = Path.Combine(docsDir, "engineering-workstream");
        var area10 = Path.Combine(frameDir, "10-current-development-state");
        var area20 = Path.Combine(frameDir, "20-development-signals");
        Directory.CreateDirectory(area10);
        Directory.CreateDirectory(area20);
        Directory.CreateDirectory(Path.Combine(repoRoot, "backend"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "frontend"));

        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");

        // --- Seed commit (2026-01-01): docs frame + a loose root page + a frame
        // stray + two code roots. active-stream.md carries a frontmatter task key.
        File.WriteAllText(Path.Combine(docsDir, "README.md"), "# Readme\n");
        File.WriteAllText(Path.Combine(docsDir, "loose-note.md"), "# Loose note\n");
        File.WriteAllText(Path.Combine(frameDir, "00-overview.html"), "<h1>Overview</h1>");
        File.WriteAllText(Path.Combine(frameDir, "stray.md"), "# Stray fragment\n");
        File.WriteAllText(Path.Combine(area10, "index.html"), "<h1>Current Development State</h1>");
        File.WriteAllText(Path.Combine(area10, "active-stream.md"),
            "---\ntask-key: AGT-2014\n---\n# Active stream\n");
        File.WriteAllText(Path.Combine(area20, "index.html"), "<h1>Development Signals</h1>");
        File.WriteAllText(Path.Combine(repoRoot, "backend", "svc.cs"), "// v0\n");
        File.WriteAllText(Path.Combine(repoRoot, "frontend", "app.ts"), "// v0\n");
        RunGit(repoRoot, "add -A");
        Commit(repoRoot, "2026-01-01T00:00:00", "seed frame and code");

        // --- 10 code commits after active-stream's last update -> Aging (>=10).
        for (var i = 1; i <= 10; i++)
        {
            File.AppendAllText(Path.Combine(repoRoot, "backend", "svc.cs"), $"// change {i}\n");
            RunGit(repoRoot, "add -A");
            Commit(repoRoot, $"2026-02-{i:00}T00:00:00", $"backend change {i}");
        }

        // --- A signals page added AFTER all code churn -> Fresh (0 commits since).
        // Its task key comes from the commit subject, not frontmatter.
        File.WriteAllText(Path.Combine(area20, "signal.md"), "# Latency signal\n");
        RunGit(repoRoot, "add -A");
        Commit(repoRoot, "2026-06-01T00:00:00", "AGT-2020 add latency signal");

        var docs = BuildDocsService(("Pulse", repoRoot));
        var git = BuildGitService(("Pulse", repoRoot));

        var pulse = docs.GetWikiPulse("Pulse", git);

        Assert.NotNull(pulse);
        Assert.True(pulse!.Exists);

        // ----- Change feed: enriched with frame-area badge + task key -----
        Assert.True(pulse.Feed.Available);
        var active = pulse.Feed.Items.Single(i => i.RelPath == "engineering-workstream/10-current-development-state/active-stream.md");
        Assert.Equal("10-current-development-state", active.FrameAreaSlug);
        Assert.Equal("Current Development State", active.FrameAreaTitle);
        Assert.Equal("AGT-2014", active.TaskKey); // from frontmatter

        var signal = pulse.Feed.Items.Single(i => i.RelPath == "engineering-workstream/20-development-signals/signal.md");
        Assert.Equal("20-development-signals", signal.FrameAreaSlug);
        Assert.Equal("AGT-2020", signal.TaskKey); // from commit subject

        // A loose root page carries no area badge.
        var loose = pulse.Feed.Items.Single(i => i.RelPath == "loose-note.md");
        Assert.Null(loose.FrameAreaSlug);

        // ----- Inbox: loose root page + frame stray, README excluded -----
        Assert.True(pulse.Inbox.Available);
        var inboxPaths = pulse.Inbox.Items.Select(i => i.RelPath).ToHashSet();
        Assert.Contains("loose-note.md", inboxPaths);
        Assert.Contains("engineering-workstream/stray.md", inboxPaths);
        Assert.DoesNotContain("README.md", inboxPaths);
        Assert.DoesNotContain("engineering-workstream/10-current-development-state/active-stream.md", inboxPaths);
        Assert.DoesNotContain("engineering-workstream/00-overview.html", inboxPaths); // frame shell
        Assert.Equal(2, pulse.Inbox.Count);

        // ----- Drift grade bar: area 10 Aging, area 20 Fresh, rest Empty -----
        Assert.True(pulse.Drift.Available);
        Assert.Equal(5, pulse.Drift.Areas.Count);

        var d10 = pulse.Drift.Areas.Single(a => a.Slug == "10-current-development-state");
        Assert.Equal("Aging", d10.Grade);
        Assert.Equal(1, d10.PageCount);       // active-stream.md; index.html shell excluded
        Assert.Equal(1, d10.GradedPageCount);
        Assert.Equal(10, d10.WorstCommitCount);
        Assert.Equal(1, d10.AgingCount);

        var d20 = pulse.Drift.Areas.Single(a => a.Slug == "20-development-signals");
        Assert.Equal("Fresh", d20.Grade);
        Assert.Equal(0, d20.WorstCommitCount);
        Assert.Equal(1, d20.FreshCount);

        var d30 = pulse.Drift.Areas.Single(a => a.Slug == "30-system-knowledge");
        Assert.Equal("Empty", d30.Grade);
        Assert.Equal(0, d30.PageCount);

        Assert.Equal("Aging", pulse.Drift.OverallGrade); // worst area
        Assert.Equal(1, pulse.Drift.Counts.Fresh);
        Assert.Equal(1, pulse.Drift.Counts.Aging);
        Assert.Equal(0, pulse.Drift.Counts.Stale);
        Assert.Equal(2, pulse.Drift.Counts.Graded);
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
