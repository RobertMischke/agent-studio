using OrchestratorApi.Models;
using OrchestratorApi.Services.Companion;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the snapshot fold the companion HostedService pushes to the relay.
/// The builder is a pure function over (jobs, runner status, quota report,
/// token aggregate, host) so the matrix stays simple.
/// </summary>
public class CompanionSnapshotBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 19, 0, 0, TimeSpan.Zero);
    private static readonly CompanionHost Host = new() { Name = "test-host", Version = "1.0.0", IsDev = true };

    [Fact]
    public void EmptyInputs_ProducesEmptyPayloadWithoutThrowing()
    {
        var snap = CompanionSnapshotBuilder.Build(
            jobs: Array.Empty<JobInfo>(),
            runner: new RunnerStatus(),
            quota: null,
            tokenAggregate: new CompanionTokens(),
            host: Host,
            now: Now);

        Assert.Equal(Now, snap.SnapshotAt);
        Assert.Empty(snap.Payload.Projects);
        Assert.Empty(snap.Payload.Quota);
        Assert.Equal(0, snap.Payload.Tokens.TotalCalls);
        Assert.Equal("test-host", snap.Host.Name);
        Assert.True(snap.Host.IsDev);
    }

    [Fact]
    public void GroupsJobsByProjectAndRoutesToCorrectPipelineLane()
    {
        var jobs = new List<JobInfo>
        {
            Job("a", "alpha", "C:/alpha", JobStates.Ready, order: 2),
            Job("b", "alpha", "C:/alpha", JobStates.Ready, order: 1),
            Job("c", "alpha", "C:/alpha", JobStates.Progress),
            Job("d", "alpha", "C:/alpha", JobStates.AutoReview),
            Job("e", "alpha", "C:/alpha", JobStates.Preparation), // not in any lane
            Job("f", "beta",  "C:/beta",  JobStates.Ready),
        };
        var runner = new RunnerStatus
        {
            Projects = new()
            {
                ["alpha"] = new ProjectRunnerStatus { ProjectName = "alpha", Mode = "auto", ActiveJobId = "c" },
            },
        };

        var snap = CompanionSnapshotBuilder.Build(jobs, runner, null, new CompanionTokens(), Host, Now);

        Assert.Equal(2, snap.Payload.Projects.Count);
        var alpha = snap.Payload.Projects.Single(p => p.Name == "alpha");
        Assert.Equal("auto", alpha.Runner.Mode);
        Assert.Equal("c", alpha.Runner.ActiveJobId);
        Assert.Equal(new[] { "b", "a" }, alpha.Pipeline.Ready.Select(c => c.Id).ToArray());
        Assert.Equal("c", Assert.Single(alpha.Pipeline.Progress).Id);
        Assert.Equal("d", Assert.Single(alpha.Pipeline.Review).Id);

        var beta = snap.Payload.Projects.Single(p => p.Name == "beta");
        Assert.Equal("manual", beta.Runner.Mode); // default when no entry
        Assert.Null(beta.Runner.ActiveJobId);
        Assert.Equal("f", Assert.Single(beta.Pipeline.Ready).Id);
    }

    [Fact]
    public void QuotaWindows_FlattenAcrossClis_AndCarryErrorWhenWindowsAreEmpty()
    {
        var quota = new QuotaReport
        {
            Snapshots = new List<QuotaSnapshot>
            {
                new()
                {
                    CliType = "claude",
                    Plan = "Pro",
                    Windows = new List<QuotaWindow>
                    {
                        new() { Label = "five-hour", UsedPct = 35, ResetAt = new DateTime(2026, 5, 4, 22, 0, 0, DateTimeKind.Utc) },
                        new() { Label = "weekly",    UsedPct = 70 },
                    },
                },
                new() { CliType = "codex", Error = "auth failed" },
            },
        };

        var snap = CompanionSnapshotBuilder.Build(
            Array.Empty<JobInfo>(), new RunnerStatus(), quota, new CompanionTokens(), Host, Now);

        Assert.Equal(3, snap.Payload.Quota.Count);
        var fiveHour = snap.Payload.Quota.Single(q => q.Cli == "claude" && q.Window == "five-hour");
        Assert.Equal(35, fiveHour.UsedPct);
        Assert.NotNull(fiveHour.ResetsAt);
        var codex = snap.Payload.Quota.Single(q => q.Cli == "codex");
        Assert.Equal("auth failed", codex.Error);
        Assert.Equal("", codex.Window);
    }

    [Fact]
    public void TokenAggregate_FlowsThroughUntouched()
    {
        var tokens = new CompanionTokens
        {
            TotalCalls = 12, InputTokens = 100, OutputTokens = 50,
            CacheReadTokens = 25, CacheCreateTokens = 10,
        };
        var snap = CompanionSnapshotBuilder.Build(
            Array.Empty<JobInfo>(), new RunnerStatus(), null, tokens, Host, Now);

        Assert.Equal(tokens, snap.Payload.Tokens);
    }

    [Fact]
    public void DispatcherSlugFromTitle_NormalisesNonAlphanumerics()
    {
        Assert.Equal("hello-world", CompanionCommandDispatcher.SlugFromTitle("  Hello, World!  "));
        Assert.Equal("a-b-c", CompanionCommandDispatcher.SlugFromTitle("a   b   c"));
        Assert.StartsWith("task-", CompanionCommandDispatcher.SlugFromTitle("???"));
    }

    private static JobInfo Job(string id, string project, string watchPath, string state, int order = 1) => new()
    {
        Id = id,
        Title = id.ToUpperInvariant(),
        ProjectName = project,
        WatchPath = watchPath,
        State = state,
        Order = order,
        Agent = "claude",
        Model = "claude-opus-4-7",
        CreatedAt = Now.UtcDateTime,
    };
}
