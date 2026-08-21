using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the AGT-2673 read-only surfacing contract: <see cref="SessionRegistry.BuildReport"/>
/// attaches the most recent <see cref="NpmShimRepairLog"/> entry onto the Claude
/// <see cref="CliUsageSection"/> so the CLI paths panel is not silent about a repair that
/// already ran, without ever triggering a repair itself.
/// </summary>
public sealed class SessionRegistryNpmShimRepairNoteTests : IDisposable
{
    private readonly string _workspaceRoot;

    public SessionRegistryNpmShimRepairNoteTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-session-registry-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void BuildReport_Claude_AttachesLastRepairNote_WhenJournalHasEntry()
    {
        var outcome = new HealOutcome(true, new[] { "npm install -g completed" }, null)
        {
            Diagnosis = NpmShimHealDiagnosis.ShimMissingPackagePresent,
            VersionBefore = "2.1.231",
            VersionAfter = "2.1.234",
            NpmInstallAttempted = true,
        };
        var at = new DateTime(2026, 8, 18, 10, 5, 0, DateTimeKind.Utc);
        NpmShimRepairLog.Append(_workspaceRoot, "claude", outcome, at, NullLogger.Instance);

        var report = BuildReport();

        var claude = Assert.Single(report.Sections, s => s.CliType == "claude");
        Assert.Equal(at, claude.LastRepairAt);
        Assert.Equal(NpmShimHealDiagnosis.ShimMissingPackagePresent, claude.LastRepairDiagnosis);
        Assert.True(claude.LastRepairSucceeded);
    }

    [Fact]
    public void BuildReport_Claude_NoRepairNote_WhenNoJournalEntryExists()
    {
        var report = BuildReport();

        var claude = Assert.Single(report.Sections, s => s.CliType == "claude");
        Assert.Null(claude.LastRepairAt);
        Assert.Null(claude.LastRepairDiagnosis);
        Assert.Null(claude.LastRepairSucceeded);
    }

    [Fact]
    public void BuildReport_Claude_FailedRepair_SurfacesAsUnsuccessful_NotDropped()
    {
        // Requirement 2 (AGT-2673): a failed repair must never look like a
        // silent success - LastRepairSucceeded must read false, not just be
        // absent, so the panel can render it as an alarm.
        var outcome = new HealOutcome(false, Array.Empty<string>(), "still missing after npm install -g")
        {
            Diagnosis = NpmShimHealDiagnosis.ShimMissingPackagePresent,
            NpmInstallAttempted = true,
        };
        var at = new DateTime(2026, 8, 18, 11, 0, 0, DateTimeKind.Utc);
        NpmShimRepairLog.Append(_workspaceRoot, "claude", outcome, at, NullLogger.Instance);

        var report = BuildReport();

        var claude = Assert.Single(report.Sections, s => s.CliType == "claude");
        Assert.Equal(at, claude.LastRepairAt);
        Assert.False(claude.LastRepairSucceeded);
    }

    private CliUsageReport BuildReport()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspaceRoot })
            .Build();
        var registry = new SessionRegistry(NullLogger<SessionRegistry>.Instance, scanner: null!, sessionIndex: null, configuration: config);
        var router = new CliRouter(new FakeClaudeCli());
        return registry.BuildReport(router);
    }

    /// <summary>Minimal stub: only <see cref="CliType"/> and <see cref="TestCliPath"/> are
    /// exercised by <c>SessionRegistry.BuildSection</c>; everything else throws loudly.</summary>
    private sealed class FakeClaudeCli : ICliExecutionService
    {
        public string CliType => CliTypes.Claude;
        public (bool Available, string? Version, string Path) TestCliPath(string? path = null)
            => (true, "2.1.234 (Claude Code)", "/fake/claude");

        public string GetCliPath() => throw new NotImplementedException();
        public bool IsAvailable() => throw new NotImplementedException();
        public CliExecution? GetExecution(string jobKey) => null;
        public Task<(CliExecution? Execution, string? Error)> StartAsync(string jobId, string jobKey, string prompt, string workingDirectory, string? sessionName = null, bool resumeSession = false, string? model = null, string? thinkingLevel = null, string? jobFolderPath = null, string? permissionMode = null, string? contextMode = null, string? executionEngine = null, CancellationToken ct = default) => throw new NotImplementedException();
        public bool Stop(string jobKey, RunStopReason reason = RunStopReason.UserStop) => throw new NotImplementedException();
        public bool SendInput(string jobKey, string input) => throw new NotImplementedException();
        public List<CliOutputLine> GetOutput(string jobKey) => throw new NotImplementedException();
        public void DiscardPersistedOutput(string jobKey) => throw new NotImplementedException();
        public void ReleaseOutputResources(string jobKey) { }
        public SessionUsage? GetLastUsage(string jobKey) => throw new NotImplementedException();
        public bool IsRunningForProject(string rootPath) => throw new NotImplementedException();
        public DateTime? GetLastStreamedAt(string jobKey) => throw new NotImplementedException();
        public WatchdogState GetWatchdogState(string jobKey) => throw new NotImplementedException();
        public void SetWatchdogState(string jobKey, WatchdogState state) => throw new NotImplementedException();
        public void ReattachOnStartup() { }
        public Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default) => throw new NotImplementedException();
        public bool IsCompatibleSessionName(string? sessionName) => throw new NotImplementedException();

        public event Action<string, CliOutputLine>? OnOutput;
        public event Action<string, CliExecution>? OnStarted;
        public event Action<string, CliExecution>? OnFinished;
        public event Action<string, CliRunEvent>? OnRunEvent;
    }
}
