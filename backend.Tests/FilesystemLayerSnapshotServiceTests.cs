using Microsoft.Extensions.Logging.Abstractions;

using System.Diagnostics;
using Xunit;

namespace AgentStudio.Tests;

// Builds real git repos and walks folder metrics on disk; under gate load the
// commit-bound snapshot timing flakes (night-flake tail, same pattern as the
// other MachineBound heavyweights).
[Trait("Category", "MachineBound")]
public class FilesystemLayerSnapshotServiceTests : IDisposable
{
    private readonly string _tempDir;

    public FilesystemLayerSnapshotServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "filesystem-layer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void GetSnapshot_BuildsFolderMetricsAndPersistsCommitBoundSnapshot()
    {
        var repo = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "backend", "Services", "Runner"));
        Directory.CreateDirectory(Path.Combine(repo, "backend.Tests"));
        Directory.CreateDirectory(Path.Combine(repo, "coverage"));
        Directory.CreateDirectory(Path.Combine(repo, "docs", "assets", "images"));
        Directory.CreateDirectory(Path.Combine(repo, "prompts", "runtime"));

        File.WriteAllText(Path.Combine(repo, "backend", "Services", "Runner", "ProjectRunner.cs"),
            "line1\nline2\nline3\n");
        File.WriteAllText(Path.Combine(repo, "backend.Tests", "ProjectRunnerTests.cs"),
            "test1\ntest2\n");
        File.WriteAllText(Path.Combine(repo, "docs", "assets", "images", "board.png"), "not really an image");
        File.WriteAllText(Path.Combine(repo, "AGENTS.md"), "agent rules\n");
        File.WriteAllText(Path.Combine(repo, "GEMINI.md"), "gemini compatibility shim\n");
        File.WriteAllText(Path.Combine(repo, "prompts", "runtime", "runner-fresh-start.md"), "prompt\n");
        File.WriteAllText(Path.Combine(repo, "coverage", "coverage.cobertura.xml"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage>
              <packages>
                <package name="backend">
                  <classes>
                    <class name="ProjectRunner" filename="backend/Services/Runner/ProjectRunner.cs">
                      <lines>
                        <line number="1" hits="1" />
                        <line number="2" hits="1" />
                        <line number="3" hits="0" />
                      </lines>
                    </class>
                    <class name="ProjectRunnerTests" filename="backend.Tests/ProjectRunnerTests.cs">
                      <lines>
                        <line number="1" hits="1" />
                        <line number="2" hits="0" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);

        RunGit(repo, "init", "-q", "-b", "main");
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "test");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", "seed");

        var service = new FilesystemLayerSnapshotService(NullLogger<FilesystemLayerSnapshotService>.Instance);

        var snapshot = service.GetSnapshot(repo);

        Assert.False(snapshot.FromCache);
        Assert.True(File.Exists(snapshot.SnapshotPath));
        Assert.NotNull(snapshot.GitCommit);

        var root = Assert.Single(snapshot.Rows, r => r.Path == ".");
        Assert.Equal(5, root.CodeLoc);
        Assert.Equal(2, root.CodeFiles);
        Assert.Equal(1, root.VisualEvidenceCount);
        Assert.Equal(3, root.AgentFileCount);
        Assert.Equal(2, root.TestLoc);
        Assert.Equal(1, root.TestFiles);
        Assert.Equal(60, root.CoveragePercent);
        Assert.Equal(3, root.CoveredLines);
        Assert.Equal(5, root.CoverableLines);
        Assert.Equal("coverage/coverage.cobertura.xml", root.CoverageSource);
        Assert.Contains(snapshot.Rows, r => r.Path == "docs/assets/images");
        Assert.Contains(snapshot.Rows, r => r.Path == "prompts/runtime");
        var coverageReport = Assert.Single(snapshot.CoverageReports);
        Assert.Equal("cobertura", coverageReport.Format);
        Assert.Equal(5, coverageReport.CoverableLines);

        var runner = Assert.Single(snapshot.Rows, r => r.Path == "backend/Services/Runner");
        Assert.Equal("Project Runner and orchestration", runner.Role);
        Assert.Equal(3, runner.CodeLoc);
        Assert.Equal(67, runner.CoveragePercent);

        var cached = service.GetSnapshot(repo);
        Assert.True(cached.FromCache);
        Assert.Equal(snapshot.GitCommit, cached.GitCommit);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }
}
