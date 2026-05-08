using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the parsing contract for <c>results/review-evidence.jsonl</c>.
/// The file is the durable surface for task-level review findings; a single
/// malformed row must not break the rest of the file or the JobDetail
/// endpoint that reads it. Empty file, missing file, valid finding,
/// malformed line, artifact paths, file refs, and append-then-fold-by-id
/// all need explicit coverage.
/// </summary>
public class ReviewEvidenceLogTests : IDisposable
{
    private readonly string _jobFolder;

    public ReviewEvidenceLogTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "evidence-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    private void WriteEvidenceLines(params string[] lines)
    {
        var dir = JobPaths.ResultsDir(_jobFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(JobPaths.ReviewEvidenceLog(_jobFolder),
            string.Join("\n", lines) + "\n",
            Encoding.UTF8);
    }

    [Fact]
    public void ReadLatestPerId_MissingFile_ReturnsEmpty()
    {
        var result = ReviewEvidenceLog.ReadLatestPerId(_jobFolder);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadLatestPerId_EmptyFile_ReturnsEmpty()
    {
        var dir = JobPaths.ResultsDir(_jobFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(JobPaths.ReviewEvidenceLog(_jobFolder), "");

        var result = ReviewEvidenceLog.ReadLatestPerId(_jobFolder);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadLatestPerId_ValidFinding_IsParsed()
    {
        WriteEvidenceLines(
            """{"id":"e1","source":"security-audit","severity":"high","title":"Token leaked","body":"details","createdAt":"2026-05-08T12:34:00Z"}"""
        );

        var result = ReviewEvidenceLog.ReadLatestPerId(_jobFolder);

        var entry = Assert.Single(result);
        Assert.Equal("e1", entry.Id);
        Assert.Equal(ReviewEvidenceSources.SecurityAudit, entry.Source);
        Assert.Equal(ReviewEvidenceSeverities.High, entry.Severity);
        Assert.Equal("Token leaked", entry.Title);
        Assert.Equal("details", entry.Body);
        Assert.False(entry.Acknowledged);
    }

    [Fact]
    public void ReadLatestPerId_MalformedLineDoesNotBreakRest()
    {
        WriteEvidenceLines(
            """{"id":"e1","severity":"high","title":"first","createdAt":"2026-05-08T12:34:00Z"}""",
            "this is not json at all",
            """{not even close}""",
            "",
            """{"id":"e2","severity":"warn","title":"second","createdAt":"2026-05-08T12:35:00Z"}"""
        );

        var result = ReviewEvidenceLog.ReadLatestPerId(_jobFolder);

        Assert.Equal(2, result.Count);
        Assert.Equal("e1", result[0].Id);
        Assert.Equal("e2", result[1].Id);
    }

    [Fact]
    public void ReadLatestPerId_ArtifactsAndFileRefs_AreCarriedThrough()
    {
        WriteEvidenceLines(
            """{"id":"e1","severity":"warn","title":"x","createdAt":"2026-05-08T12:34:00Z","artifacts":["results/proof.png","results/playwright/spec/file.png"],"fileRefs":["backend/Foo.cs:42","frontend/Bar.ts"]}"""
        );

        var entry = Assert.Single(ReviewEvidenceLog.ReadLatestPerId(_jobFolder));
        Assert.Equal(["results/proof.png", "results/playwright/spec/file.png"], entry.Artifacts);
        Assert.Equal(["backend/Foo.cs:42", "frontend/Bar.ts"], entry.FileRefs);
    }

    [Fact]
    public void ReadLatestPerId_UnknownSourceAndSeverity_AreNormalized()
    {
        WriteEvidenceLines(
            """{"id":"e1","source":"made-up","severity":"catastrophic","title":"x","createdAt":"2026-05-08T12:34:00Z"}"""
        );

        var entry = Assert.Single(ReviewEvidenceLog.ReadLatestPerId(_jobFolder));
        Assert.Equal(ReviewEvidenceSources.Other, entry.Source);
        Assert.Equal(ReviewEvidenceSeverities.Info, entry.Severity);
    }

    [Fact]
    public void ReadLatestPerId_FoldsLatestPerId()
    {
        WriteEvidenceLines(
            """{"id":"e1","severity":"high","title":"original","createdAt":"2026-05-08T12:34:00Z","acknowledged":false}""",
            """{"id":"e2","severity":"info","title":"other","createdAt":"2026-05-08T12:34:10Z"}""",
            """{"id":"e1","severity":"high","title":"original","createdAt":"2026-05-08T12:35:00Z","acknowledged":true}"""
        );

        var result = ReviewEvidenceLog.ReadLatestPerId(_jobFolder);

        Assert.Equal(2, result.Count);
        Assert.Equal("e1", result[0].Id);
        Assert.True(result[0].Acknowledged);
        Assert.Equal("e2", result[1].Id);
        Assert.False(result[1].Acknowledged);
    }

    [Fact]
    public void ReadLatestPerId_MissingTitleOrId_IsSkipped()
    {
        WriteEvidenceLines(
            """{"severity":"high","title":"missing-id","createdAt":"2026-05-08T12:34:00Z"}""",
            """{"id":"e2","severity":"info","createdAt":"2026-05-08T12:34:10Z"}""",
            """{"id":"e3","severity":"info","title":"valid","createdAt":"2026-05-08T12:34:20Z"}"""
        );

        var result = ReviewEvidenceLog.ReadLatestPerId(_jobFolder);
        Assert.Single(result);
        Assert.Equal("e3", result[0].Id);
    }

    [Fact]
    public void Append_CreatesResultsDirIfMissing()
    {
        var entry = new ReviewEvidenceEntry
        {
            Id = "e1",
            Source = ReviewEvidenceSources.HumanNote,
            Severity = ReviewEvidenceSeverities.Warn,
            Title = "appended",
            CreatedAt = DateTime.UtcNow
        };

        ReviewEvidenceLog.Append(_jobFolder, entry);

        Assert.True(File.Exists(JobPaths.ReviewEvidenceLog(_jobFolder)));
        var roundTrip = Assert.Single(ReviewEvidenceLog.ReadLatestPerId(_jobFolder));
        Assert.Equal("e1", roundTrip.Id);
        Assert.Equal("appended", roundTrip.Title);
    }
}

/// <summary>
/// Integration test for <see cref="JobScannerService.GetJobDetail"/>. Builds
/// a job folder on disk, drops a review-evidence file next to it, then asserts
/// that the field surfaces on <see cref="JobDetail.ReviewEvidence"/>.
/// </summary>
public class JobDetailReviewEvidenceTests : IDisposable
{
    private readonly string _watchPath;

    public JobDetailReviewEvidenceTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "jobdetail-evidence-" + Guid.NewGuid().ToString("N"));
        foreach (var state in JobStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    private JobScannerService BuildScanner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
    }

    [Fact]
    public void GetJobDetail_NoEvidenceFile_ReturnsEmptyList()
    {
        var dir = Path.Combine(_watchPath, JobStates.AutoReview, "demo-task");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            """{"id":"demo-task","title":"demo","state":"4-auto-review","order":1,"agent":"claude"}""");

        var detail = BuildScanner().GetJobDetail("demo-task", _watchPath);

        Assert.NotNull(detail);
        Assert.Empty(detail!.ReviewEvidence);
    }

    [Fact]
    public void GetJobDetail_WithEvidence_SurfacesEntries()
    {
        var dir = Path.Combine(_watchPath, JobStates.HumanReview, "with-evidence");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            """{"id":"with-evidence","title":"with","state":"5-human-review","order":1,"agent":"claude"}""");

        var resultsDir = JobPaths.ResultsDir(dir);
        Directory.CreateDirectory(resultsDir);
        var lines = new[]
        {
            """{"id":"high-1","source":"security-audit","severity":"high","title":"Token leaked","createdAt":"2026-05-08T12:34:00Z","fileRefs":["backend/Auth.cs:42"]}""",
            "// junk that is not a json line",
            """{"id":"warn-1","source":"code-review","severity":"warn","title":"Missing null guard","createdAt":"2026-05-08T12:35:00Z"}"""
        };
        File.WriteAllText(JobPaths.ReviewEvidenceLog(dir), string.Join("\n", lines) + "\n", Encoding.UTF8);

        var detail = BuildScanner().GetJobDetail("with-evidence", _watchPath);

        Assert.NotNull(detail);
        Assert.Equal(2, detail!.ReviewEvidence.Count);

        // High severity sorts first in the panel, but the parser preserves
        // file order. The frontend sort lives in the component; the API
        // returns latest-per-id in first-seen order.
        Assert.Equal("high-1", detail.ReviewEvidence[0].Id);
        Assert.Equal(ReviewEvidenceSeverities.High, detail.ReviewEvidence[0].Severity);
        Assert.Equal(["backend/Auth.cs:42"], detail.ReviewEvidence[0].FileRefs);

        Assert.Equal("warn-1", detail.ReviewEvidence[1].Id);
        Assert.Equal(ReviewEvidenceSeverities.Warn, detail.ReviewEvidence[1].Severity);
    }
}
