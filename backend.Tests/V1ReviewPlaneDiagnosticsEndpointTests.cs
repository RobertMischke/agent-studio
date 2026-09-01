using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// The monolith's v1 review-plane mount must answer the runner's diagnostic
/// routes instead of 404-ing them: an unmounted
/// <c>POST /api/v1/runners/{id}/capability-failures</c> is what turned a single
/// infrastructure failure into a crash loop and left the Review Unit paused
/// (AGT-2374). These tests pin the two diagnostic routes, the executor pause a
/// drained capability causes, and the registration/advertisement that lifts it
/// again.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class V1ReviewPlaneDiagnosticsEndpointTests : IDisposable
{
    private const string ProjectName = "agent-runner-01";
    private const string RunnerId = "review-runner-diagnostics";
    private const string Instance = "review-host:7373";

    private readonly string _workspace;
    private readonly string _watchPath;

    [Fact]
    public void Fresh_provider_auth_probe_clears_in_memory_drain_without_registration()
    {
        var registry = new V1ReviewExecutorRegistry();
        registry.Register(
            RunnerId,
            new Contract.RegisterRunnerRequest(
                RunnerId,
                "review-host",
                Instance,
                "1.0.0",
                Contract.TaskServerProtocol.Current,
                [Contract.ReviewCapabilities.ReviewExecutor]));
        var capability = Contract.CapabilityProtocol.ProviderAuthentication("codex");
        registry.AdvertiseCapabilities(
            RunnerId,
            new Contract.CapabilityAdvertisementRequest(
                RunnerId,
                Instance,
                Contract.CapabilityProtocol.CurrentSchemaVersion,
                DateTime.UtcNow,
                180,
                1,
                [new Contract.AdvertisedCapabilityDto(
                    capability,
                    "provider-auth",
                    "unavailable",
                    Condition: Contract.ProviderAuthProbeConditions.SignedOut)]));
        registry.ReportCapabilityFailure(RunnerId, Failure(
            capability,
            "ProviderUnauthorized",
            "provider-auth-1",
            DateTime.UtcNow.AddSeconds(-1)));
        registry.ReportCapabilityFailure(RunnerId, Failure(
            capability,
            "ProviderUnauthorized",
            "provider-auth-2",
            DateTime.UtcNow));
        Assert.True(registry.TryGetCapabilityPause(RunnerId, out _));

        var snapshot = registry.AdvertiseCapabilities(
            RunnerId,
            new Contract.CapabilityAdvertisementRequest(
                RunnerId,
                Instance,
                Contract.CapabilityProtocol.CurrentSchemaVersion,
                DateTime.UtcNow,
                180,
                2,
                [new Contract.AdvertisedCapabilityDto(
                    capability,
                    "provider-auth",
                    Condition: Contract.ProviderAuthProbeConditions.Ok)]));

        Assert.False(registry.TryGetCapabilityPause(RunnerId, out _));
        var recovered = Assert.Single(snapshot.Capabilities);
        Assert.Equal(Contract.CapabilityHealthStates.Healthy, recovered.HealthState);
        Assert.Contains(recovered.RecoveryHistory, item =>
            item.Reason.Contains("without a runner restart", StringComparison.Ordinal));
    }

    public V1ReviewPlaneDiagnosticsEndpointTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-v1-diagnostics-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Capability_failure_is_recorded_idempotently_and_drains_the_executor_on_the_second_report()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        await RegisterReviewExecutorAsync(http);

        var now = DateTime.UtcNow;
        var firstReport = Failure("toolchain:dotnet", "ToolchainUnavailable", "cap-failure-1", now.AddSeconds(-5));

        var first = await http.PostAsJsonAsync(FailurePath(RunnerId), firstReport);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var accepted = await first.Content.ReadFromJsonAsync<Contract.CapabilityFailureResponse>();
        Assert.Equal("accepted", accepted!.Status);
        Assert.Equal("toolchain:dotnet", accepted.CapabilityKey);
        Assert.Equal(Contract.CapabilityHealthStates.Suspect, accepted.HealthState);
        Assert.Null(accepted.CooldownUntil);
        Assert.False(accepted.WholeHostDraining);

        // A replayed idempotency key returns its first verdict and must not
        // advance the state machine - the runner retries reports freely.
        var replay = await http.PostAsJsonAsync(FailurePath(RunnerId), firstReport);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(accepted, await replay.Content.ReadFromJsonAsync<Contract.CapabilityFailureResponse>());

        var second = await http.PostAsJsonAsync(
            FailurePath(RunnerId),
            Failure("toolchain:dotnet", "ToolchainUnavailable", "cap-failure-2", now));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var drained = await second.Content.ReadFromJsonAsync<Contract.CapabilityFailureResponse>();
        Assert.Equal(Contract.CapabilityHealthStates.Draining, drained!.HealthState);
        Assert.NotNull(drained.CooldownUntil);
        Assert.True(drained.CooldownUntil > DateTime.UtcNow);

        // The drained capability pauses this executor's claims instead of feeding
        // it attempts it just reported itself unable to materialize.
        var paused = await ClaimAsync(http);
        Assert.Equal("empty", paused.Status);
        Assert.Contains("paused", paused.Message!, StringComparison.Ordinal);
        Assert.Contains("toolchain:dotnet", paused.Message!, StringComparison.Ordinal);

        // ... and the routine 60s capability advertisement must NOT cut that drain
        // short: it refreshes the snapshot, it is not a health verdict. Lifting
        // the pause here would mean the drain never drains - a broken executor
        // would be claim-eligible again one minute later, forever.
        var advertisement = await http.PostAsJsonAsync(
            $"/api/v1/runners/{RunnerId}/capabilities",
            new Contract.CapabilityAdvertisementRequest(
                RunnerId,
                Instance,
                Contract.CapabilityProtocol.CurrentSchemaVersion,
                DateTime.UtcNow,
                180,
                1,
                [new Contract.AdvertisedCapabilityDto(Contract.CapabilityProtocol.DotNet, "toolchain")]));
        advertisement.EnsureSuccessStatusCode();

        var stillPaused = await ClaimAsync(http);
        Assert.Equal("empty", stillPaused.Status);
        Assert.Contains("paused", stillPaused.Message!, StringComparison.Ordinal);

        // The pause lifts by itself once the cooldown runs out.
        factory.Services
            .GetRequiredService<V1ReviewExecutorRegistry>()
            .AgeCapabilityFailuresForTests(RunnerId, TimeSpan.FromMinutes(10));

        var resumed = await ClaimAsync(http);
        Assert.Equal("empty", resumed.Status);
        Assert.DoesNotContain("paused", resumed.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_re_registered_review_executor_starts_unpaused_even_inside_an_active_cooldown()
    {
        // The other way out of a drain: a full re-registration. That is a daemon
        // restart re-declaring this identity's health, so a replacement instance
        // is never born paused by its predecessor's failures.
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        await RegisterReviewExecutorAsync(http);

        var now = DateTime.UtcNow;
        foreach (var key in new[] { "cap-drain-1", "cap-drain-2" })
        {
            var reported = await http.PostAsJsonAsync(
                FailurePath(RunnerId),
                Failure("toolchain:dotnet", "ToolchainUnavailable", key, now));
            reported.EnsureSuccessStatusCode();
        }

        var paused = await ClaimAsync(http);
        Assert.Contains("paused", paused.Message!, StringComparison.Ordinal);

        await RegisterReviewExecutorAsync(http);

        var resumed = await ClaimAsync(http);
        Assert.Equal("empty", resumed.Status);
        Assert.DoesNotContain("paused", resumed.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Concurrent_registration_and_advertisement_never_hold_the_registry_gate()
    {
        var registry = new V1ReviewExecutorRegistry();
        var registration = new Contract.RegisterRunnerRequest(
            RunnerId,
            "review-host",
            Instance,
            "1.0.0",
            Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.ReviewExecutor]);
        registry.Register(RunnerId, registration);

        var operations = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            registry.Register(RunnerId, registration);
            return registry.AdvertiseCapabilities(
                RunnerId,
                new Contract.CapabilityAdvertisementRequest(
                    RunnerId,
                    Instance,
                    Contract.CapabilityProtocol.CurrentSchemaVersion,
                    DateTime.UtcNow,
                    180,
                    1,
                    [new Contract.AdvertisedCapabilityDto(
                        Contract.CapabilityProtocol.DotNet,
                        "toolchain")]));
        }));

        var snapshots = await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(100, snapshots.Length);
        Assert.All(snapshots, snapshot => Assert.Equal(RunnerId, snapshot.RunnerId));
    }

    [Fact]
    public async Task Capability_failure_of_a_whole_host_capability_drains_on_the_first_report()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        await RegisterReviewExecutorAsync(http);

        var response = await http.PostAsJsonAsync(
            FailurePath(RunnerId),
            Failure(Contract.CapabilityProtocol.Disk, "DiskFull", "host-disk-1", DateTime.UtcNow));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var drained = await response.Content.ReadFromJsonAsync<Contract.CapabilityFailureResponse>();
        Assert.Equal(Contract.CapabilityHealthStates.Draining, drained!.HealthState);
        Assert.True(drained.WholeHostDraining);
        Assert.NotNull(drained.CooldownUntil);
    }

    [Fact]
    public async Task Capability_failure_rejects_a_route_and_body_runner_mismatch()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        await RegisterReviewExecutorAsync(http);

        var response = await http.PostAsJsonAsync(
            FailurePath("a-different-runner"),
            Failure("toolchain:dotnet", "ToolchainUnavailable", "cap-mismatch", DateTime.UtcNow));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<Contract.ApiError>();
        Assert.Equal("runner-id-mismatch", error!.Code);
    }

    [Fact]
    public async Task Capability_failure_of_a_stale_instance_is_a_conflict()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        await RegisterReviewExecutorAsync(http);

        var response = await http.PostAsJsonAsync(
            FailurePath(RunnerId),
            Failure("toolchain:dotnet", "ToolchainUnavailable", "cap-stale", DateTime.UtcNow)
                with { InstanceId = "review-host:1111" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<Contract.ApiError>();
        Assert.Equal("capability-failure-conflict", error!.Code);
    }

    [Fact]
    public async Task Outbox_status_is_accepted_recorded_and_guards_its_sequence()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();

        var accepted = await http.PutAsJsonAsync(
            OutboxPath(RunnerId),
            Outbox(lastSequence: 12, acknowledged: 10, backlog: 2, oldestUnacknowledged: 11));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var status = await accepted.Content.ReadFromJsonAsync<Contract.RunnerOutboxStatusDto>();
        Assert.Equal(RunnerId, status!.RunnerId);
        Assert.Equal(Instance, status.InstanceId);
        Assert.Equal(12L, status.LastSequence);
        Assert.Equal(10L, status.LastAcknowledgedSequence);
        Assert.Equal(2, status.BacklogCount);
        Assert.Equal("run-diagnostics", status.RunId);

        var recorded = factory.Services
            .GetRequiredService<V1ReviewExecutorRegistry>()
            .GetOutboxStatus(RunnerId, "run-diagnostics");
        Assert.Equal(status, recorded);

        var stale = await http.PutAsJsonAsync(
            OutboxPath(RunnerId),
            Outbox(lastSequence: 5, acknowledged: 5, backlog: 0, oldestUnacknowledged: null));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "stale-outbox-status",
            (await stale.Content.ReadFromJsonAsync<Contract.ApiError>())!.Code);

        var invalid = await http.PutAsJsonAsync(
            OutboxPath(RunnerId),
            Outbox(lastSequence: 14, acknowledged: 20, backlog: 0, oldestUnacknowledged: null));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(
            "invalid-request",
            (await invalid.Content.ReadFromJsonAsync<Contract.ApiError>())!.Code);
    }

    [Fact]
    public async Task Diagnostic_routes_reject_a_foreign_runner_principal()
    {
        using var factory = BuildFactory(runnerPrincipalId: "another-runner");
        using var http = factory.CreateClient();

        var failure = await http.PostAsJsonAsync(
            FailurePath(RunnerId),
            Failure("toolchain:dotnet", "ToolchainUnavailable", "cap-foreign", DateTime.UtcNow));
        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);

        var outbox = await http.PutAsJsonAsync(
            OutboxPath(RunnerId),
            Outbox(lastSequence: 1, acknowledged: 1, backlog: 0, oldestUnacknowledged: null));
        Assert.Equal(HttpStatusCode.Unauthorized, outbox.StatusCode);
    }

    [Fact]
    public async Task Diagnostic_routes_accept_the_runner_s_own_principal()
    {
        using var factory = BuildFactory(runnerPrincipalId: RunnerId);
        using var http = factory.CreateClient();

        var failure = await http.PostAsJsonAsync(
            FailurePath(RunnerId),
            Failure("toolchain:dotnet", "ToolchainUnavailable", "cap-own", DateTime.UtcNow));
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);

        var outbox = await http.PutAsJsonAsync(
            OutboxPath(RunnerId),
            Outbox(lastSequence: 1, acknowledged: 1, backlog: 0, oldestUnacknowledged: null));
        Assert.Equal(HttpStatusCode.OK, outbox.StatusCode);
    }

    private static string FailurePath(string runnerId)
        => $"/api/v1/runners/{runnerId}/capability-failures";

    private static string OutboxPath(string runnerId)
        => $"/api/v1/runners/{runnerId}/outbox-status";

    private static Contract.CapabilityFailureRequest Failure(
        string capabilityKey,
        string classification,
        string idempotencyKey,
        DateTime occurredAt)
        => new(
            RunnerId,
            Instance,
            capabilityKey,
            classification,
            "The review workspace could not be materialized.",
            occurredAt,
            idempotencyKey,
            "review",
            "rat_diagnostics",
            7);

    private static Contract.RunnerOutboxStatusRequest Outbox(
        long lastSequence,
        long acknowledged,
        int backlog,
        long? oldestUnacknowledged)
        => new(
            Instance,
            lastSequence,
            acknowledged,
            backlog,
            oldestUnacknowledged,
            "pending",
            "run-diagnostics",
            null,
            DateTime.UtcNow);

    private static async Task RegisterReviewExecutorAsync(HttpClient http)
    {
        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}",
            new Contract.RegisterRunnerRequest(
                RunnerId,
                "review-host",
                Instance,
                "1.0.0",
                Contract.TaskServerProtocol.Current,
                [
                    Contract.ReviewCapabilities.ReviewExecutor,
                    Contract.ReviewCapabilities.BaselineComparison,
                    Contract.ReviewCapabilities.DependencyPreparation,
                    Contract.ReviewCapabilities.GitMaterialization,
                    Contract.ReviewCapabilities.SemanticReview,
                ]));
        registration.EnsureSuccessStatusCode();
    }

    private static async Task<Contract.ReviewClaimResponse> ClaimAsync(HttpClient http)
    {
        var response = await http.PostAsJsonAsync(
            $"/api/v1/runners/{RunnerId}/review-claims",
            new Contract.ReviewClaimRequest(RunnerId, Instance, 120, 1));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>())!;
    }

    private WebApplicationFactory<Program> BuildFactory(string? runnerPrincipalId = null) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                        ["WatchPaths:0:RepositoryPath"] = _watchPath,
                        ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    }));
                if (runnerPrincipalId is not null)
                {
                    builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(
                        new RunnerPrincipalStartupFilter(runnerPrincipalId)));
                }
            });

    /// <summary>
    /// Stamps an authenticated Runner principal on every request. The local
    /// profile never authenticates one, so this is the only way to exercise the
    /// v1 mount's per-route identity guard from an in-process client.
    /// </summary>
    private sealed class RunnerPrincipalStartupFilter(string runnerId) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] = new RunnerPrincipal(
                    runnerId,
                    runnerId,
                    "test-credential",
                    new HashSet<string>(StringComparer.Ordinal));
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
