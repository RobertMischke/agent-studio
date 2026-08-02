using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class HostOrchestratorClientTests
{
    [Fact]
    public async Task Permit_is_adopted_and_containment_step_is_reported_after_host_report()
    {
        var now = DateTime.UtcNow;
        var task = new TaskDto(
            "task-1", "project-1", "TS-1", "Task", "3-progress", 2, now, now, "prompt");
        var run = new RunDto("run-1", task.TaskId, "running", "runner-1", 7, now, now, null);
        var lease = new LeaseDto(
            "lease-1", run.RunId, task.TaskId, "runner-1", InstanceId(), 7,
            now, now.AddMinutes(2), "active");
        var step = new PostStepPlanDto(
            "step-1", run.RunId, "post-worktree-containment", "runner-1", "available");
        var acceptance = new WorkPermitAcceptanceDto(
            "accepted", "permit-1", run, task, lease, lease.ExpiresAt, [step]);
        var handler = new ContractHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/runners/runner-1/reports" => Json(new HostReportResponse(
                "accepted", 1,
                new HostContractRangeDto(HostOrchestratorContract.Current, HostOrchestratorContract.Current),
                1, "active", [new WorkPermitDto("permit-1", task, 1, now.AddMinutes(5))], [])),
            "/api/v1/work-permits/permit-1/accept" => Json(acceptance),
            "/api/v1/runs/run-1/post-steps/step-1/claim" => Json(new PostStepClaimResponse(
                "claimed", step with { Status = "running" }, 7)),
            "/api/v1/runs/run-1/post-steps/step-1/complete" => Json(new PostStepCompleteResponse(
                "completed", step with { Status = "completed" }, "passed", ["evidence-sha"])),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = Client(http);
        var reportRequest = new HostReportRequest(
            HostOrchestratorContract.Current,
            "host-1",
            InstanceId(),
            1,
            now,
            new HostCapacityDto(1, 1, 0, 0, 1),
            [], [], [], []);

        var report = await client.ReportHostAsync(reportRequest, default);
        var accepted = await client.AcceptWorkPermitAsync(
            Assert.Single(report.AvailableWork), report.AcceptedSequence, report.PolicyVersion, default);
        var claim = client.AdoptWorkPermit(accepted);
        await client.CompleteHostPostProcessingAsync(task.TaskKey, "evidence-sha", default);

        Assert.Equal("run-1", claim.RunId);
        Assert.Equal("lease-1", claim.Lease!.LeaseId);
        Assert.Equal(
            [
                "/api/v1/runners/runner-1/reports",
                "/api/v1/work-permits/permit-1/accept",
                "/api/v1/runs/run-1/post-steps/step-1/claim",
                "/api/v1/runs/run-1/post-steps/step-1/complete",
            ],
            handler.Paths);
    }

    [Fact]
    public async Task Registration_declares_host_contract_and_capabilities()
    {
        JsonElement requestBody = default;
        var handler = new ContractHandler(async (request, ct) =>
        {
            requestBody = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var now = DateTime.UtcNow;
            return Json(new RunnerDto(
                "runner-1", "Runner", "host-1", InstanceId(), "1.0.0",
                TaskServerProtocol.Current, "active", now, now));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = Client(http);

        await client.RegisterAsync("Runner", "service", default);

        Assert.Equal(
            HostOrchestratorContract.Current,
            requestBody.GetProperty("hostOrchestratorMinimum").GetString());
        Assert.Equal(
            HostOrchestratorContract.Current,
            requestBody.GetProperty("hostOrchestratorMaximum").GetString());
        var capabilities = requestBody.GetProperty("capabilities")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        Assert.Contains("permits", capabilities);
        Assert.Contains("local-queue", capabilities);
        Assert.Contains("host-post-processing", capabilities);
    }

    [Fact]
    public async Task Unknown_post_step_fails_closed_before_claiming_it()
    {
        var now = DateTime.UtcNow;
        var task = new TaskDto(
            "task-2", "project-1", "TS-2", "Task", "3-progress", 2, now, now, "prompt");
        var run = new RunDto("run-2", task.TaskId, "running", "runner-1", 8, now, now, null);
        var lease = new LeaseDto(
            "lease-2", run.RunId, task.TaskId, "runner-1", InstanceId(), 8,
            now, now.AddMinutes(2), "active");
        var acceptance = new WorkPermitAcceptanceDto(
            "accepted",
            "permit-2",
            run,
            task,
            lease,
            lease.ExpiresAt,
            [new PostStepPlanDto("step-2", run.RunId, "post-not-implemented", "runner-1", "available")]);
        var handler = new ContractHandler((_, _) => Json(new HostReportResponse(
            "accepted", 1,
            new HostContractRangeDto(HostOrchestratorContract.Current, HostOrchestratorContract.Current),
            1, "active", [], [])));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = Client(http);
        client.AdoptWorkPermit(acceptance);
        await client.ReportHostAsync(new HostReportRequest(
            HostOrchestratorContract.Current,
            "host-1",
            InstanceId(),
            1,
            now,
            new HostCapacityDto(1, 1, 1, 0, 0),
            [], [], [], []), default);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CompleteHostPostProcessingAsync(task.TaskKey, null, default));

        Assert.Contains("no runner implementation", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/api/v1/runners/runner-1/reports"], handler.Paths);
    }

    private static TaskServerClient Client(HttpClient http)
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-1",
            RunnerName = "Runner",
            Hostname = "host-1",
            BackendName = "test",
            GitRemote = "/tmp/origin.git",
            WorkDir = "/tmp/runner-work",
            StateDir = "/tmp/runner-state",
            BaseBranch = "develop",
            CliBin = "codex",
            CliArgs = "exec",
            TtlSeconds = 120,
            HeartbeatSeconds = 30,
            RunTimeoutSeconds = 600,
            HostMaxParallelism = 2,
            PollSeconds = 1,
        };
        return new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            supportsHostOrchestrator: true,
            options: options);
    }

    private static string InstanceId() => $"host-1:{Environment.ProcessId}";

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    private sealed class ContractHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public ContractHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
            : this((request, ct) => Task.FromResult(respond(request, ct)))
        {
        }

        public ContractHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            => _respond = respond;

        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return await _respond(request, cancellationToken);
        }
    }
}
