using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Services.Drift;
using Xunit;
using Xunit.Abstractions;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the CodePatternDriftAnalysisService contract: rule detection
/// classifies sites as canonical / drift / missing-canonical correctly,
/// and the live repo scan (when run against the dev checkout) reports
/// zero drift on the rules our CLI-OneShot fix just closed.
/// </summary>
public class CodePatternDriftAnalysisServiceTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;

    public CodePatternDriftAnalysisServiceTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "code-pattern-drift-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void Analyze_DetectsBadVariant_AndPassesCanonical()
    {
        // Drift site: ArgumentList.Add("-p") immediately followed by Add(prompt).
        WriteFile("BadSite.cs", """
            using System.Diagnostics;
            public class BadSite {
              public void Run(string prompt, string model) {
                var psi = new ProcessStartInfo { FileName = "claude" };
                psi.ArgumentList.Add("--dangerously-skip-permissions");
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(prompt);
              }
            }
            """);

        // Canonical site: stdin-piped prompt via StandardInput.WriteAsync.
        WriteFile("GoodSite.cs", """
            using System.Diagnostics;
            public class GoodSite {
              public void Run(string prompt) {
                var psi = new ProcessStartInfo { FileName = "claude", RedirectStandardInput = true };
                using var p = Process.Start(psi);
                p.StandardInput.WriteAsync(prompt);
              }
            }
            """);

        var svc = new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance);
        var report = svc.Analyze(_root);

        var cliFinding = report.Findings.Single(f => f.RuleId == "cli-one-shot-stdin");
        Assert.Equal(2, cliFinding.TotalSites);
        Assert.Equal(1, cliFinding.CanonicalSites);
        Assert.Equal(1, cliFinding.DriftSites);
        Assert.Equal(DriftSeverity.High, cliFinding.OverallSeverity);
        Assert.Contains(cliFinding.Hits, h => h.IsDrift && h.FilePath.EndsWith("BadSite.cs"));
        Assert.Contains(cliFinding.Hits, h => !h.IsDrift && h.FilePath.EndsWith("GoodSite.cs"));
    }

    [Fact]
    public void Analyze_ReportsMissingCanonicalAsDrift_WhenOnlyGoodVariantDefined()
    {
        // JSONL append rule: only GoodVariant defined; a file that
        // matches the candidate but lacks SemaphoreSlim is flagged.
        WriteFile("NoLockAppender.cs", """
            using System.IO;
            public class NoLockAppender {
              public void Append(string path) {
                using var s = new FileStream(path, FileMode.Append, FileAccess.Write);
                // FilePath ends with .jsonl
                var p = "/logs/example.jsonl";
              }
            }
            """);

        WriteFile("LockedAppender.cs", """
            using System.IO;
            using System.Threading;
            public class LockedAppender {
              private readonly SemaphoreSlim _lock = new(1, 1);
              public void Append(string path) {
                _lock.Wait();
                using var s = new FileStream(path, FileMode.Append, FileAccess.Write);
                var p = "/logs/example.jsonl";
              }
            }
            """);

        var svc = new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance);
        var report = svc.Analyze(_root);
        var finding = report.Findings.Single(f => f.RuleId == "jsonl-append-locked");

        Assert.Equal(1, finding.CanonicalSites);
        Assert.Equal(1, finding.DriftSites);
        Assert.Contains(finding.Hits, h => h.IsDrift && h.FilePath.EndsWith("NoLockAppender.cs") && h.Evidence == "missing-canonical");
    }

    [Fact]
    public void Analyze_ExcludesTestAndArtifactFolders()
    {
        // Even if a "bad" pattern lives in backend.Tests/, it is excluded.
        Directory.CreateDirectory(Path.Combine(_root, "backend.Tests"));
        File.WriteAllText(Path.Combine(_root, "backend.Tests", "FakeSite.cs"), """
            using System.Diagnostics;
            public class FakeSite {
              public void Run(string prompt) {
                var psi = new ProcessStartInfo { FileName = "claude" };
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(prompt);
              }
            }
            """);

        var svc = new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance);
        var report = svc.Analyze(_root);

        var cliFinding = report.Findings.Single(f => f.RuleId == "cli-one-shot-stdin");
        Assert.Equal(0, cliFinding.TotalSites);
        Assert.Equal(0, cliFinding.DriftSites);
    }

    [Fact]
    public void Analyze_AgainstLiveDevCheckout_ReportsZeroCliDriftAfterFix()
    {
        // Smoke test against the actual dev repo to prove the post-fix
        // state is clean. Locates the repo by walking up from the test
        // binary location until AGENTS.md is found. Skipped if the marker
        // is not reachable (e.g. CI builds the test out-of-tree).
        var repo = LocateRepoRoot();
        if (repo is null)
        {
            _out.WriteLine("No AGENTS.md upstream; skipping live-repo smoke test");
            return;
        }

        var svc = new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance);
        var report = svc.Analyze(repo);
        var cli = report.Findings.Single(f => f.RuleId == "cli-one-shot-stdin");

        _out.WriteLine($"live-cli-one-shot-stdin: total={cli.TotalSites} canonical={cli.CanonicalSites} drift={cli.DriftSites}");
        foreach (var drift in cli.Hits.Where(h => h.IsDrift))
        {
            _out.WriteLine($"  drift: {drift.FilePath}:{drift.LineNumber} ({drift.Evidence})");
        }
        Assert.Equal(0, cli.DriftSites);
    }

    private void WriteFile(string relativePath, string contents)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
