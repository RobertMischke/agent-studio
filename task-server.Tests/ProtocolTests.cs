using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TaskServer.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task Unsupported_runner_is_rejected_before_registration_or_claim()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, "0");

        var response = await client.PutAsJsonAsync(
            "/api/v1/runners/old-runner",
            new RegisterRunnerRequest("old", "host", "instance", "0.8.0", 0));

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("protocol-unsupported", error!.Code);

        var claim = await client.PostAsJsonAsync(
            "/api/v1/runners/old-runner/claims",
            new ClaimRequest("old-runner", "old-instance"));
        Assert.Equal(HttpStatusCode.UpgradeRequired, claim.StatusCode);
    }

    [Fact]
    public async Task Supported_mixed_product_versions_negotiate_protocol_v1()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();

        var studio = await client.PostAsJsonAsync(
            "/api/v1/protocol/compatibility",
            new ProtocolCompatibilityRequest("studio", "1.1.0", 1));
        var runner = await client.PostAsJsonAsync(
            "/api/v1/protocol/compatibility",
            new ProtocolCompatibilityRequest("runner", "1.0.0", 1));

        Assert.Equal(HttpStatusCode.OK, studio.StatusCode);
        Assert.Equal(HttpStatusCode.OK, runner.StatusCode);
        Assert.True((await runner.Content.ReadFromJsonAsync<ProtocolCompatibilityResponse>())!.Supported);
    }

    [Fact]
    public async Task Published_contract_fixtures_pin_supported_and_unsupported_mixed_versions()
    {
        var root = RepositoryRoot();
        using var supported = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(root, "contracts", "fixtures", "protocol-v1", "supported-mixed-versions.json")));
        using var unsupported = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(root, "contracts", "fixtures", "protocol-v1", "unsupported-runner.json")));

        Assert.True(TaskServerProtocol.Supports(supported.RootElement.GetProperty("runner").GetProperty("protocolVersion").GetInt32()));
        Assert.False(TaskServerProtocol.Supports(unsupported.RootElement.GetProperty("runner").GetProperty("protocolVersion").GetInt32()));
        Assert.Equal("rejected-before-claim", unsupported.RootElement.GetProperty("runner").GetProperty("expected").GetString());
    }

    [Fact]
    public async Task Task_attempt_timeline_api_exposes_typed_outcome_raw_facts_and_recovery()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());

        var workspaceResponse = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest("Outcome API", "wsp-outcome"));
        workspaceResponse.EnsureSuccessStatusCode();
        var projectResponse = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest("wsp-outcome", "Outcome API", "OUT", "prj-outcome"));
        projectResponse.EnsureSuccessStatusCode();
        var taskResponse = await client.PostAsJsonAsync(
            "/api/v1/projects/prj-outcome/tasks",
            new CreateTaskRequest("Classify this", State: "2-ready", TaskKey: "OUT-1"));
        taskResponse.EnsureSuccessStatusCode();

        var register = await client.PutAsJsonAsync(
            "/api/v1/runners/runner-outcome",
            new RegisterRunnerRequest("runner-outcome", "host-a", "instance-a", "1.0.0", 1));
        register.EnsureSuccessStatusCode();
        var claimResponse = await client.PostAsJsonAsync(
            "/api/v1/runners/runner-outcome/claims",
            new ClaimRequest("runner-outcome", "instance-a"));
        claimResponse.EnsureSuccessStatusCode();
        var claim = (await claimResponse.Content.ReadFromJsonAsync<ClaimResponse>())!;

        var decision = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            claim.Run!.RunId,
            ExecutionAttemptKind.Coding,
            StdErr: "429 quota exceeded",
            ExitCode: 1));
        var completion = await client.PostAsJsonAsync(
            $"/api/v1/runs/{claim.Run.RunId}/completion",
            new CompleteRunRequest(
                "runner-outcome",
                "instance-a",
                claim.Lease!.LeaseId,
                claim.Lease.Fence,
                decision.Outcome.ToString(),
                OutcomeDecision: decision));
        completion.EnsureSuccessStatusCode();

        var attempts = await client.GetFromJsonAsync<List<ExecutionAttemptTimelineDto>>(
            "/api/v1/projects/prj-outcome/tasks/OUT-1/attempts");
        var attempt = Assert.Single(attempts!);
        Assert.Equal(claim.Run.RunId, attempt.Run.RunId);
        Assert.Equal(ExecutionOutcomeKind.QuotaExceeded, attempt.OutcomeDecision!.Outcome);
        Assert.Equal(ExecutionRecoveryAction.WaitForCapabilityRecovery, attempt.OutcomeDecision.RecoveryAction);
        Assert.Equal("429 quota exceeded", attempt.OutcomeDecision.RawFacts.StdErr);
    }

    internal static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "agent-taskboard.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class TaskServerFactory(string dataDirectory) : WebApplicationFactory<Program>
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
