using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace TaskServer.Tests;

public sealed class OrchestratorContextStoreTests
{
    [Theory]
    [InlineData("project", null, false)]
    [InlineData("project", "7-archive", false)]
    [InlineData("task", "6-completed", false)]
    [InlineData("task", "7-archive", true)]
    [InlineData("dossier", "active", false)]
    [InlineData("dossier", "documented", true)]
    [InlineData("dossier", "archived", true)]
    public void Visibility_policy_hides_only_terminal_task_and_Dossier_contexts(
        string kind,
        string? taskState,
        bool expected)
        => Assert.Equal(expected, OrchestratorContextVisibilityPolicy.IsHidden(kind, taskState));

    [Fact]
    public async Task Dossier_context_is_fresh_resumes_independently_and_is_retained_when_documented()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Agent Studio", "AGT"),
            "test",
            default);
        var active = new EnsureDossierOrchestratorContextRequest(
            "AGT-W34", "Context model", "active");

        await store.AppendOrchestratorContextTurnAsync(
            project.ProjectId,
            null,
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                "project_turn", DateTime.UtcNow, "user", "Project history must stay separate.")),
            "test",
            default);

        var fresh = await store.ReadDossierOrchestratorContextAsync(
            project.ProjectId, "context-model", active, 20, "test", default);
        Assert.Equal("dossier:Agent Studio/context-model", fresh.Context.ContextKey);
        Assert.Equal("AGT-W34", fresh.Context.DossierKey);
        Assert.Equal("Context model", fresh.Context.Title);
        Assert.Empty(fresh.Turns);

        await store.AppendDossierOrchestratorContextTurnAsync(
            project.ProjectId,
            "context-model",
            active,
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                "dossier_turn", DateTime.UtcNow, "user", "Continue this Dossier discussion.")),
            "test",
            default);
        var resumed = await store.ReadDossierOrchestratorContextAsync(
            project.ProjectId, "context-model", active, 20, "test", default);
        Assert.Equal("Continue this Dossier discussion.", Assert.Single(resumed.Turns).Body);

        var documented = active with { LifecycleState = "documented" };
        await store.EnsureDossierOrchestratorContextAsync(
            project.ProjectId, "context-model", documented, "test", default);
        Assert.DoesNotContain(
            await store.ListOrchestratorContextsAsync(false, default),
            context => context.Kind == OrchestratorContextKinds.Dossier);
        var retained = Assert.Single(
            await store.ListOrchestratorContextsAsync(true, default),
            context => context.Kind == OrchestratorContextKinds.Dossier);
        Assert.NotNull(retained.HiddenAt);
        Assert.Equal(1, retained.TurnCount);
    }

    [Fact]
    public async Task Project_contexts_are_permanent_and_task_contexts_follow_archive_visibility_without_deletion()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Agent Studio", "AGT"),
            "test",
            default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest("Context foundation", State: "2-ready"),
            "test",
            default);

        var initial = await store.ListOrchestratorContextsAsync(false, default);
        var projectContext = Assert.Single(initial);
        Assert.Equal("project:Agent Studio", projectContext.ContextKey);
        Assert.Null(projectContext.HiddenAt);

        var taskContext = await store.EnsureOrchestratorContextAsync(
            project.ProjectId, task.TaskKey, "test", default);
        Assert.Equal($"task:Agent Studio/{task.TaskKey}", taskContext.ContextKey);
        Assert.Equal("Context foundation", taskContext.Summary);
        Assert.Equal(2, (await store.ListOrchestratorContextsAsync(false, default)).Count);

        var archived = await store.UpdateTaskAsync(
            project.ProjectId,
            task.TaskId,
            new UpdateTaskRequest(null, null, "7-archive", task.Version),
            "test",
            default);
        Assert.NotNull(archived);

        var visible = await store.ListOrchestratorContextsAsync(false, default);
        Assert.Single(visible);
        Assert.Equal("project:Agent Studio", visible[0].ContextKey);
        var all = await store.ListOrchestratorContextsAsync(true, default);
        var retainedTask = Assert.Single(all, item => item.Kind == OrchestratorContextKinds.Task);
        Assert.NotNull(retainedTask.HiddenAt);

        var restored = await store.UpdateTaskAsync(
            project.ProjectId,
            task.TaskId,
            new UpdateTaskRequest(null, null, "6-completed", archived!.Version),
            "test",
            default);
        Assert.NotNull(restored);
        Assert.Equal(2, (await store.ListOrchestratorContextsAsync(false, default)).Count);
    }

    [Fact]
    public async Task Turn_and_source_receipt_are_persisted_with_user_turn_link_and_compact_summary()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Agent Studio", "AGT"),
            "test",
            default);
        var user = new OrchestratorContextTurnDto(
            "usr_1",
            DateTime.UtcNow,
            "user",
            "Explain the foundation context and the receipt persistence contract.");
        await store.AppendOrchestratorContextTurnAsync(
            project.ProjectId,
            null,
            new AppendOrchestratorContextTurnRequest(user),
            "test",
            default);

        var receipt = new OrchestratorContextReceiptDto(
            "rcp_1",
            user.TurnId,
            "project:will-be-canonicalized",
            DateTime.UtcNow,
            new OrchestratorContextBudgetReceiptDto(4000, 6000, 8000, 312),
            [new OrchestratorContextSourceReceiptDto(
                "project:Agent Studio/pulse",
                "project-base",
                "v7",
                new string('a', 64),
                "current",
                1200,
                300,
                "included")]);
        var reply = new OrchestratorContextTurnDto(
            "orch_1",
            DateTime.UtcNow.AddSeconds(1),
            "orchestrator",
            "The central receipt is persisted.",
            "gpt-5.5",
            new OrchestratorContextTokenUsageDto("gpt-5.5", 700, 90, 10, 0),
            Receipt: receipt);
        await store.AppendOrchestratorContextTurnAsync(
            project.ProjectId,
            null,
            new AppendOrchestratorContextTurnRequest(reply),
            "test",
            default);

        var transcript = await store.ReadOrchestratorContextAsync(
            project.ProjectId, null, 20, "test", default);
        Assert.Equal(2, transcript.Turns.Count);
        var persistedReply = Assert.Single(transcript.Turns, item => item.Role == "orchestrator");
        Assert.NotNull(persistedReply.Receipt);
        Assert.Equal(user.TurnId, persistedReply.Receipt!.UserTurnId);
        Assert.Equal("project:Agent Studio", persistedReply.Receipt.ContextKey);
        Assert.Equal(new string('a', 64), Assert.Single(persistedReply.Receipt.Sources).Sha256);
        Assert.DoesNotContain("The central receipt", JsonSerializer.Serialize(persistedReply.Receipt));

        var context = Assert.Single(await store.ListOrchestratorContextsAsync(false, default));
        Assert.Equal(user.Body, context.Summary);
        Assert.Equal(2, context.TurnCount);
        Assert.Equal(700, context.CumulativeInputTokens);
        Assert.Equal(90, context.CumulativeOutputTokens);
    }

    [Fact]
    public async Task Legacy_import_is_idempotent_and_keeps_the_source_outside_the_central_store()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Agent Studio", "AGT"),
            "test",
            default);
        var request = new ImportLegacyOrchestratorChatRequest(
            new string('b', 64),
            [
                new OrchestratorContextTurnDto("legacy_1", DateTime.UtcNow, "user", "Legacy question"),
                new OrchestratorContextTurnDto("legacy_2", DateTime.UtcNow.AddSeconds(1), "orchestrator", "Legacy answer"),
            ]);

        var first = await store.ImportLegacyOrchestratorChatAsync(
            project.ProjectId, request, "migration", default);
        var second = await store.ImportLegacyOrchestratorChatAsync(
            project.ProjectId, request, "migration", default);

        Assert.Equal(2, first.Imported);
        Assert.Equal(0, first.AlreadyPresent);
        Assert.Equal(0, second.Imported);
        Assert.Equal(2, second.AlreadyPresent);
        Assert.Equal(2, (await store.ReadOrchestratorContextAsync(
            project.ProjectId, null, 20, "test", default)).Turns.Count);
    }

    [Fact]
    public async Task Http_contract_round_trips_context_turn_and_receipt_through_the_Task_Server_store()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        var workspaceResponse = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest("Workspace"));
        workspaceResponse.EnsureSuccessStatusCode();
        var workspace = await workspaceResponse.Content.ReadFromJsonAsync<WorkspaceDto>();
        var projectResponse = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest(workspace!.WorkspaceId, "Agent Studio", "AGT"));
        projectResponse.EnsureSuccessStatusCode();
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectDto>();
        var taskResponse = await client.PostAsJsonAsync(
            $"/api/v1/projects/{project!.ProjectId}/tasks",
            new CreateTaskRequest("Open task context", State: "2-ready"));
        taskResponse.EnsureSuccessStatusCode();
        var task = await taskResponse.Content.ReadFromJsonAsync<TaskDto>();

        var openedTaskContext = await client.GetFromJsonAsync<OrchestratorContextTranscriptResponse>(
            $"/api/v1/orchestrator-contexts/projects/Agent%20Studio/tasks/{task!.TaskKey}/turns");
        Assert.Equal($"task:Agent Studio/{task.TaskKey}", openedTaskContext!.Context.ContextKey);
        Assert.Empty(openedTaskContext.Turns);

        var user = new OrchestratorContextTurnDto(
            "http_user", DateTime.UtcNow, "user", "What context was used?");
        var userResponse = await client.PostAsJsonAsync(
            "/api/v1/orchestrator-contexts/projects/Agent%20Studio/turns",
            new AppendOrchestratorContextTurnRequest(user));
        userResponse.EnsureSuccessStatusCode();
        var receipt = new OrchestratorContextReceiptDto(
            "http_receipt",
            user.TurnId,
            "project:Agent Studio",
            DateTime.UtcNow,
            new OrchestratorContextBudgetReceiptDto(4000, 6000, 8000, 10),
            [new OrchestratorContextSourceReceiptDto(
                "project:Agent Studio/state", "project-base", "v1", new string('c', 64),
                "current", 40, 10, "included")]);
        var replyResponse = await client.PostAsJsonAsync(
            "/api/v1/orchestrator-contexts/projects/Agent%20Studio/turns",
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                "http_reply", DateTime.UtcNow, "orchestrator", "The receipt is central.",
                Receipt: receipt)));
        replyResponse.EnsureSuccessStatusCode();

        var transcript = await client.GetFromJsonAsync<OrchestratorContextTranscriptResponse>(
            "/api/v1/orchestrator-contexts/projects/Agent%20Studio/turns");
        Assert.Equal(2, transcript!.Turns.Count);
        Assert.Equal(
            user.TurnId,
            Assert.Single(transcript.Turns, turn => turn.Receipt is not null).Receipt!.UserTurnId);
        var contexts = await client.GetFromJsonAsync<OrchestratorContextListResponse>(
            "/api/v1/orchestrator-contexts");
        Assert.Equal(2, contexts!.Contexts.Count);
        Assert.Equal(
            user.Body,
            Assert.Single(contexts.Contexts, context => context.Kind == OrchestratorContextKinds.Project).Summary);
    }

    private static TaskServerStore Store(string dataDirectory)
        => new(
            Options.Create(new TaskServerOptions { DataDirectory = dataDirectory }),
            TimeProvider.System);

    private sealed class TaskServerFactory(string dataDirectory)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                }));
        }
    }
}
