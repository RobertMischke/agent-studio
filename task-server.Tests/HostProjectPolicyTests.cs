using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class HostProjectPolicyTests
{
    [Fact]
    public async Task Selected_projects_are_versioned_audited_and_enforced_during_claim()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"),
            "test",
            default);
        var blockedProject = await CreateReadyProjectAsync(
            store,
            workspace.WorkspaceId,
            "Blocked",
            "BLK");
        var allowedProject = await CreateReadyProjectAsync(
            store,
            workspace.WorkspaceId,
            "Allowed",
            "ALW");
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1"),
            "test",
            default);
        var service = new HostProjectPolicyService(store);

        var policy = await service.UpdateAsync(
            "host-a",
            new UpdateHostProjectPolicyRequest(
                AllowAllProjects: false,
                AllowedProjectIds: [allowedProject.ProjectId],
                ExpectedVersion: 0),
            "operator",
            default);
        var claim = await store.ClaimAsync(
            new ClaimRequest("runner-a", "runner-a:1"),
            "runner-a",
            default);

        Assert.Equal(1, policy.Version);
        Assert.Equal([allowedProject.ProjectId], policy.AllowedProjectIds);
        Assert.Equal("claimed", claim.Status);
        Assert.Equal(allowedProject.ProjectId, claim.Task!.ProjectId);
        Assert.NotEqual(blockedProject.ProjectId, claim.Task.ProjectId);
        var audit = await store.ListAuditAsync(0, default);
        Assert.Contains(
            audit,
            record => record.Action == "host-project-policy.created"
                      && record.TargetId == "host-a");
        Assert.Contains(
            audit,
            record => record.Action == "run.claimed"
                      && record.DetailJson.Contains(
                          "\"hostProjectPolicyVersion\":1",
                          StringComparison.Ordinal));
        var snapshot = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Equal(policy.Version, snapshot.ProjectPolicy!.Version);
        Assert.Equal(policy.AllowAllProjects, snapshot.ProjectPolicy.AllowAllProjects);
        Assert.Equal(policy.AllowedProjectIds, snapshot.ProjectPolicy.AllowedProjectIds);
    }

    [Fact]
    public async Task Explicit_empty_policy_stops_new_claims_while_missing_policy_allows_compatibility()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"),
            "test",
            default);
        await CreateReadyProjectAsync(store, workspace.WorkspaceId, "Project", "PRJ");
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("runner-a:1"),
            "test",
            default);

        var compatible = await store.ClaimAsync(
            new ClaimRequest("runner-a", "runner-a:1"),
            "runner-a",
            default);
        Assert.Equal("claimed", compatible.Status);
        await CreateReadyProjectAsync(store, workspace.WorkspaceId, "Second", "SEC");

        await store.UpdateHostProjectPolicyAsync(
            "host-a",
            new UpdateHostProjectPolicyRequest(false, [], 0),
            "operator",
            default);
        var blocked = await store.ClaimAsync(
            new ClaimRequest("runner-a", "runner-a:1"),
            "runner-a",
            default);

        Assert.Equal("empty", blocked.Status);
        Assert.Equal("No admissible task is ready.", blocked.Message);
    }

    [Fact]
    public async Task Policy_rejects_unknown_projects_and_stale_versions()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"),
            "test",
            default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project", "PRJ"),
            "test",
            default);

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateHostProjectPolicyAsync(
            "host-a",
            new UpdateHostProjectPolicyRequest(false, ["missing-project"], 0),
            "operator",
            default));
        var created = await store.UpdateHostProjectPolicyAsync(
            "host-a",
            new UpdateHostProjectPolicyRequest(false, [project.ProjectId], 0),
            "operator",
            default);

        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.UpdateHostProjectPolicyAsync(
            "host-a",
            new UpdateHostProjectPolicyRequest(true, [], created.Version - 1),
            "operator",
            default));
    }

    private static async Task<ProjectDto> CreateReadyProjectAsync(
        TaskServerStore store,
        string workspaceId,
        string name,
        string prefix)
    {
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspaceId, name, prefix),
            "test",
            default);
        await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest($"{name} task", "work", "2-ready"),
            "test",
            default);
        return project;
    }

    private static TaskServerStore Store(string dataDirectory)
        => new(
            Options.Create(new TaskServerOptions { DataDirectory = dataDirectory }),
            TimeProvider.System);

    private static RegisterRunnerRequest Runner(string instance)
        => new(
            "runner",
            "host-a",
            instance,
            "1.0.0",
            TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor],
            BootstrapMaxParallelism: 2);
}
