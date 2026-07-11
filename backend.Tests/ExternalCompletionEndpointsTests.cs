using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// HTTP-level coverage for <c>POST /api/tasks/{id}/external-completion</c>
/// (docs/concepts/out-of-band-task-completion.md §3). Pins the atomic
/// reconciliation contract: status.md + deliverables.md are written, the
/// stale lifecycle is terminalized, the external timeline entry lands, the
/// externalCompletion provenance is stamped, and the lane moves.
/// </summary>
public sealed class ExternalCompletionEndpointsTests : IDisposable
{
    private const string ProjectName = "external-completion-test";

    private readonly string _workspace;
    private readonly string _watchPath;

    public ExternalCompletionEndpointsTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-external-completion-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ExternalCompletion_ReconcilesEscalatedCard_WritesEvidenceMovesLaneRecordsTimeline()
    {
        // A card stuck in 5e-escalated with the "no agent-written summary" corpse
        // and a lifecycle.json still running post-processing (the AGT-1917 shape).
        WriteJob(TaskStates.Escalated, "stuck-card", "Stuck Card",
            "Do the out-of-band thing.",
            statusBody: "# Status\n\n- Result: Escalated to human decision (watchdog-kill)\n\nno agent-written summary.");
        WriteLifecycleRunning(TaskStates.Escalated, "stuck-card");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        var request = new ExternalCompletionRequest
        {
            Summary = "Implemented and committed out-of-band in docs/concepts.",
            Source = "operator-chat",
            Deliverables = new List<ExternalDeliverable>
            {
                new() { Path = "docs/concepts/out-of-band-task-completion.md@abc1234", Note = "concept doc" },
                new() { Url = "https://github.com/example/repo/tree/runner/host/stuck-card", Note = "runner salvage branch" },
            },
            GateItems =
            [
                "worktree-blocked: unsecured worktree on agent-runner-01: /var/lib/agent-runner/worktrees/AGT-2147",
            ],
        };

        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/stuck-card/external-completion?watchPath={watchPath}", request);

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(TaskStates.HumanReview, body.RootElement.GetProperty("targetState").GetString());
        Assert.Equal("operator-chat", body.RootElement.GetProperty("source").GetString());

        // Lane moved 5e-escalated -> 5-human-review.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "stuck-card")));
        var moved = Path.Combine(_watchPath, TaskStates.HumanReview, "stuck-card");
        Assert.True(Directory.Exists(moved));

        // status.md replaced with the out-of-band result.
        var status = File.ReadAllText(Path.Combine(moved, "status.md"));
        Assert.Contains("Completed out-of-band", status);
        Assert.Contains("operator-chat", status);
        Assert.DoesNotContain("no agent-written summary", status);

        // results/deliverables.md written with the deliverable + provenance.
        var deliverables = File.ReadAllText(Path.Combine(moved, "results", "deliverables.md"));
        Assert.Contains("out-of-band-task-completion.md@abc1234", deliverables);
        Assert.Contains("[https://github.com/example/repo/tree/runner/host/stuck-card]", deliverables);
        Assert.Contains("Completed externally by operator-chat", deliverables);

        // task.json carries the externalCompletion provenance for the badge.
        var taskJson = File.ReadAllText(Path.Combine(moved, "task.json"));
        Assert.Contains("externalCompletion", taskJson);
        Assert.Contains("operator-chat", taskJson);

        // lifecycle.json terminalized: awaiting-review + running check skipped.
        var lifecycle = File.ReadAllText(Path.Combine(moved, "lifecycle.json"));
        Assert.Contains("awaiting-review", lifecycle);
        Assert.Contains("skipped", lifecycle);
        Assert.DoesNotContain("\"status\": \"running\"", lifecycle);

        // The external timeline entry lands (the card's history stops being a corpse).
        var timeline = File.ReadAllText(TaskPaths.TimelineLog(moved));
        Assert.Contains(TimelineEventKinds.ExternalCompletion, timeline);
        Assert.Contains("Completed externally by operator-chat", timeline);

        // A remote salvage failure arrives as an explicit open gate item. The
        // escalation summary consumes this checklist on the moved card.
        var followUp = File.ReadAllText(Path.Combine(moved, "orchestrator-follow-up.md"));
        Assert.Contains("- [ ] worktree-blocked: unsecured worktree on agent-runner-01", followUp);
    }

