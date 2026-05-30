using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// F35: per-lane sort strategy applied to the kanban grouped endpoint.
/// Covers strategy resolution (defaults, overrides, invalid values),
/// each comparator's order, key-aware semantic ordering for newest/oldest,
/// the runner-pickup strategy, and that ProjectSettingsService persists
/// overrides across reload.
/// </summary>
public sealed class LaneSortStrategyTests : IDisposable
{
    private readonly string _workspace;

    public LaneSortStrategyTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-lane-sort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData(TaskStates.Backlog, LaneSortStrategies.NewestFirst)]
    [InlineData(TaskStates.Preparation, LaneSortStrategies.NewestFirst)]
    [InlineData(TaskStates.Ready, LaneSortStrategies.NewestFirst)]
    [InlineData(TaskStates.Progress, LaneSortStrategies.LastActivity)]
    [InlineData(TaskStates.AutoReview, LaneSortStrategies.LastActivity)]
    [InlineData(TaskStates.HumanReview, LaneSortStrategies.OldestFirst)]
    [InlineData(TaskStates.Completed, LaneSortStrategies.LastActivity)]
    public void DefaultStrategyMatchesPromptTable(string lane, string expected)
    {
        Assert.Equal(expected, LaneSortStrategies.GetDefaultForLane(lane));
    }

    [Fact]
    public void Resolve_UsesOverrideWhenSet()
    {
        var settings = new ProjectSettings
        {
            LaneSortStrategyOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TaskStates.Ready] = LaneSortStrategies.Manual,
            },
        };
        Assert.Equal(LaneSortStrategies.Manual,
            LaneSortStrategies.Resolve(settings, TaskStates.Ready));
    }

    [Fact]
    public void Resolve_FallsBackToDefaultForUnknownOverride()
    {
        var settings = new ProjectSettings
        {
            LaneSortStrategyOverrides = new Dictionary<string, string>
            {
                [TaskStates.Ready] = "bogus-strategy",
            },
        };
        Assert.Equal(LaneSortStrategies.NewestFirst,
            LaneSortStrategies.Resolve(settings, TaskStates.Ready));
    }

    [Fact]
    public void NewestFirstComparator_OrdersByKeyDescThenCreatedAtDesc()
    {
        var jobs = new[]
        {
            J("a", key: "ATP-3", createdAt: T(1)),
            J("b", key: "ATP-10", createdAt: T(2)),
            J("c", key: "ATP-2", createdAt: T(3)),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.NewestFirst);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        // ATP-10 has the largest numeric suffix, so it sits on top.
        Assert.Equal(new[] { "b", "a", "c" }, sorted);
    }

    [Fact]
    public void OldestFirstComparator_OrdersByKeyAsc()
    {
        var jobs = new[]
        {
            J("a", key: "ATP-3", createdAt: T(1)),
            J("b", key: "ATP-10", createdAt: T(2)),
            J("c", key: "ATP-2", createdAt: T(3)),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.OldestFirst);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal(new[] { "c", "a", "b" }, sorted);
    }

    [Fact]
    public void LastActivityComparator_OrdersByLastActivityDesc()
    {
        var jobs = new[]
        {
            J("a", lastActivity: T(1), order: 99),
            J("b", lastActivity: T(3), order: 1),
            J("c", lastActivity: T(2), order: 50),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.LastActivity);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal(new[] { "b", "c", "a" }, sorted);
    }

    [Fact]
    public void PickupPriorityComparator_OrdersByOrderAscThenLastActivityAsc()
    {
        var jobs = new[]
        {
            J("a", order: 5, lastActivity: T(2)),
            J("b", order: 1, lastActivity: T(3)),
            J("c", order: 1, lastActivity: T(1)),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.PickupPriority);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal(new[] { "c", "b", "a" }, sorted);
    }

    [Fact]
    public void ManualComparator_OrdersByOrderAscThenKeyDescAsTiebreaker()
    {
        var jobs = new[]
        {
            J("a", order: 2, key: "ATP-1"),
            J("b", order: 1, key: "ATP-7"),
            J("c", order: 1, key: "ATP-3"),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.Manual);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal(new[] { "b", "c", "a" }, sorted);
    }

    [Fact]
    public void NullKey_PushedToBottomInKeyBasedStrategies()
    {
        var jobs = new[]
        {
            J("a", key: null, createdAt: T(5)),
            J("b", key: "ATP-1"),
            J("c", key: null, createdAt: T(2)),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.NewestFirst);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal("b", sorted[0]);
        Assert.Contains("a", sorted[1..]);
        Assert.Contains("c", sorted[1..]);
    }

    [Fact]
    public void LaneSortApplier_GroupsByProjectAndSortsEachIndependently()
    {
        var jobs = new[]
        {
            J("a", project: "Alpha", key: "ALP-3"),
            J("b", project: "Bravo", key: "BRA-1", order: 5),
            J("c", project: "Alpha", key: "ALP-7"),
            J("d", project: "Bravo", key: "BRA-9", order: 1),
        };
        ProjectSettings Resolver(string projectName)
        {
            // Alpha uses default newest-first; Bravo overrides to manual.
            if (string.Equals(projectName, "Bravo", StringComparison.OrdinalIgnoreCase))
            {
                return new ProjectSettings
                {
                    LaneSortStrategyOverrides = new Dictionary<string, string>
                    {
                        [TaskStates.Ready] = LaneSortStrategies.Manual,
                    },
                };
            }
            return new ProjectSettings();
        }

        var sorted = LaneSortApplier
            .Sort(jobs, TaskStates.Ready, Resolver)
            .Select(j => j.Id)
            .ToArray();

        // Alpha first (alphabetical project order): newest-first → ALP-7 above ALP-3.
        // Then Bravo: manual → BRA-9 (order=1) above BRA-1 (order=5).
        Assert.Equal(new[] { "c", "a", "d", "b" }, sorted);
    }

    [Fact]
    public void ProjectSettingsService_SetLaneSortStrategyPersistsAcrossReload()
    {
        var svc = BuildSettings();
        svc.SetLaneSortStrategy("acme", TaskStates.Ready, LaneSortStrategies.Manual);

        var reloaded = BuildSettings();
        var s = reloaded.Get("acme");

        Assert.NotNull(s.LaneSortStrategyOverrides);
        Assert.Equal(LaneSortStrategies.Manual, s.LaneSortStrategyOverrides![TaskStates.Ready]);
        Assert.Equal(LaneSortStrategies.Manual, LaneSortStrategies.Resolve(s, TaskStates.Ready));
        // Lanes without an override still resolve via defaults.
        Assert.Equal(LaneSortStrategies.NewestFirst, LaneSortStrategies.Resolve(s, TaskStates.Backlog));
    }

    [Fact]
    public void ProjectSettingsService_NullStrategyClearsOverride()
    {
        var svc = BuildSettings();
        svc.SetLaneSortStrategy("acme", TaskStates.Ready, LaneSortStrategies.Manual);
        svc.SetLaneSortStrategy("acme", TaskStates.Ready, null);

        var s = svc.Get("acme");
        Assert.Null(s.LaneSortStrategyOverrides);
        Assert.Equal(LaneSortStrategies.NewestFirst, LaneSortStrategies.Resolve(s, TaskStates.Ready));
    }

    private ProjectSettingsService BuildSettings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
            })
            .Build();
        return new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
    }

    private static DateTime T(int seconds) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);

    private static TaskInfo J(
        string id,
        string? key = null,
        string? project = null,
        int order = 1,
        DateTime? createdAt = null,
        DateTime? lastActivity = null)
    {
        return new TaskInfo
        {
            Id = id,
            TaskKey = (project ?? "TestProject") + "::" + id,
            Key = key,
            Title = id,
            Order = order,
            State = TaskStates.Ready,
            ProjectName = project ?? "TestProject",
            WatchPath = "/tmp/" + (project ?? "TestProject"),
            CreatedAt = createdAt ?? T(0),
            LastActivity = lastActivity ?? createdAt ?? T(0),
        };
    }
}
