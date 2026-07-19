using System.Text.Json;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewDecisionLogIndexTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        "review-decision-index-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BoardLookup_ReusesFullLoad_AndProcessAppendUpdatesCachedLatest()
    {
        const string project = "alpha";
        var first = Record(project, "job-1", ReviewDecisionKind.Reissue);
        ReviewDecisionLog.Append(_workspace, first);

        var jobs = new[]
        {
            new TaskInfo
            {
                Id = "job-1",
                TaskKey = "alpha::job-1",
                ProjectName = project,
                WatchPath = Path.Combine(_workspace, "projects", project),
                State = TaskStates.HumanReview,
            },
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
            })
            .Build();

        var initial = TaskEndpointHelpers.BuildOrchestratorVerdictLookup(jobs, configuration);
        Assert.Equal("reissue", initial["alpha::job-1"]);

        ReviewDecisionLog.Append(
            _workspace,
            Record(project, "job-1", ReviewDecisionKind.Escalate));

        var updated = TaskEndpointHelpers.BuildOrchestratorVerdictLookup(jobs, configuration);
        Assert.Equal("escalate", updated["alpha::job-1"]);

        var diagnostics = ReviewDecisionLog.GetLatestIndexDiagnostics(_workspace, project);
        Assert.Equal(1, diagnostics.FullLoads);
        Assert.Equal(0, diagnostics.IncrementalLoads);
        Assert.True(diagnostics.CacheHits >= 1);
    }

    [Fact]
    public void LatestIndex_ExternalAppend_ParsesOnlyAppendedRecords()
    {
        const string project = "external-append";
        ReviewDecisionLog.Append(
            _workspace,
            Record(project, "job-1", ReviewDecisionKind.Reissue));

        var initial = ReviewDecisionLog.ReadLatestByJob(_workspace, project);
        Assert.Equal(ReviewDecisionKind.Reissue, initial["job-1"].Kind);

        AppendExternally(
            project,
            Record(project, "job-1", ReviewDecisionKind.AcceptAsDone));
        AppendExternally(
            project,
            Record(project, "job-2", ReviewDecisionKind.Escalate));

        var updated = ReviewDecisionLog.ReadLatestByJob(_workspace, project);

        Assert.Equal(ReviewDecisionKind.AcceptAsDone, updated["job-1"].Kind);
        Assert.Equal(ReviewDecisionKind.Escalate, updated["job-2"].Kind);
        var diagnostics = ReviewDecisionLog.GetLatestIndexDiagnostics(_workspace, project);
        Assert.Equal(1, diagnostics.FullLoads);
        Assert.Equal(1, diagnostics.IncrementalLoads);
    }

    [Fact]
    public void LatestIndex_ExternalRotation_RebuildsWithoutLeakingOldRecords()
    {
        const string project = "rotation";
        ReviewDecisionLog.Append(
            _workspace,
            Record(project, "old-job", ReviewDecisionKind.Reissue));
        Assert.Contains("old-job", ReviewDecisionLog.ReadLatestByJob(_workspace, project).Keys);

        var path = ReviewDecisionLog.DecisionsFile(_workspace, project);
        File.Move(path, path + ".rotated");
        WriteReplacement(
            path,
            Record(project, "new-job", ReviewDecisionKind.Escalate));

        var updated = ReviewDecisionLog.ReadLatestByJob(_workspace, project);

        Assert.DoesNotContain("old-job", updated.Keys);
        Assert.Equal(ReviewDecisionKind.Escalate, updated["new-job"].Kind);
        var diagnostics = ReviewDecisionLog.GetLatestIndexDiagnostics(_workspace, project);
        Assert.Equal(2, diagnostics.FullLoads);
        Assert.Equal(0, diagnostics.IncrementalLoads);
    }

    [Fact]
    public void LatestIndex_SameLengthSameTimestampRewrite_IsDetected()
    {
        const string project = "same-metadata";
        var path = ReviewDecisionLog.DecisionsFile(_workspace, project);
        var original = Record(project, "job-1", ReviewDecisionKind.Reissue);
        var replacement = original with { Kind = ReviewDecisionKind.Skipped };
        var originalLine = SerializeLine(original);
        var replacementLine = SerializeLine(replacement);
        Assert.Equal(originalLine.Length, replacementLine.Length);

        WriteRaw(path, originalLine);
        var timestamp = File.GetLastWriteTimeUtc(path);
        Assert.Equal(
            ReviewDecisionKind.Reissue,
            ReviewDecisionLog.ReadLatestByJob(_workspace, project)["job-1"].Kind);

        WriteRaw(path, replacementLine);
        File.SetLastWriteTimeUtc(path, timestamp);

        var updated = ReviewDecisionLog.ReadLatestByJob(_workspace, project);

        Assert.Equal(ReviewDecisionKind.Skipped, updated["job-1"].Kind);
        var diagnostics = ReviewDecisionLog.GetLatestIndexDiagnostics(_workspace, project);
        Assert.Equal(2, diagnostics.FullLoads);
    }

    [Fact]
    public void PerFileLocks_KeepConcurrentProjectAppendsIndependentAndComplete()
    {
        const int count = 100;
        Parallel.For(0, count, i =>
        {
            var project = i % 2 == 0 ? "project-a" : "project-b";
            ReviewDecisionLog.Append(
                _workspace,
                Record(project, $"job-{i}", ReviewDecisionKind.Reissue));
        });

        Assert.Equal(count / 2, ReviewDecisionLog.ReadAll(_workspace, "project-a").Count);
        Assert.Equal(count / 2, ReviewDecisionLog.ReadAll(_workspace, "project-b").Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { /* best effort */ }
    }

    private static ReviewDecisionRecord Record(
        string project,
        string jobId,
        ReviewDecisionKind kind)
        => new(
            CreatedAt: DateTime.UtcNow,
            JobId: jobId,
            Project: project,
            Kind: kind,
            Reason: "reason",
            Prompt: "prompt",
            Response: "response",
            FollowUp: "follow-up");

    private void AppendExternally(string project, ReviewDecisionRecord record)
    {
        var path = ReviewDecisionLog.DecisionsFile(_workspace, project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, SerializeLine(record));
    }

    private static void WriteReplacement(string path, ReviewDecisionRecord record)
        => WriteRaw(path, SerializeLine(record));

    private static void WriteRaw(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string SerializeLine(ReviewDecisionRecord record)
        => JsonSerializer.Serialize(record, Json) + Environment.NewLine;
}