    [Fact]
    public async Task ExternalCompletion_MissingSummary_ReturnsBadRequest()
    {
        WriteJob(TaskStates.Escalated, "no-summary", "No Summary", "Prompt.");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/no-summary/external-completion?watchPath={watchPath}",
            new ExternalCompletionRequest { Source = "chat" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The card is untouched: no accidental lane move on a rejected request.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "no-summary")));
    }

    [Fact]
    public async Task ExternalCompletion_UnknownTask_ReturnsNotFound()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/does-not-exist/external-completion?watchPath={watchPath}",
            new ExternalCompletionRequest { Summary = "done", Source = "chat" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExternalCompletion_HonorsExplicitTargetState()
    {
        WriteJob(TaskStates.Progress, "explicit-target", "Explicit Target", "Prompt.", commitSha: null);

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/explicit-target/external-completion?watchPath={watchPath}",
            new ExternalCompletionRequest
            {
                Summary = "done elsewhere",
                Source = "remote-arm",
                TargetState = TaskStates.Completed,
            });

        response.EnsureSuccessStatusCode();
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Completed, "explicit-target")));
    }

    [Fact]
    public async Task ExternalCompletion_FromRegisteredService_EntersRegularPostProcessing()
    {
        // Recovery may move a remote card before its completion arrives. The
        // registered runner identity must still start the regular review path.
        WriteJob(TaskStates.Escalated, "remote-completion", "Remote Completion", "Prompt.");
        var queue = new RecordingAutoReviewQueue();

        using var factory = BuildFactory(queue);
        using var client = factory.CreateClient();
        using var registration = await client.PostAsJsonAsync("/api/clients/register", new RegisterClientRequest
        {
            DisplayName = "agent-runner-01",
            Kind = ClientIdentityKinds.Service,
        });
        registration.EnsureSuccessStatusCode();
        using var registrationBody = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var runnerClientId = registrationBody.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(runnerClientId));
        client.DefaultRequestHeaders.Add("X-Client-Id", runnerClientId);

        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/remote-completion/external-completion?watchPath={Uri.EscapeDataString(_watchPath)}",
            new ExternalCompletionRequest
            {
                Summary = "Delivered on the remote branch.",
                // Deliberately not runner-shaped: routing must use identity kind.
                Source = "operator-chat",
                TargetState = TaskStates.HumanReview,
            });

        response.EnsureSuccessStatusCode();
        using var responseBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(TaskStates.AutoReview, responseBody.RootElement.GetProperty("targetState").GetString());
        var moved = Path.Combine(_watchPath, TaskStates.AutoReview, "remote-completion");
        Assert.True(Directory.Exists(moved));
        var queued = Assert.Single(queue.Requests);
        Assert.Equal("remote-completion", queued.JobId);
        Assert.Equal("runner-external-completion", queued.Source);
    }

    [Fact]
    public async Task ExternalCompletion_FromChat_RemainsLightweightEvenWithRunnerShapedSource()
    {
        WriteJob(TaskStates.Progress, "chat-completion", "Chat Completion", "Prompt.");
        var queue = new RecordingAutoReviewQueue();

        using var factory = BuildFactory(queue);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/chat-completion/external-completion?watchPath={Uri.EscapeDataString(_watchPath)}",
            new ExternalCompletionRequest
            {
                Summary = "Relayed by a human operator.",
                Source = "agent-runner-01",
            });

        response.EnsureSuccessStatusCode();
        var moved = Path.Combine(_watchPath, TaskStates.HumanReview, "chat-completion");
        Assert.True(Directory.Exists(moved));
        var lifecycle = File.ReadAllText(Path.Combine(moved, "lifecycle.json"));
        Assert.Contains("awaiting-review", lifecycle);
        Assert.DoesNotContain("post-processing-running", lifecycle);
        Assert.Empty(queue.Requests);
    }

    private WebApplicationFactory<Program> BuildFactory(RecordingAutoReviewQueue? queue = null) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                    });
                });
                if (queue != null)
                {
                    b.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IAutoReviewPostProcessingQueue>();
                        services.AddSingleton<IAutoReviewPostProcessingQueue>(queue);
                    });
                }
            });

    private void WriteJob(
        string state,
        string slug,
        string title,
        string promptBody,
        string statusBody = "Result: Success.",
        string? commitSha = "abc1234")
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);

        var commitJson = commitSha == null
            ? ""
            : $",\"commits\":[{{\"sha\":\"{commitSha}\",\"message\":\"work\",\"authorEmail\":\"x@y\",\"at\":\"2026-05-29T12:00:00Z\",\"fileCount\":1}}]";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{title}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"{commitJson}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        File.WriteAllText(Path.Combine(dir, "status.md"), statusBody);
    }

    private void WriteLifecycleRunning(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        var lifecycle =
            "{\"version\":1,\"phase\":\"post-processing-running\"," +
            "\"phaseEnteredAt\":\"2026-07-07T10:00:00Z\"," +
            "\"postProcessingChecks\":[{\"name\":\"orchestrator-post-processing\"," +
            "\"status\":\"running\",\"startedAt\":\"2026-07-07T10:00:00Z\"," +
            "\"detail\":\"still running\"}]}";
        File.WriteAllText(Path.Combine(dir, "lifecycle.json"), lifecycle);
    }

    private sealed class RecordingAutoReviewQueue : IAutoReviewPostProcessingQueue
    {
        public List<AutoReviewPostProcessingRequest> Requests { get; } = [];

        public bool Enqueue(AutoReviewPostProcessingRequest request)
        {
            Requests.Add(request);
            return true;
        }
    }
}
