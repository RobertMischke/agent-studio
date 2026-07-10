using AgentStudio.Docs;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkstreamCurationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workstream-curator-tests", Guid.NewGuid().ToString("N"));
    private readonly WorkstreamCurationService _sut = new(NullLogger<WorkstreamCurationService>.Instance);

    [Fact]
    public void RetroPilot_ClassifiesCapturedAgentStudioHistory_AndIsExactlyOnce()
    {
        Directory.CreateDirectory(_root);
        var project = new WatchPathEntry { Name = "agent-studio", RootPath = _root };
        var corpusPath = Path.Combine(AppContext.BaseDirectory, "Fixtures",
            "workstream-retro-pilot-agent-studio-2026-07-08.json");
        var corpus = JsonSerializer.Deserialize<HistoricalCorpus>(File.ReadAllText(corpusPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(corpus);
        Assert.Equal("Agent Studio task history from the 2026-07-08/09 incident window", corpus.Scope);
        Assert.All(corpus.Records, record =>
        {
            Assert.StartsWith("tasks/", record.SourceArtifact, StringComparison.Ordinal);
            Assert.Matches("^[A-F0-9]{64}$", record.SourceSha256);
        });
        var history = corpus.Records.Select(record => Task(record.TaskId, record.Title, record.Evidence)).ToArray();

        var result = _sut.RunRetroPilot(project, history, new DateTime(2026, 7, 9, 3, 0, 0, DateTimeKind.Utc));

        Assert.True(result.Ran);
        Assert.Equal(3, result.Signals);
        Assert.Equal(1, result.Knowledge);
        Assert.Equal(1, result.Decisions);
        var frame = Path.Combine(_root, "docs", "engineering-workstream");
        Assert.True(File.Exists(Path.Combine(frame, "20-development-signals", "generated", "post-processing-robustness.md")));
        Assert.True(File.Exists(Path.Combine(frame, "20-development-signals", "generated", "restart-resume-orphans.md")));
        Assert.True(File.Exists(Path.Combine(frame, "20-development-signals", "generated", "reissue-wipe.md")));
        Assert.True(File.Exists(Path.Combine(frame, ".curator", "retro-pilot-v1.json")));
        foreach (var record in corpus.Records)
        {
            var signal = File.ReadAllText(Path.Combine(frame, "20-development-signals", "generated",
                $"{record.ExpectedSignal}.md"));
            Assert.Contains($"`{record.TaskId}`", signal, StringComparison.Ordinal);
        }

        var second = _sut.RunRetroPilot(project, history, new DateTime(2026, 7, 10, 3, 0, 0, DateTimeKind.Utc));
        Assert.False(second.Ran);
        Assert.Equal("retro pilot already completed", second.Reason);
    }

    [Fact]
    public void Curator_MergesOnlyManagedDuplicates_AndCapsEvidenceGrowth()
    {
        Directory.CreateDirectory(_root);
        var project = new WatchPathEntry { Name = "agent-studio", RootPath = _root };
        _sut.RunRetroPilot(project, [Task("AGT-1", "Restart orphan", "restart orphan")],
            new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc));
        var area = Path.Combine(_root, "docs", "engineering-workstream", "20-development-signals", "generated");
        var source = Path.Combine(area, "restart-resume-orphans.md");
        var duplicate = Path.Combine(area, "duplicate.md");
        File.Copy(source, duplicate);
        File.AppendAllText(duplicate, string.Join("", Enumerable.Range(2, 30).Select(i => $"| `AGT-{i}` | evidence {i} |\n")));
        var operatorPage = Path.Combine(area, "operator-note.md");
        File.WriteAllText(operatorPage, "# Operator note\n\nMust remain untouched.\n");

        var result = _sut.Curate(project, new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, result.Merged);
        Assert.True(result.Condensed >= 0);
        Assert.False(File.Exists(duplicate));
        Assert.True(File.Exists(operatorPage));
        Assert.Equal(WorkstreamCurationService.MaxEvidenceRows,
            File.ReadLines(source).Count(line => line.StartsWith("| `", StringComparison.Ordinal)));
        Assert.Contains("last-verified: 2026-07-11T00:00:00Z", File.ReadAllText(source));
    }

    [Fact]
    public void Curator_PrunesOnlyEmptyLowConfidenceManagedOverflow()
    {
        Directory.CreateDirectory(_root);
        var project = new WatchPathEntry { Name = "agent-studio", RootPath = _root };
        _sut.RunRetroPilot(project, [], new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc));
        var area = Path.Combine(_root, "docs", "engineering-workstream", "20-development-signals", "generated");
        Directory.CreateDirectory(area);
        for (var i = 0; i < 41; i++)
        {
            File.WriteAllText(Path.Combine(area, $"candidate-{i:00}.md"),
                $"---\nmanaged-by: workstream-collector\ncanonical-key: candidate-{i:00}\nconfidence: 0.10\nlast-verified: 2026-07-01T00:00:00Z\n---\n\n# Candidate {i}\n");
        }
        var operatorPage = Path.Combine(area, "operator-note.md");
        File.WriteAllText(operatorPage, "# Operator note\n");

        var result = _sut.Curate(project, new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, result.Pruned);
        Assert.Equal(40, Directory.EnumerateFiles(area, "candidate-*.md").Count());
        Assert.True(File.Exists(operatorPage));
    }

    private TaskInfo Task(string id, string title, string log)
    {
        var folder = Path.Combine(_root, "tasks", id);
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        File.WriteAllText(Path.Combine(folder, "logs", "cli-output.log"), log);
        return new TaskInfo { Id = id, Key = id, Title = title, ProjectName = "agent-studio", FolderPath = folder };
    }

    private sealed record HistoricalCorpus(string CapturedAtUtc, string Scope, HistoricalRecord[] Records);
    private sealed record HistoricalRecord(string TaskId, string Title, string Evidence, string SourceArtifact,
        string SourceSha256, string ExpectedSignal);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
