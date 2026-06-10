using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the reference-validation contract: a job-local image link in
/// <c>status.md</c> that points at a missing file must surface as a broken
/// reference (so it becomes a visible review finding), while links that exist,
/// escape the job folder, or are not the job's to validate (external URLs,
/// data URIs, rooted/absolute paths) must be left alone. Getting either side
/// wrong is harmful: a false positive nags the reviewer; a false negative is
/// the silently-empty image this feature exists to kill.
/// </summary>
public class ProtocolImageReferenceValidatorTests : IDisposable
{
    private readonly string _jobFolder;

    public ProtocolImageReferenceValidatorTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "img-ref-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    private void WriteResultFile(string relativeUnderResults)
    {
        var full = Path.Combine(_jobFolder, "results", relativeUnderResults.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "not-really-an-image");
    }

    [Fact]
    public void NullOrEmptyInputs_ReturnEmpty()
    {
        Assert.Empty(ProtocolImageReferenceValidator.FindBrokenReferences(null, _jobFolder));
        Assert.Empty(ProtocolImageReferenceValidator.FindBrokenReferences("   ", _jobFolder));
        Assert.Empty(ProtocolImageReferenceValidator.FindBrokenReferences("![](results/x.png)", ""));
    }

    [Fact]
    public void ExistingResultsReference_IsNotBroken()
    {
        WriteResultFile("proof.png");
        var broken = ProtocolImageReferenceValidator.FindBrokenReferences("![](results/proof.png)", _jobFolder);
        Assert.Empty(broken);
    }

    [Fact]
    public void MissingResultsReference_IsBroken()
    {
        var broken = ProtocolImageReferenceValidator.FindBrokenReferences("![ok](results/missing.png)", _jobFolder);
        Assert.Equal(["results/missing.png"], broken);
    }

    [Fact]
    public void NestedPlaywrightReference_IsCheckedAndReportedForwardSlashed()
    {
        var broken = ProtocolImageReferenceValidator.FindBrokenReferences(
            @"![](results\playwright\spec\shot.png)", _jobFolder);
        Assert.Equal(["results/playwright/spec/shot.png"], broken);
    }

    [Fact]
    public void BareFilename_ResolvesUnderResults()
    {
        // Legacy fallback: a bare filename is resolved under results/.
        WriteResultFile("legacy.png");
        Assert.Empty(ProtocolImageReferenceValidator.FindBrokenReferences("![](legacy.png)", _jobFolder));

        var broken = ProtocolImageReferenceValidator.FindBrokenReferences("![](gone.png)", _jobFolder);
        Assert.Equal(["results/gone.png"], broken);
    }

    [Fact]
    public void AttachmentsReference_IsChecked()
    {
        var full = Path.Combine(_jobFolder, "attachments", "input.png");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");

        Assert.Empty(ProtocolImageReferenceValidator.FindBrokenReferences("![](attachments/input.png)", _jobFolder));
        Assert.Equal(
            ["attachments/nope.png"],
            ProtocolImageReferenceValidator.FindBrokenReferences("![](attachments/nope.png)", _jobFolder));
    }

    [Theory]
    [InlineData("![](https://example.com/x.png)")]
    [InlineData("![](http://example.com/x.png)")]
    [InlineData("![](//cdn.example.com/x.png)")]
    [InlineData("![](data:image/png;base64,AAAA)")]
    [InlineData("![](/etc/passwd.png)")]
    [InlineData("![](C:/Windows/secret.png)")]
    [InlineData("![](../escape.png)")]
    [InlineData("![](results/../../escape.png)")]
    public void ExternalRootedOrEscapingReferences_AreLeftAlone(string markdown)
    {
        Assert.Empty(ProtocolImageReferenceValidator.FindBrokenReferences(markdown, _jobFolder));
    }

    [Fact]
    public void NonImageExtension_IsIgnored()
    {
        Assert.Empty(ProtocolImageReferenceValidator.FindBrokenReferences("![](results/notes.txt)", _jobFolder));
    }

    [Fact]
    public void DuplicateBrokenReference_ReportedOnce_FirstSeenOrder()
    {
        var md = "![a](results/b.png) then ![](results/a.png) then ![](results/b.png)";
        var broken = ProtocolImageReferenceValidator.FindBrokenReferences(md, _jobFolder);
        Assert.Equal(["results/b.png", "results/a.png"], broken);
    }

    [Fact]
    public void MixedExistingAndMissing_OnlyMissingReported()
    {
        WriteResultFile("here.png");
        var md = "![](results/here.png) and ![](results/there.png)";
        var broken = ProtocolImageReferenceValidator.FindBrokenReferences(md, _jobFolder);
        Assert.Equal(["results/there.png"], broken);
    }
}
