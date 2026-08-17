using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers <see cref="ProjectDocsService.ReadWikiAsset"/>, the endpoint behind
/// <c>GET /api/projects/{projectName}/wiki/assets/{**relPath}</c>. Regression
/// coverage for a dossier that references an image nested under its own
/// <c>assets/</c> subfolder (e.g. <c>docs/operations/&lt;slug&gt;/assets/foo.png</c>),
/// the pattern every dossier/Workbench screenshot uses.
/// </summary>
public class WikiAssetResolutionTests : IDisposable
{
    private const string ProjectName = "AssetProj";

    private readonly string _tempDir;
    private readonly string _docsDir;

    public WikiAssetResolutionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-asset-tests-" + Guid.NewGuid().ToString("N"));
        _docsDir = Path.Combine(_tempDir, "docs");
        Directory.CreateDirectory(_docsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void ReadWikiAsset_ResolvesAnImageNestedUnderADossierAssetsFolder()
    {
        WriteAsset("operations/timeline-redesign/assets/task-timeline-current--real.png");

        var asset = BuildDocsService().ReadWikiAsset(
            ProjectName, "operations/timeline-redesign/assets/task-timeline-current--real.png");

        Assert.NotNull(asset);
        Assert.Equal("image/png", asset!.Value.ContentType);
        Assert.True(File.Exists(asset.Value.Path));
    }

    [Fact]
    public void ReadWikiAsset_ResolvesAnImageUnderAMultiPageDossierPagesFolder()
    {
        WriteAsset("operations/admin-design-guideline/pages/assets/applied-surfaces--real.png");

        var asset = BuildDocsService().ReadWikiAsset(
            ProjectName, "operations/admin-design-guideline/pages/assets/applied-surfaces--real.png");

        Assert.NotNull(asset);
        Assert.Equal("image/png", asset!.Value.ContentType);
    }

    [Fact]
    public void ReadWikiAsset_RejectsPathsThatEscapeTheDocsRoot()
    {
        var docs = BuildDocsService();

        Assert.Null(docs.ReadWikiAsset(ProjectName, "../secrets.png"));
        Assert.Null(docs.ReadWikiAsset(ProjectName, "operations/timeline-redesign/assets/../../../secrets.png"));
    }

    [Fact]
    public void ReadWikiAsset_RejectsAMissingFileAndADisallowedExtension()
    {
        WriteAsset("operations/timeline-redesign/assets/note.txt");

        var docs = BuildDocsService();

        Assert.Null(docs.ReadWikiAsset(ProjectName, "operations/timeline-redesign/assets/missing.png"));
        Assert.Null(docs.ReadWikiAsset(ProjectName, "operations/timeline-redesign/assets/note.txt"));
    }

    // ---- helpers ----

    private void WriteAsset(string relPath)
    {
        var full = Path.Combine(_docsDir, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [0x89, 0x50, 0x4E, 0x47]);
    }

    private ProjectDocsService BuildDocsService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:RootPath"] = _tempDir,
                ["WatchPaths:0:Path"] = Path.Combine(_tempDir, ".orchestrator", "jobs"),
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        return new ProjectDocsService(scanner, registry, NullLogger<ProjectDocsService>.Instance);
    }
}
