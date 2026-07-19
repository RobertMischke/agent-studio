using System.Diagnostics;
using AgentStudio.Proposals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Guards the proposals image path against the former N x N catalogue scan:
/// every evidence request used to re-read every proposal markdown file.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class ProjectProposalEvidencePerfTests : IDisposable
{
    private const string ProjectName = "proposal-perf";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "proposal-perf-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WarmCatalogue_ResolvesEveryEvidenceRequestWithoutRescanningMarkdown()
    {
        var proposalsDir = Path.Combine(_root, "docs", "concepts", "proposals", "2026-07-11");
        var assetsDir = Path.Combine(proposalsDir, "assets");
        var jobsDir = Path.Combine(_root, ".orchestrator", "jobs");
        Directory.CreateDirectory(assetsDir);
        Directory.CreateDirectory(jobsDir);
        var evidence = Path.Combine(assetsDir, "evidence.png");
        File.WriteAllBytes(evidence, [137, 80, 78, 71]);

        for (var i = 0; i < 66; i++)
        {
            File.WriteAllText(Path.Combine(proposalsDir, $"survey-{i:D3}.md"), $$"""
                ---
                id: "survey-{{i:D3}}"
                generation: "2026-07-11"
                finding: "Finding {{i}}"
                evidenceScreenshot: "2026-07-11/assets/evidence.png"
                proposal: "Proposal {{i}}"
                estimatedEffort: "medium"
                severity: "medium"
                status: "proposed"
                spawnedTask: null
                ---
                """);
        }

        var service = BuildService(jobsDir);
        Assert.Equal(66, service.List(ProjectName)!.Count);

        // If GetEvidencePath falls back to List for each request, removing the
        // source documents makes the first lookup fail. The warm index must be
        // sufficient for every image request on the mounted screen.
        foreach (var markdown in Directory.EnumerateFiles(proposalsDir, "*.md"))
            File.Move(markdown, markdown + ".bak");

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
            Assert.Equal(evidence, service.GetEvidencePath(ProjectName, "2026-07-11/assets/evidence.png"));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Warm evidence lookups took {stopwatch.ElapsedMilliseconds} ms.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private ProjectProposalService BuildService(string jobsDir)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = Path.Combine(_root, ".task-repository"),
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = jobsDir,
                ["WatchPaths:0:RootPath"] = _root,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var mutations = new TaskMutationService(scanner, clients, registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        return new ProjectProposalService(scanner, registry, mutations, NullLogger<ProjectProposalService>.Instance);
    }
}
