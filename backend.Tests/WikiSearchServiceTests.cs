using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers <see cref="WikiSearchService"/>: BM25 ranking with weighted fields
/// (title above body), umlaut-folded tokenization, HTML-escaped snippets with
/// <c>&lt;em&gt;</c> highlighting, fingerprint-driven index invalidation on a
/// file change, and the fail-open semantic layer (missing CLI degrades to
/// lexical results, a fake CLI feeds expansion terms into the same pass).
/// </summary>
public class WikiSearchServiceTests : IDisposable
{
    private const string ProjectName = "SearchProj";

    private readonly string _tempDir;
    private readonly string _docsDir;

    public WikiSearchServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-search-tests-" + Guid.NewGuid().ToString("N"));
        _docsDir = Path.Combine(_tempDir, "docs");
        Directory.CreateDirectory(_docsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ---- Ranking ----

    [Fact]
    public async Task Search_TitleMatchRanksAboveBodyMatch()
    {
        WritePage("roadmap.md", "# Roadmap Planung\n\nWas als naechstes ansteht.\n");
        WritePage("notes.md", "# Notizen\n\nDie roadmap wird hier nur im Fliesstext erwaehnt.\n");
        WritePage("unrelated.md", "# Anderes\n\nNichts Relevantes.\n");
        var search = BuildSearchService();

        var res = await search.SearchAsync(ProjectName, "roadmap", semantic: false, limit: 20);

        Assert.NotNull(res);
        Assert.Equal("roadmap", res!.Query);
        Assert.False(res.SemanticUsed);
        Assert.Empty(res.ExpandedTerms);
        Assert.Equal(2, res.Results.Count);
        Assert.Equal("roadmap.md", res.Results[0].RelPath); // title hit outranks body hit
        Assert.Equal("notes.md", res.Results[1].RelPath);
        Assert.True(res.Results[0].Score > res.Results[1].Score);
        Assert.Equal("Roadmap Planung", res.Results[0].Title);
        Assert.Equal("md", res.Results[0].Kind);
        Assert.True(res.DurationMs >= 0);

        var limited = await search.SearchAsync(ProjectName, "roadmap", semantic: false, limit: 1);
        Assert.Single(limited!.Results);
    }

    // ---- Umlaut folding ----

    [Fact]
    public async Task Search_FoldsUmlautsBothWays()
    {
        WritePage("haertung.md", "# Härtung\n\nDie Härtung der verteilten Ausführung.\n");
        WritePage("ascii.md", "# Betrieb\n\nDie haertung wird hier ascii geschrieben.\n");
        var search = BuildSearchService();

        var foldedQuery = await search.SearchAsync(ProjectName, "haertung", semantic: false, limit: 20);
        Assert.Equal(2, foldedQuery!.Results.Count);

        var umlautQuery = await search.SearchAsync(ProjectName, "Härtung", semantic: false, limit: 20);
        Assert.Equal(2, umlautQuery!.Results.Count);
        var hit = umlautQuery.Results.Single(r => r.RelPath == "haertung.md");
        Assert.Contains("<em>Härtung</em>", hit.Snippet);
    }

    // ---- Snippets ----

    [Fact]
    public async Task Search_SnippetIsHtmlEscapedWithEmHighlights()
    {
        WritePage("vector.md", "# Seite\n\nEin <script>alert('x')</script> attack vector im Text.\n");
        var search = BuildSearchService();

        var res = await search.SearchAsync(ProjectName, "attack", semantic: false, limit: 20);

        var snippet = Assert.Single(res!.Results).Snippet;
        Assert.Contains("<em>attack</em>", snippet);
        Assert.DoesNotContain("<script>", snippet);
        Assert.Contains("&lt;script&gt;", snippet);
        // Only <em> markup is allowed in the snippet.
        Assert.DoesNotContain("<", snippet.Replace("<em>", "").Replace("</em>", ""));
    }

    [Fact]
    public async Task Search_HtmlPagesAreIndexedTagStripped()
    {
        WritePage("concept.html",
            "<html><head><title>Konzept Suche</title></head><body><h2>Abschnitt</h2><p>Der eindeutige begriffxyz steht im Fliesstext.</p></body></html>");
        var search = BuildSearchService();

        var res = await search.SearchAsync(ProjectName, "begriffxyz", semantic: false, limit: 20);

        var hit = Assert.Single(res!.Results);
        Assert.Equal("concept.html", hit.RelPath);
        Assert.Equal("html", hit.Kind);
        Assert.Equal("Konzept Suche", hit.Title);
        Assert.DoesNotContain("<p>", hit.Snippet);
        Assert.Contains("<em>begriffxyz</em>", hit.Snippet);
    }

    // ---- Index invalidation ----

    [Fact]
    public async Task Search_RebuildsIndexAfterFileChange()
    {
        var page = WritePage("changing.md", "# Seite\n\nalpha inhalt.\n");
        var search = BuildSearchService();

        Assert.Single((await search.SearchAsync(ProjectName, "alpha", semantic: false, limit: 20))!.Results);
        Assert.Empty((await search.SearchAsync(ProjectName, "omega", semantic: false, limit: 20))!.Results);

        // Different content AND different size, so the fingerprint changes even
        // if the filesystem timestamp granularity swallows the mtime bump.
        File.WriteAllText(page, "# Seite\n\nomega inhalt jetzt deutlich anders.\n");

        Assert.Single((await search.SearchAsync(ProjectName, "omega", semantic: false, limit: 20))!.Results);
        Assert.Empty((await search.SearchAsync(ProjectName, "alpha", semantic: false, limit: 20))!.Results);
    }

    // ---- Semantic layer ----

    [Fact]
    public async Task Search_SemanticWithoutCli_FailsOpenToLexicalResults()
    {
        WritePage("deploy.md", "# Deployment\n\nDeployment guide.\n");
        var search = BuildSearchService(); // empty one-shot registry: no CLI at all

        var res = await search.SearchAsync(ProjectName, "deployment", semantic: true, limit: 20);

        Assert.NotNull(res);
        Assert.False(res!.SemanticUsed);
        Assert.Empty(res.ExpandedTerms);
        Assert.Single(res.Results); // lexical hits are still served
    }

    [Fact]
    public async Task Search_SemanticExpansionTermsJoinTheSearchAtHalfWeight()
    {
        WritePage("deploy.md", "# Verteilung\n\nDas deployment auf die Server.\n");
        WritePage("other.md", "# Anderes\n\nOhne Treffer.\n");
        var search = BuildSearchService(new FakeOneShot("""{"terms":["deployment","rollout"]}"""));

        var res = await search.SearchAsync(ProjectName, "ausrollen", semantic: true, limit: 20);

        Assert.NotNull(res);
        Assert.True(res!.SemanticUsed);
        Assert.Contains("deployment", res.ExpandedTerms);
        var hit = Assert.Single(res.Results);
        Assert.Equal("deploy.md", hit.RelPath);
        Assert.Contains("<em>deployment</em>", hit.Snippet);
    }

    [Fact]
    public async Task Search_SemanticWithFailingCli_FailsOpen()
    {
        WritePage("deploy.md", "# Deployment\n\nDeployment guide.\n");
        var search = BuildSearchService(new FakeOneShot(stdout: "boom", ok: false));

        var res = await search.SearchAsync(ProjectName, "deployment", semantic: true, limit: 20);

        Assert.False(res!.SemanticUsed);
        Assert.Single(res.Results);
    }

    [Fact]
    public void ParseExpansionTerms_ParsesShapeAndRejectsGarbage()
    {
        Assert.Equal(new[] { "a1", "b2" }, WikiSearchService.ParseExpansionTerms("""noise {"terms":["a1"," b2 "]} tail""")!.ToArray());
        Assert.Null(WikiSearchService.ParseExpansionTerms("no json at all"));
        Assert.Null(WikiSearchService.ParseExpansionTerms("""{"other":true}"""));
        Assert.Equal(WikiSearchService.MaxExpansionTerms,
            WikiSearchService.ParseExpansionTerms(
                """{"terms":["t1","t2","t3","t4","t5","t6","t7","t8","t9","t10"]}""")!.Count);
    }

    [Fact]
    public async Task Search_UnknownProject_ReturnsNull()
    {
        var search = BuildSearchService();
        Assert.Null(await search.SearchAsync("NopeProject", "x", semantic: false, limit: 20));
    }

    // ---- helpers ----

    private sealed class FakeOneShot : ICliOneShot
    {
        private readonly string _stdout;
        private readonly bool _ok;

        public FakeOneShot(string stdout, bool ok = true)
        {
            _stdout = stdout;
            _ok = ok;
        }

        public string CliType => "claude";

        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            return Task.FromResult(new CliOneShotResult(
                Ok: _ok,
                ExitCode: _ok ? 0 : 1,
                Stdout: _stdout,
                Stderr: "",
                Duration: TimeSpan.Zero,
                ParsedText: _stdout,
                Usage: null,
                RichUsage: null,
                Latency: new AgentMessageLatency(RequestedAt: now, CompletedAt: now, TotalMs: 0),
                Error: _ok ? null : "fake failure"));
        }
    }

    private string WritePage(string relPath, string content)
    {
        var full = Path.Combine(_docsDir, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private WikiSearchService BuildSearchService(params ICliOneShot[] oneShots)
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new WikiSearchService(
            scanner,
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new CliOneShotRegistry(oneShots),
            config,
            new AgentStudio.Prompts.RuntimePromptService(config, NullLogger<AgentStudio.Prompts.RuntimePromptService>.Instance),
            NullLogger<WikiSearchService>.Instance);
    }

    private IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:RootPath"] = _tempDir,
            ["WatchPaths:0:Path"] = Path.Combine(_tempDir, ".orchestrator", "jobs"),
        })
        .Build();
}
