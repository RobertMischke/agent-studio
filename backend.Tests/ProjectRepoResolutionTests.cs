using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The docs/wiki root is &lt;repo&gt;/docs by convention — never a setting of
/// its own. These tests pin the repository resolution order behind that
/// convention: registry record → legacy WatchPaths config → derivation from
/// the in-repo storage layout (&lt;repo&gt;/.orchestrator/jobs).
/// </summary>
public sealed class ProjectRepoResolutionTests
{
    private static ProjectRecord Record(string? repositoryPath = null, string storage = "")
        => new() { Id = "PROJ-001", DisplayName = "Demo", StorageLocation = storage, RepositoryPath = repositoryPath };

    private static WatchPathEntry Entry(string path = "", string rootPath = "", string repositoryPath = "")
        => new() { Name = "Demo", Path = path, RootPath = rootPath, RepositoryPath = repositoryPath };

    [Fact]
    public void RegistryRepositoryPath_WinsOverEverything()
    {
        var resolved = ProjectRepoResolver.Resolve(
            Record(repositoryPath: @"C:\repos\from-registry", storage: @"C:\x\repo\.orchestrator\jobs"),
            Entry(path: @"C:\x\repo\.orchestrator\jobs", rootPath: @"C:\repos\from-rootpath", repositoryPath: @"C:\repos\from-watchpath"));
        Assert.Equal(@"C:\repos\from-registry", resolved);
    }

    [Fact]
    public void WatchPathRepositoryPath_BeatsRootPathAndDerivation()
    {
        var resolved = ProjectRepoResolver.Resolve(
            Record(storage: @"C:\x\repo\.orchestrator\jobs"),
            Entry(path: @"C:\x\repo\.orchestrator\jobs", rootPath: @"C:\repos\from-rootpath", repositoryPath: @"C:\repos\from-watchpath"));
        Assert.Equal(@"C:\repos\from-watchpath", resolved);
    }

    [Fact]
    public void RootPath_BeatsDerivation()
    {
        var resolved = ProjectRepoResolver.Resolve(
            Record(storage: @"C:\x\repo\.orchestrator\jobs"),
            Entry(path: @"C:\x\repo\.orchestrator\jobs", rootPath: @"C:\repos\from-rootpath"));
        Assert.Equal(@"C:\repos\from-rootpath", resolved);
    }

    [Fact]
    public void InRepoStorageLayout_DerivesRepositoryWithoutAnyConfig()
    {
        var resolved = ProjectRepoResolver.Resolve(
            Record(storage: @"C:\Projects\my-lib\.orchestrator\jobs"),
            Entry(path: @"C:\Projects\my-lib\.orchestrator\jobs"));
        Assert.Equal(@"C:\Projects\my-lib", resolved);
    }

    [Fact]
    public void Derivation_FallsBackToWatchPathEntry_WhenNoRecordExists()
    {
        var resolved = ProjectRepoResolver.Resolve(
            record: null,
            entry: Entry(path: @"C:\Projects\my-lib\.orchestrator\jobs"));
        Assert.Equal(@"C:\Projects\my-lib", resolved);
    }

    [Theory]
    [InlineData(@"C:\Projects\workspace\projects\demo")]      // central task storage
    [InlineData(@"C:\Projects\repo\.orchestrator\other")]     // not the jobs folder
    [InlineData(@"C:\Projects\repo\orchestrator\jobs")]       // missing the dot
    [InlineData(@"C:\.orchestrator\jobs")]                    // no repo above
    [InlineData("")]
    [InlineData(null)]
    public void NonMatchingStorage_DoesNotDerive(string? storage)
    {
        Assert.Null(ProjectRepoResolver.DeriveFromStorage(storage));
    }

