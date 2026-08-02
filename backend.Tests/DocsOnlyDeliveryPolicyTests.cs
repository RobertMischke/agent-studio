using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The AGT-2417 docs rule classifier: docs-only deliveries (documentation and
/// task evidence, no product code) take the light release gate. Unknown or
/// empty diffs fail closed to the strict path.
/// </summary>
public sealed class DocsOnlyDeliveryPolicyTests
{
    [Theory]
    [InlineData("docs/operations/research-deliverables/index.html")]
    [InlineData("docs/research/report.html")]
    [InlineData("README.md")]
    [InlineData("backend/Features/Pipeline/NOTES.md")]
    [InlineData("results/AGT-2442/report.html")]
    [InlineData("attachments/screenshot.png")]
    public void DocsPaths_AreDocsOnly(string path)
        => Assert.True(DocsOnlyDeliveryPolicy.IsDocsOnly([path]));

    [Theory]
    [InlineData("backend/Features/Pipeline/MergeIntoDevelopRunner.cs")]
    [InlineData("prompts/runtime/mode-framing-research.md.meta.json")]
    [InlineData("frontend/src/app/app.component.ts")]
    [InlineData("scripts/deploy.sh")]
    [InlineData("appsettings.json")]
    public void CodeAndConfigPaths_AreNotDocsOnly(string path)
        => Assert.False(DocsOnlyDeliveryPolicy.IsDocsOnly([path]));

    [Fact]
    public void MixedDiff_IsNotDocsOnly()
        => Assert.False(DocsOnlyDeliveryPolicy.IsDocsOnly(
            ["docs/note.md", "backend/Program.cs"]));

    [Fact]
    public void UnknownOrEmptyDiff_FailsClosed()
    {
        Assert.False(DocsOnlyDeliveryPolicy.IsDocsOnly(null));
        Assert.False(DocsOnlyDeliveryPolicy.IsDocsOnly([]));
    }
}
