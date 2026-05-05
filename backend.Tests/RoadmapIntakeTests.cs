using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the splitter / confirm contract for <see cref="RoadmapIntakeService"/>.
/// The Haiku call is stubbed so the suite never bills tokens; the parser
/// and the confirm path are exercised against a real on-disk watch path
/// so the job folders, <c>job.json</c>, and <c>prompt.md</c> writes are
/// covered end-to-end.
/// </summary>
public class RoadmapIntakeTests : IDisposable
{
    private readonly string _watchPath;

    public RoadmapIntakeTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "agent-taskboard-intake-" + Guid.NewGuid().ToString("N"));
        foreach (var state in JobStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ParseSplitterJson_EmptyCandidates_ReturnsEmptyList()
    {
        var resp = RoadmapIntakeService.ParseSplitterJson("""{"candidates": [], "notes": ""}""");
        Assert.Empty(resp.Candidates);
        Assert.Equal("", resp.Notes);
    }

    [Fact]
    public void ParseSplitterJson_StripsCodeFence()
    {
        var raw = "```json\n{\"candidates\": [], \"notes\": \"x\"}\n```";
        var resp = RoadmapIntakeService.ParseSplitterJson(raw);
        Assert.Empty(resp.Candidates);
        Assert.Equal("x", resp.Notes);
    }

    [Fact]
    public void ParseSplitterJson_HydratesAllFields()
    {
        var json = """
        {
          "candidates": [
            {
              "title": "Add chat intake",
              "promptBody": "Body here",
              "kind": "feature",
              "suggestedOrder": 20,
              "suggestedCliType": "claude",
              "rationale": "User asked"
            }
          ],
          "notes": "merged two related items"
        }
        """;
        var resp = RoadmapIntakeService.ParseSplitterJson(json);
        var c = Assert.Single(resp.Candidates);
        Assert.Equal("Add chat intake", c.Title);
        Assert.Equal("Body here", c.PromptBody);
        Assert.Equal("feature", c.Kind);
        Assert.Equal(20, c.SuggestedOrder);
        Assert.Equal("claude", c.SuggestedCliType);
        Assert.Equal("User asked", c.Rationale);
        Assert.Equal("merged two related items", resp.Notes);
    }

    [Fact]
    public void ParseSplitterJson_InvalidJson_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => RoadmapIntakeService.ParseSplitterJson("not json"));
    }

    [Fact]
    public async Task SplitAsync_EmptyText_ReturnsEmptyCandidatesWithoutCallingSplitter()
    {
        var svc = BuildService("""{"candidates": [{"title":"x","promptBody":"y","kind":"feature","suggestedOrder":10,"suggestedCliType":"claude","rationale":"z"}], "notes":""}""");
        var resp = await svc.SplitAsync("   ");
        Assert.Empty(resp.Candidates);
        Assert.Equal(0, svc.InvocationCount);
    }

    [Fact]
    public async Task SplitAsync_SingleTask_PassesThroughCandidate()
    {
        var svc = BuildService("""
        {"candidates":[{"title":"Add intake endpoint","promptBody":"Implement POST /api/roadmap/intake","kind":"feature","suggestedOrder":10,"suggestedCliType":"claude","rationale":"single ask"}],"notes":""}
        """);
        var resp = await svc.SplitAsync("Please add a /api/roadmap/intake endpoint.");
        var c = Assert.Single(resp.Candidates);
        Assert.Equal("Add intake endpoint", c.Title);
        Assert.Equal("feature", c.Kind);
    }

    [Fact]
    public async Task SplitAsync_MultipleMixed_KeepsBothBugAndFeature()
    {
        var svc = BuildService("""
        {"candidates":[
          {"title":"Fix login race","promptBody":"Race in token refresh","kind":"bug","suggestedOrder":10,"suggestedCliType":"claude","rationale":"observed regression"},
          {"title":"Add roadmap intake","promptBody":"Two-step splitter UI","kind":"feature","suggestedOrder":20,"suggestedCliType":"claude","rationale":"new ask"}
        ],"notes":""}
        """);
        var resp = await svc.SplitAsync("Login is flaky AND I want the roadmap thing.");
        Assert.Equal(2, resp.Candidates.Count);
        Assert.Equal("bug", resp.Candidates[0].Kind);
        Assert.Equal("feature", resp.Candidates[1].Kind);
    }

    [Fact]
    public async Task SplitAsync_OversizedInput_StillRunsSplitterOnTruncatedBuffer()
    {
        var svc = BuildService("""{"candidates":[{"title":"Compressed","promptBody":"truncated dump processed","kind":"feature","suggestedOrder":10,"suggestedCliType":"claude","rationale":"oversized"}],"notes":"input was very large"}""");
        var huge = new string('x', 80_000);
        var resp = await svc.SplitAsync(huge);
        Assert.Single(resp.Candidates);
        Assert.NotNull(svc.LastPrompt);
        // The service must bound the embedded input - the rendered prompt
        // should not contain the full 80k payload, otherwise we re-introduce
        // the Windows CreateProcess command-line limit even though we feed
        // the prompt via stdin. The prompt template plus 40k cap puts the
        // total comfortably under 50k.
        Assert.True(svc.LastPrompt!.Length < 50_000,
            $"rendered prompt was {svc.LastPrompt.Length} chars, expected truncation");
        Assert.Contains("input truncated", svc.LastPrompt);
    }

    [Fact]
    public void Confirm_WritesOneJobFolderPerCandidate_AllInPreparation()
    {
        var svc = BuildService("");
        var resp = svc.Confirm(new RoadmapIntakeConfirmRequest
        {
            WatchPath = _watchPath,
            Candidates = new()
            {
                new RoadmapIntakeCandidate
                {
                    Title = "First task",
                    PromptBody = "Body of first",
                    Kind = "feature",
                    SuggestedOrder = 10,
                    SuggestedCliType = "claude",
                    Rationale = "split rationale"
                },
                new RoadmapIntakeCandidate
                {
                    Title = "Second task",
                    PromptBody = "Body of second",
                    Kind = "bug",
                    SuggestedOrder = 20,
                    SuggestedCliType = "codex",
                    Rationale = ""
                }
            }
        });

        Assert.Equal(2, resp.Created.Count);
        Assert.Empty(resp.Skipped);
        var prep = Path.Combine(_watchPath, JobStates.Preparation);
        Assert.True(Directory.Exists(prep));
        var jobs = Directory.GetDirectories(prep);
        Assert.Equal(2, jobs.Length);

        var ready = Path.Combine(_watchPath, JobStates.Ready);
        Assert.Empty(Directory.GetDirectories(ready)); // never auto-queues
    }

    [Fact]
    public void Confirm_SkipsCandidatesWithEmptyTitle()
    {
        var svc = BuildService("");
        var resp = svc.Confirm(new RoadmapIntakeConfirmRequest
        {
            WatchPath = _watchPath,
            Candidates = new()
            {
                new RoadmapIntakeCandidate { Title = "Real one", PromptBody = "x", SuggestedCliType = "claude" },
                new RoadmapIntakeCandidate { Title = "", PromptBody = "orphan body" }
            }
        });
        Assert.Single(resp.Created);
        Assert.Single(resp.Skipped);
    }

    [Fact]
    public void Confirm_PromptBody_IncludesBodyAndRationaleFooter()
    {
        var svc = BuildService("");
        var resp = svc.Confirm(new RoadmapIntakeConfirmRequest
        {
            WatchPath = _watchPath,
            Candidates = new()
            {
                new RoadmapIntakeCandidate
                {
                    Title = "Feature X",
                    PromptBody = "Body of X",
                    Kind = "feature",
                    Rationale = "user asked",
                    SuggestedCliType = "claude"
                }
            }
        });

        var jobId = resp.Created[0].JobId;
        var promptPath = Path.Combine(_watchPath, JobStates.Preparation, jobId, "prompt.md");
        Assert.True(File.Exists(promptPath));
        var body = File.ReadAllText(promptPath);
        Assert.Contains("Body of X", body);
        Assert.Contains("user asked", body);
        Assert.Contains("feature", body);
    }

    private FakeIntakeService BuildService(string fakeSplitterResponse)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath,
                ["PromptTemplates:RuntimePath"] = FindPromptRoot()
            })
            .Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config, prompts);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var mutations = new JobMutationService(scanner, NullLogger<JobMutationService>.Instance);
        return new FakeIntakeService(
            NullLogger<RoadmapIntakeService>.Instance,
            config, prompts, mutations, fakeSplitterResponse);
    }

    private static string FindPromptRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "prompts", "runtime");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate prompts/runtime from test base directory.");
    }

    /// <summary>
    /// Test seam: replaces the Haiku subprocess with a deterministic raw
    /// response so the suite never bills tokens. Records the rendered
    /// prompt so size-related assertions can read what would have been
    /// piped to stdin.
    /// </summary>
    private sealed class FakeIntakeService : RoadmapIntakeService
    {
        private readonly string _response;
        public string? LastPrompt { get; private set; }
        public int InvocationCount { get; private set; }

        public FakeIntakeService(
            ILogger<RoadmapIntakeService> logger,
            IConfiguration config,
            RuntimePromptService prompts,
            JobMutationService mutations,
            string response)
            : base(logger, config, prompts, mutations)
        {
            _response = response;
        }

        protected override Task<(bool Ok, string? Raw, string? Error)> InvokeSplitterAsync(
            string prompt, CancellationToken ct)
        {
            LastPrompt = prompt;
            InvocationCount++;
            return Task.FromResult((true, (string?)_response, (string?)null));
        }
    }
}
