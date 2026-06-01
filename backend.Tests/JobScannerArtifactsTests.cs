using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the Files-tab contract (F48): every `.md` directly in the job
/// root is surfaced with a kind classification (prompt / aspect / note /
/// other), status.md is excluded (it has its own Protocol tab), and the
/// sort order is prompt-first, then aspects alphabetical, then notes,
/// then everything else. Subfolder files are ignored.
/// </summary>
public class TaskScannerArtifactsTests : IDisposable
{
    private readonly string _watchPath;

    public TaskScannerArtifactsTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "agent-taskboard-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    private TaskScannerService BuildScanner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private string WriteJobRoot(string slug, string state = "2-ready")
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
        return dir;
    }

    [Fact]
    public void ListArtifacts_OnlyPromptMd_ReturnsSingleEntryClassifiedAsPrompt()
    {
        var dir = WriteJobRoot("only-prompt");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), "# task\n\nplease do the thing.");

        var response = BuildScanner().ListArtifacts("only-prompt", _watchPath);

        Assert.NotNull(response);
        Assert.Single(response!.Files);
        Assert.Equal("prompt.md", response.Files[0].Name);
        Assert.Equal(TaskArtifactKind.Prompt, response.Files[0].Kind);
        Assert.Null(response.Files[0].AspectName);
        Assert.True(response.Files[0].SizeBytes > 0);
    }

    [Fact]
    public void ListArtifacts_ExcludesStatusMd()
    {
        var dir = WriteJobRoot("with-status");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), "x");
        File.WriteAllText(Path.Combine(dir, "status.md"), "should not appear");

        var response = BuildScanner().ListArtifacts("with-status", _watchPath);

        Assert.NotNull(response);
        Assert.DoesNotContain(response!.Files, f => string.Equals(f.Name, "status.md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ListArtifacts_MultiFile_SortsPromptFirstThenAspectsThenNotesThenOther()
    {
        var dir = WriteJobRoot("multi-file");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), "p");
        File.WriteAllText(Path.Combine(dir, "aspect-requirement-fit.md"), "rf");
        File.WriteAllText(Path.Combine(dir, "aspect-code-quality.md"), "cq");
        File.WriteAllText(Path.Combine(dir, "REVIEW_NOTE.md"), "rn");
        File.WriteAllText(Path.Combine(dir, "ANOTHER_NOTES.md"), "an");
        File.WriteAllText(Path.Combine(dir, "follow-up-plan.md"), "fu");

        var response = BuildScanner().ListArtifacts("multi-file", _watchPath);

        Assert.NotNull(response);
        var names = response!.Files.Select(f => f.Name).ToArray();
        Assert.Equal(new[]
        {
            "prompt.md",
            "aspect-code-quality.md",
            "aspect-requirement-fit.md",
            "ANOTHER_NOTES.md",
            "REVIEW_NOTE.md",
            "follow-up-plan.md",
        }, names);

        var aspect = response.Files.First(f => f.Name == "aspect-code-quality.md");
        Assert.Equal(TaskArtifactKind.Aspect, aspect.Kind);
        Assert.Equal("code-quality", aspect.AspectName);

        var note = response.Files.First(f => f.Name == "REVIEW_NOTE.md");
        Assert.Equal(TaskArtifactKind.Note, note.Kind);

        var other = response.Files.First(f => f.Name == "follow-up-plan.md");
        Assert.Equal(TaskArtifactKind.Other, other.Kind);
    }

    [Fact]
    public void ListArtifacts_IgnoresFilesInSubfolders()
    {
        var dir = WriteJobRoot("nested-md");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), "p");

        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "deep-note.md"), "nope");

        var results = Path.Combine(dir, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "harvest.md"), "nope");

        var response = BuildScanner().ListArtifacts("nested-md", _watchPath);

        Assert.NotNull(response);
        Assert.Single(response!.Files);
        Assert.Equal("prompt.md", response.Files[0].Name);
    }

    [Fact]
    public void ListArtifacts_UnknownJob_ReturnsNull()
    {
        var response = BuildScanner().ListArtifacts("does-not-exist", _watchPath);
        Assert.Null(response);
    }

    [Fact]
    public void ReadJobFile_AllowsArbitraryMarkdownInJobRoot_ButRejectsPathTraversal()
    {
        var dir = WriteJobRoot("read-aspect");
        File.WriteAllText(Path.Combine(dir, "aspect-code-quality.md"), "verdict: ok");

        var scanner = BuildScanner();

        Assert.Equal("verdict: ok", scanner.ReadJobFile("read-aspect", "aspect-code-quality.md", _watchPath));
        Assert.Null(scanner.ReadJobFile("read-aspect", "../escape.md", _watchPath));
        Assert.Null(scanner.ReadJobFile("read-aspect", "logs/inner.md", _watchPath));
        Assert.Null(scanner.ReadJobFile("read-aspect", "not-markdown.txt", _watchPath));
    }
}