    [Fact]
    public void TrailingSeparator_DoesNotBreakDerivation()
    {
        Assert.Equal(@"C:\Projects\my-lib",
            ProjectRepoResolver.DeriveFromStorage(@"C:\Projects\my-lib\.orchestrator\jobs\"));
    }

    [Fact]
    public void TaskOnlyProject_ResolvesToNull()
    {
        var resolved = ProjectRepoResolver.Resolve(
            Record(storage: @"C:\Projects\workspace\projects\privat"),
            Entry(path: @"C:\Projects\workspace\projects\privat"));
        Assert.Null(resolved);
    }
}

/// <summary>
/// Name-based resolution (ResolveForProject) pairs the registry record by
/// storage location when a watch-path entry matches, so a same-named record
/// of a DIFFERENT project can never capture the docs surface. Uses real
/// scanner + registry instances over in-memory config.
/// </summary>
public sealed class ProjectRepoResolverPairingTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectRepoResolverPairingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-repo-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    private static (TaskScannerService Scanner, ProjectRegistry Registry) Build(
        Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        return (scanner, registry);
    }

    [Fact]
    public void SameNamedRegistryRecordOfOtherProject_CannotCaptureWatchPathProject()
    {
        var storageA = Path.Combine(_tempDir, "workspace", "projects", "website");
        Directory.CreateDirectory(storageA);
        var (scanner, registry) = Build(new()
        {
            ["TaskRepository"] = Path.Combine(_tempDir, "workspace"),
            ["WatchPaths:0:Name"] = "Website",
            ["WatchPaths:0:Path"] = storageA,
        });
        // Project A backs the watch path; project B is an unrelated record
        // that happens to carry the same display name plus a RepositoryPath.
        registry.EnsureProjectForStorage(storageA, "Website", "ws-1");
        var other = registry.EnsureProjectForStorage(
            Path.Combine(_tempDir, "workspace", "projects", "other"), "Other", "ws-1");
        registry.Rename(other.Id, "Website");

        // Record B's repository must not win: project A has no repository at
        // all, so resolution yields null instead of B's path.
        Assert.Null(ProjectRepoResolver.ResolveForProject("Website", scanner, registry));
    }

    [Fact]
    public void RegistryOnlyProject_ResolvesViaDisplayName()
    {
        var repo = Path.Combine(_tempDir, "some-repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var (scanner, registry) = Build(new()
        {
            ["TaskRepository"] = Path.Combine(_tempDir, "workspace"),
        });
        var rec = registry.EnsureProjectForStorage(
            Path.Combine(_tempDir, "workspace", "projects", "solo"), "Solo", "ws-1");
        registry.SetRepositoryPath(rec.Id, repo);

        Assert.Equal(repo, ProjectRepoResolver.ResolveForProject("Solo", scanner, registry));
    }

    [Fact]
    public void SetRepositoryPath_RejectsUncRelativeMissingAndNonGitPaths()
    {
        var (_, registry) = Build(new() { ["TaskRepository"] = Path.Combine(_tempDir, "workspace") });
        var rec = registry.EnsureProjectForStorage(
            Path.Combine(_tempDir, "workspace", "projects", "p"), "P", "ws-1");

        Assert.Throws<ArgumentException>(() => registry.SetRepositoryPath(rec.Id, @"\\evil\share"));
        Assert.Throws<ArgumentException>(() => registry.SetRepositoryPath(rec.Id, @"relative\path"));
        Assert.Throws<ArgumentException>(() => registry.SetRepositoryPath(rec.Id, Path.Combine(_tempDir, "does-not-exist")));

        var noGit = Path.Combine(_tempDir, "no-git");
        Directory.CreateDirectory(noGit);
        Assert.Throws<ArgumentException>(() => registry.SetRepositoryPath(rec.Id, noGit));

        var gitRepo = Path.Combine(_tempDir, "with-git");
        Directory.CreateDirectory(Path.Combine(gitRepo, ".git"));
        Assert.Equal(gitRepo, registry.SetRepositoryPath(rec.Id, gitRepo).RepositoryPath);
        Assert.Null(registry.SetRepositoryPath(rec.Id, null).RepositoryPath);
    }

    /// <summary>
    /// SetRootPath mirrors SetRepositoryPath's path validation (UNC /
    /// relative / missing all rejected) but deliberately skips the
    /// git-checkout requirement: a CLI working directory can legitimately be
    /// a subfolder of a repo (e.g. Runbook's &lt;repo&gt;/App) rather than the
    /// checkout root itself, so a plain folder with no .git must be accepted.
    /// </summary>
    [Fact]
    public void SetRootPath_RejectsUncRelativeMissingPaths_ButAllowsNonGitFolder()
    {
        var (_, registry) = Build(new() { ["TaskRepository"] = Path.Combine(_tempDir, "workspace") });
        var rec = registry.EnsureProjectForStorage(
            Path.Combine(_tempDir, "workspace", "projects", "p"), "P", "ws-1");

        Assert.Throws<ArgumentException>(() => registry.SetRootPath(rec.Id, @"\\evil\share"));
        Assert.Throws<ArgumentException>(() => registry.SetRootPath(rec.Id, @"relative\path"));
        Assert.Throws<ArgumentException>(() => registry.SetRootPath(rec.Id, Path.Combine(_tempDir, "does-not-exist")));

        var noGit = Path.Combine(_tempDir, "no-git-workdir");
        Directory.CreateDirectory(noGit);
        Assert.Equal(noGit, registry.SetRootPath(rec.Id, noGit).RootPath);
        Assert.Null(registry.SetRootPath(rec.Id, null).RootPath);
    }
}
