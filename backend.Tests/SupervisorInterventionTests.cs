using System.Text.Json;
using OrchestratorApi.Services.Supervisor;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the persistence side of <see cref="SupervisorInterventionService"/>.
/// The runner-side side effects (StopJob, SetMode) flow through the existing
/// TaskRunnerService so the runner stays the single state-machine authority;
/// those paths are covered by their existing tests. What's load-bearing here
/// is that every supervisor intervention lands in interventions.jsonl with a
/// stable shape so the supervisor panel and Layer 3 review can read history.
/// </summary>
public class SupervisorInterventionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AppendInterventionRecord_WritesJsonlLine_AtCanonicalPath()
    {
        using var temp = new TempDir();
        var iv = new SupervisorIntervention(
            CreatedAt: new DateTime(2026, 5, 4, 10, 0, 0, DateTimeKind.Utc),
            Project: "agent-taskboard",
            Kind: SupervisorInterventionKind.CancelRun,
            Source: SupervisorSource.User,
            Reason: "Quota close to exhausted",
            JobId: "refactor-job-service");

        SupervisorInterventionService.AppendInterventionRecord(temp.Path, iv);

        var path = SupervisorLogPaths.InterventionsFile(temp.Path, iv.Project);
        Assert.True(File.Exists(path));
        var line = File.ReadAllText(path).TrimEnd();
        var decoded = JsonSerializer.Deserialize<SupervisorIntervention>(line, Json);
        Assert.NotNull(decoded);
        Assert.Equal(iv.CreatedAt, decoded!.CreatedAt);
        Assert.Equal(iv.Kind, decoded.Kind);
        Assert.Equal(iv.Reason, decoded.Reason);
        Assert.Equal(iv.JobId, decoded.JobId);
    }

    [Fact]
    public void AppendInterventionRecord_AppendsMultipleLines_OneRecordPerCall()
    {
        using var temp = new TempDir();
        var baseTs = new DateTime(2026, 5, 4, 10, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 3; i++)
        {
            var iv = new SupervisorIntervention(
                CreatedAt: baseTs.AddMinutes(i),
                Project: "agent-taskboard",
                Kind: SupervisorInterventionKind.PausePickup,
                Source: SupervisorSource.HardCheck,
                Reason: $"reason {i}",
                PauseTtl: TimeSpan.FromMinutes(30));
            SupervisorInterventionService.AppendInterventionRecord(temp.Path, iv);
        }

        var path = SupervisorLogPaths.InterventionsFile(temp.Path, "agent-taskboard");
        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        for (int i = 0; i < 3; i++)
        {
            var decoded = JsonSerializer.Deserialize<SupervisorIntervention>(lines[i], Json);
            Assert.NotNull(decoded);
            Assert.Equal($"reason {i}", decoded!.Reason);
            Assert.Equal(TimeSpan.FromMinutes(30), decoded.PauseTtl);
        }
    }

    [Fact]
    public void AppendInterventionRecord_RejectsEmptyWorkspace()
    {
        var iv = new SupervisorIntervention(
            CreatedAt: DateTime.UtcNow,
            Project: "p",
            Kind: SupervisorInterventionKind.Resume,
            Source: SupervisorSource.User,
            Reason: "go");
        Assert.Throws<ArgumentException>(() => SupervisorInterventionService.AppendInterventionRecord("", iv));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "supervisor-tests-" + Guid.NewGuid().ToString("N"));
        public TempDir() { Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
