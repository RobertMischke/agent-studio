using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Tests for the evidence-completion helpers (AGT-2022): the results/ folder
/// inventory and the card-mode framing that every review / aspect prompt now
/// carries so an empty git diff is never mis-read as "deliverables missing".
/// </summary>
public class ReviewEvidenceContextTests : IDisposable
{
    private readonly string _jobFolder;

    public ReviewEvidenceContextTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "review-evidence-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ResultsInventory_NoFolder_ReportsAbsenceExplicitly()
    {
        var inventory = ResultsInventory.Render(_jobFolder);
        Assert.Equal("No results/ folder present for this task.", inventory);
    }

    [Fact]
    public void ResultsInventory_EmptyFolder_ReportsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_jobFolder, "results"));
        var inventory = ResultsInventory.Render(_jobFolder);
        Assert.Contains("empty", inventory);
    }

    [Fact]
    public void ResultsInventory_ReadOnlyTaskWithArtifacts_ListsFilesAndExcerptsText()
    {
        // A read-only / concept task with results/ artefacts must produce a
        // non-empty inventory so the reviewer never false-BLOCKs it as
        // "deliverables missing" on an empty code diff (AGT-1915).
        var results = Path.Combine(_jobFolder, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "analysis.md"), "# Findings\n\nThe root cause is X.");
        File.WriteAllBytes(Path.Combine(results, "shot--real.png"), new byte[] { 1, 2, 3, 4 });

        var inventory = ResultsInventory.Render(_jobFolder);

        Assert.Contains("results/ folder contains 2 file(s)", inventory);
        Assert.Contains("analysis.md", inventory);
        Assert.Contains("shot--real.png", inventory);
        // Text artefact excerpted; binary artefact listed but not excerpted.
        Assert.Contains("The root cause is X.", inventory);
        Assert.DoesNotContain("Excerpt of shot--real.png", inventory);
    }

    [Fact]
    public void ResultsInventory_WalksNestedFolders()
    {
        var nested = Path.Combine(_jobFolder, "results", "sub");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "note.txt"), "nested deliverable");

        var inventory = ResultsInventory.Render(_jobFolder);

        Assert.Contains("sub/note.txt", inventory);
        Assert.Contains("nested deliverable", inventory);
    }

    [Theory]
    [InlineData("planning")]
    [InlineData("research")]
    public void ReviewCardMode_ReadOnlyModes_SayNoCodeDiffIsExpected(string mode)
    {
        var described = ReviewCardMode.Describe(mode);
        Assert.Contains("read-only", described);
        Assert.Contains("NO code diff", described);
        Assert.Contains("Do NOT treat an empty", described);
    }

    [Theory]
    [InlineData("coding")]
    [InlineData(null)]
    [InlineData("something-unknown")]
    public void ReviewCardMode_CodingOrUnknown_ExpectsACodeChangeSet(string? mode)
    {
        var described = ReviewCardMode.Describe(mode);
        Assert.Contains("coding", described);
        Assert.Contains("code change set is expected", described);
    }
}
