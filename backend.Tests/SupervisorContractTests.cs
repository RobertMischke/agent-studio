using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the supervisor contract: advisory and intervention records round-trip
/// through JSON, and the canonical log paths are stable. The supervisor's
/// per-project jsonl files are append-only; future readers (Layer 3 system
/// review monitor, future UI) depend on this shape staying compatible.
/// </summary>
public class SupervisorContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    [Fact]
    public void Advisory_RoundTrips_PreservingAllFields()
    {
        var original = new SupervisorAdvisory(
            CreatedAt: new DateTime(2026, 5, 4, 10, 30, 0, DateTimeKind.Utc),
            Project: "agent-taskboard",
            Severity: SupervisorSeverity.Warn,
            Source: SupervisorSource.HardCheck,
            Topic: "no-progress",
            Message: "No log line in 12 minutes while job is running.",
            JobId: "refactor-job-service");

        var json = JsonSerializer.Serialize(original, Json);
        var decoded = JsonSerializer.Deserialize<SupervisorAdvisory>(json, Json);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Intervention_RoundTrips_WithPauseTtl()
    {
        var original = new SupervisorIntervention(
            CreatedAt: new DateTime(2026, 5, 4, 10, 31, 0, DateTimeKind.Utc),
            Project: "agent-taskboard",
            Kind: SupervisorInterventionKind.PausePickup,
            Source: SupervisorSource.User,
            Reason: "Investigating quota burn",
            JobId: null,
            PauseTtl: TimeSpan.FromMinutes(30));

        var json = JsonSerializer.Serialize(original, Json);
        var decoded = JsonSerializer.Deserialize<SupervisorIntervention>(json, Json);

        Assert.Equal(original, decoded);
        Assert.NotNull(decoded!.PauseTtl);
        Assert.Equal(TimeSpan.FromMinutes(30), decoded.PauseTtl);
    }

    [Fact]
    public void Observation_RoundTrips_WithNullableSubrecords()
    {
        var original = new SupervisorObservation(
            CapturedAt: new DateTime(2026, 5, 4, 10, 32, 0, DateTimeKind.Utc),
            Project: "agent-taskboard",
            RunnerStatus: "running",
            CurrentJobId: "refactor-job-service",
            CurrentRunState: "3-progress",
            LastProgressAt: new DateTime(2026, 5, 4, 10, 31, 50, DateTimeKind.Utc),
            Quota: new SupervisorQuotaWindow("claude", 0.92, new DateTime(2026, 5, 4, 16, 0, 0, DateTimeKind.Utc)),
            RecentDecisions: new[]
            {
                new SupervisorRecentDecision(new DateTime(2026, 5, 4, 10, 30, 0, DateTimeKind.Utc), "reissue", "Re-issued follow-up after fast no-op."),
            },
            RecentAgentSamples: new[] { "Reading file...", "Writing file..." },
            ErrorCounts: new SupervisorErrorCounts(0, 0, 0));

        var json = JsonSerializer.Serialize(original, Json);
        var decoded = JsonSerializer.Deserialize<SupervisorObservation>(json, Json);

        AssertObservationEqual(original, decoded!);
    }

    [Fact]
    public void Observation_RoundTrips_WhenIdleProjectHasNoCurrentJob()
    {
        var original = new SupervisorObservation(
            CapturedAt: DateTime.UtcNow,
            Project: "idle-project",
            RunnerStatus: "idle",
            CurrentJobId: null,
            CurrentRunState: null,
            LastProgressAt: null,
            Quota: null,
            RecentDecisions: Array.Empty<SupervisorRecentDecision>(),
            RecentAgentSamples: Array.Empty<string>(),
            ErrorCounts: new SupervisorErrorCounts(0, 0, 0));

        var json = JsonSerializer.Serialize(original, Json);
        var decoded = JsonSerializer.Deserialize<SupervisorObservation>(json, Json);

        AssertObservationEqual(original, decoded!);
    }

    /// <summary>
    /// Records compare reference-equal on their collection members. After a
    /// JSON round-trip the collections are fresh instances, so direct
    /// <see cref="Assert.Equal{T}(T, T)"/> on the record fails even though
    /// the contract is preserved. Compare field-by-field instead.
    /// </summary>
    private static void AssertObservationEqual(SupervisorObservation a, SupervisorObservation b)
    {
        Assert.Equal(a.CapturedAt, b.CapturedAt);
        Assert.Equal(a.Project, b.Project);
        Assert.Equal(a.RunnerStatus, b.RunnerStatus);
        Assert.Equal(a.CurrentJobId, b.CurrentJobId);
        Assert.Equal(a.CurrentRunState, b.CurrentRunState);
        Assert.Equal(a.LastProgressAt, b.LastProgressAt);
        Assert.Equal(a.Quota, b.Quota);
        Assert.Equal(a.ErrorCounts, b.ErrorCounts);
        Assert.Equal(a.RecentDecisions, b.RecentDecisions);
        Assert.Equal(a.RecentAgentSamples, b.RecentAgentSamples);
    }

    [Fact]
    public void LogPaths_AreCanonical_ForProject()
    {
        var workspace = Path.Combine("C:", "ws");
        var slug = "agent-taskboard";

        Assert.Equal(Path.Combine(workspace, "logs", "meta", slug), SupervisorLogPaths.ProjectLogDir(workspace, slug));
        Assert.Equal(Path.Combine(workspace, "logs", "meta", slug, "observations.jsonl"), SupervisorLogPaths.ObservationsFile(workspace, slug));
        Assert.Equal(Path.Combine(workspace, "logs", "meta", slug, "interventions.jsonl"), SupervisorLogPaths.InterventionsFile(workspace, slug));
        Assert.Equal(Path.Combine(workspace, "logs", "meta", slug, "reasoning.md"), SupervisorLogPaths.ReasoningFile(workspace, slug));
        Assert.Equal(Path.Combine(workspace, "logs", "meta", slug, "heartbeat.json"), SupervisorLogPaths.HeartbeatFile(workspace, slug));
    }

    [Fact]
    public void LogPaths_SystemReview_IsWorkspaceWide()
    {
        var workspace = Path.Combine("C:", "ws");
        Assert.Equal(Path.Combine(workspace, "logs", "system-review"), SupervisorLogPaths.SystemReviewDir(workspace));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void LogPaths_RejectEmptyWorkspace(string workspace)
    {
        Assert.Throws<ArgumentException>(() => SupervisorLogPaths.SystemReviewDir(workspace));
        Assert.Throws<ArgumentException>(() => SupervisorLogPaths.ProjectLogDir(workspace, "slug"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void LogPaths_RejectEmptyProjectSlug(string slug)
    {
        Assert.Throws<ArgumentException>(() => SupervisorLogPaths.ProjectLogDir("C:/ws", slug));
    }
}
