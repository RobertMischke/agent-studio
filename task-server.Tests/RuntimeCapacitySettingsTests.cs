using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class RuntimeCapacitySettingsTests
{
    [Fact]
    public async Task First_registration_bootstraps_capacity_without_claiming_later_runners_adopted_it()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();

        var first = await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 7),
            "test",
            default);
        var second = await store.RegisterRunnerAsync(
            "runner-b",
            Runner("runner-b:1", bootstrap: 2),
            "test",
            default);

        Assert.Equal(7, first.RuntimeCapacity!.MaxParallelism);
        Assert.Equal(7, second.RuntimeCapacity!.MaxParallelism);
        Assert.Equal("balanced", second.RuntimeCapacity.RampStrategy);
        Assert.Equal(80, second.RuntimeCapacity.TargetLoadPercent);
        var secondSnapshot = (await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "runner-b");
        Assert.Equal(2, secondSnapshot.EffectiveMaxParallelism);
        Assert.NotNull(secondSnapshot.RuntimeCapacityAppliedAt);
        Assert.Null(secondSnapshot.RuntimeCapacityAppliedVersion);
    }

    [Fact]
    public async Task Review_registration_does_not_seed_the_coding_capacity()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();

        var review = await store.RegisterRunnerAsync(
            "review-a",
            new RegisterRunnerRequest(
                "review",
                "host-a",
                "review-a:1",
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.ReviewExecutor],
                BootstrapMaxParallelism: 9),
            "test",
            default);
        var coding = await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 3),
            "test",
            default);

        Assert.Null(review.RuntimeCapacity);
        Assert.Equal(3, coding.RuntimeCapacity!.MaxParallelism);
        var reviewSnapshot = (await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "review-a");
        Assert.Null(reviewSnapshot.EffectiveMaxParallelism);
        Assert.Null(reviewSnapshot.RuntimeCapacityAppliedAt);
    }

    [Fact]
    public async Task Update_is_versioned_and_exposed_through_the_runtime_service()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 4),
            "test",
            default);
        var service = new RuntimeCapacitySettingsService(store);
        var current = await service.GetAsync("host-a");

        var updated = await service.UpdateAsync(
            "host-a",
            new UpdateRuntimeCapacitySettingsRequest(
                6,
                85,
                "aggressive",
                current!.Version),
            "operator",
            default);

        Assert.Equal(6, updated.MaxParallelism);
        Assert.Equal(85, updated.TargetLoadPercent);
        Assert.Equal("aggressive", updated.RampStrategy);
        Assert.Equal(current.Version + 1, updated.Version);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => service.UpdateAsync(
            "host-a",
            new UpdateRuntimeCapacitySettingsRequest(8, 80, "balanced", current.Version),
            "operator",
            default));
    }

    [Fact]
    public async Task Runner_acknowledgement_is_bound_to_the_exact_version_and_audited_once()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 4),
            "runner-a",
            default);
        var current = await store.GetRuntimeCapacitySettingsAsync("host-a", default);
        var updated = await store.UpdateRuntimeCapacitySettingsAsync(
            "host-a",
            new UpdateRuntimeCapacitySettingsRequest(
                6,
                current!.TargetLoadPercent,
                current.RampStrategy,
                current.Version),
            "operator",
            default);
        var appliedAt = DateTime.UtcNow.AddSeconds(-1);

        await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "runner-a:1",
                EffectiveMaxParallelism: updated.MaxParallelism,
                EffectiveRuntimeCapacityVersion: updated.Version,
                EffectiveRuntimeCapacityAppliedAt: appliedAt),
            "runner-a",
            default);
        await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "runner-a:1",
                EffectiveMaxParallelism: updated.MaxParallelism,
                EffectiveRuntimeCapacityVersion: updated.Version,
                EffectiveRuntimeCapacityAppliedAt: appliedAt),
            "runner-a",
            default);

        var snapshot = (await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "runner-a");
        Assert.Equal(updated.MaxParallelism, snapshot.EffectiveMaxParallelism);
        Assert.Equal(updated.Version, snapshot.RuntimeCapacityAppliedVersion);
        Assert.Equal(appliedAt, snapshot.RuntimeCapacityAppliedAt!.Value, TimeSpan.FromMilliseconds(1));
        var audit = await store.ListAuditAsync(0, default);
        var applied = Assert.Single(audit, item => item.Action == "runtime-capacity.applied");
        Assert.Equal("runner-a", applied.ActorId);
        Assert.Equal("runner-a", applied.TargetId);
        Assert.Contains($"\"settingsVersion\":{updated.Version}", applied.DetailJson);
        Assert.True(applied.OccurredAt >= updated.UpdatedAt);
    }

    [Fact]
    public async Task Stale_acknowledgement_remains_visible_but_is_not_recorded_as_applied()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 4),
            "runner-a",
            default);
        var current = await store.GetRuntimeCapacitySettingsAsync("host-a", default);
        var updated = await store.UpdateRuntimeCapacitySettingsAsync(
            "host-a",
            new UpdateRuntimeCapacitySettingsRequest(
                6,
                current!.TargetLoadPercent,
                current.RampStrategy,
                current.Version),
            "operator",
            default);

        await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "runner-a:1",
                EffectiveMaxParallelism: current.MaxParallelism,
                EffectiveRuntimeCapacityVersion: current.Version),
            "runner-a",
            default);

        var snapshot = (await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "runner-a");
        Assert.Equal(current.MaxParallelism, snapshot.EffectiveMaxParallelism);
        Assert.Null(snapshot.RuntimeCapacityAppliedVersion);
        Assert.DoesNotContain(
            await store.ListAuditAsync(0, default),
            item => item.Action == "runtime-capacity.applied"
                    && item.DetailJson.Contains(
                        $"\"settingsVersion\":{updated.Version}",
                        StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replacement_process_acknowledges_the_cached_version_once_for_its_new_instance()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 4),
            "runner-a",
            default);
        var current = await store.GetRuntimeCapacitySettingsAsync("host-a", default);
        await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "runner-a:1",
                EffectiveMaxParallelism: current!.MaxParallelism,
                EffectiveRuntimeCapacityVersion: current.Version),
            "runner-a",
            default);

        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:2", bootstrap: 2) with
            {
                EffectiveMaxParallelism = current.MaxParallelism,
                EffectiveRuntimeCapacityVersion = current.Version,
                EffectiveRuntimeCapacityAppliedAt = DateTime.UtcNow,
            },
            "runner-a",
            default);

        var applied = (await store.ListAuditAsync(0, default))
            .Where(item => item.Action == "runtime-capacity.applied")
            .ToArray();
        Assert.Equal(2, applied.Length);
        Assert.All(applied, item => Assert.Equal("runner-a", item.TargetId));
    }

    [Fact]
    public async Task Central_capacity_limits_claims_across_runners_and_projects_on_one_host()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"),
            "test",
            default);
        foreach (var (name, prefix) in new[] { ("One", "ONE"), ("Two", "TWO") })
        {
            var project = await store.CreateProjectAsync(
                new CreateProjectRequest(workspace.WorkspaceId, name, prefix),
                "test",
                default);
            await store.CreateTaskAsync(
                project.ProjectId,
                new CreateTaskRequest($"{name} task", "work", "2-ready"),
                "test",
                default);
        }
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1", bootstrap: 1),
            "test",
            default);
        await store.RegisterRunnerAsync(
            "runner-b",
            Runner("runner-b:1", bootstrap: 9),
            "test",
            default);

        var first = await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "runner-a:1",
                EffectiveMaxParallelism: 1),
            "runner-a",
            default);
        var blocked = await store.ClaimAsync(
            new ClaimRequest(
                "runner-b",
                "runner-b:1",
                EffectiveMaxParallelism: 1),
            "runner-b",
            default);

        Assert.Equal("claimed", first.Status);
        Assert.Equal("empty", blocked.Status);
        Assert.Contains("capacity is full (1/1)", blocked.Message);
        Assert.Equal(1, blocked.RuntimeCapacity!.MaxParallelism);

        var capacity = await store.GetRuntimeCapacitySettingsAsync("host-a", default);
        await store.UpdateRuntimeCapacitySettingsAsync(
            "host-a",
            new UpdateRuntimeCapacitySettingsRequest(
                2,
                capacity!.TargetLoadPercent,
                capacity.RampStrategy,
                capacity.Version),
            "operator",
            default);
        var second = await store.ClaimAsync(
            new ClaimRequest(
                "runner-b",
                "runner-b:1",
                EffectiveMaxParallelism: 2),
            "runner-b",
            default);

        Assert.Equal("claimed", second.Status);
        Assert.NotEqual(first.Task!.ProjectId, second.Task!.ProjectId);
        Assert.Equal(2, second.RuntimeCapacity!.MaxParallelism);
        var snapshot = (await store.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "runner-b");
        Assert.Equal(2, snapshot.EffectiveMaxParallelism);
        Assert.NotNull(snapshot.RuntimeCapacityAppliedAt);
    }

    private static TaskServerStore Store(string dataDirectory)
        => new(
            Options.Create(new TaskServerOptions { DataDirectory = dataDirectory }),
            TimeProvider.System);

    private static RegisterRunnerRequest Runner(string instance, int bootstrap)
        => new(
            "runner",
            "host-a",
            instance,
            "1.0.0",
            TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor],
            BootstrapMaxParallelism: bootstrap);
}
