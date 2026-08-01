using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class RuntimeCapacitySettingsTests
{
    [Fact]
    public async Task First_registration_bootstraps_capacity_and_later_runners_inherit_it()
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
        Assert.Equal(7, secondSnapshot.EffectiveMaxParallelism);
        Assert.NotNull(secondSnapshot.RuntimeCapacityAppliedAt);
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
