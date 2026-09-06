using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class ManagementApiTests : IDisposable
{
    private readonly string _root = CreateServerDataDirectory();
    private readonly string _backups;
    private readonly string _logs;

    public ManagementApiTests()
    {
        _backups = _root + "-backups";
        _logs = _root + "-logs";
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_root, state));
        Directory.CreateDirectory(Path.Combine(_root, ".metadata"));
        File.WriteAllText(Path.Combine(_root, ".metadata", "server-evidence.jsonl"), "{}\n");
    }

    [Fact]
    public async Task StatusAndCommands_RequireActor_AndLeaveDurableAudit()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/management/status")).StatusCode);

        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/management/status");
        Assert.Equal("healthy", status.GetProperty("health").GetProperty("state").GetString());
        Assert.True(
            status.GetProperty("store").GetProperty("eventCount").GetInt64() >= 1,
            "The seeded server evidence event must remain visible when startup emits additional runtime events.");

        const string key = "maintenance-test-key";
        var request = new { kind = "maintenance-enter", dryRun = false, confirmation = "maintenance-enter", idempotencyKey = key, reason = "test rehearsal" };
        var first = await client.PostAsJsonAsync("/api/v1/management/commands", request);
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var second = await client.PostAsJsonAsync("/api/v1/management/commands", request);
        second.EnsureSuccessStatusCode();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstBody.GetProperty("commandId").GetString(), secondBody.GetProperty("commandId").GetString());
        Assert.True(File.Exists(Path.Combine(_root, ".audit", "management.jsonl")));
        var audit = File.ReadLines(Path.Combine(_root, ".audit", "management.jsonl")).ToArray();
        Assert.Equal(2, audit.Length);
        Assert.Contains("\"outcome\":\"started\"", audit[0]);
        Assert.Contains("\"outcome\":\"completed\"", audit[1]);
    }

    [Fact]
    public async Task RemoteHosts_ReturnsTheLatestRunnerCapabilitySnapshot()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var registry = factory.Services.GetRequiredService<V1ReviewExecutorRegistry>();
        const string runnerId = "agent-runner-capability-snapshot";
        const string instanceId = "agent-runner-host:2498";
        registry.Register(
            runnerId,
            new Contract.RegisterRunnerRequest(
                "Agent Runner Snapshot",
                "agent-runner-host",
                instanceId,
                "1.2.3",
                Contract.TaskServerProtocol.Current,
                [Contract.ReviewCapabilities.CodingExecutor],
                BootstrapMaxParallelism: 6));
        registry.AdvertiseCapabilities(
            runnerId,
            new Contract.CapabilityAdvertisementRequest(
                runnerId,
                instanceId,
                Contract.CapabilityProtocol.CurrentSchemaVersion,
                DateTime.UtcNow,
                180,
                1,
                [
                    new Contract.AdvertisedCapabilityDto(
                        Contract.CapabilityProtocol.CliExecution("claude"),
                        "cli-execution",
                        "ready",
                        "available",
                        "/usr/bin/claude"),
                    new Contract.AdvertisedCapabilityDto(
                        Contract.CapabilityProtocol.ProviderAuthentication("claude"),
                        "provider-auth",
                        "ready",
                        Identity: "claude"),
                ]));
        registry.ReportCapabilityFailure(
            runnerId,
            new Contract.CapabilityFailureRequest(
                runnerId,
                instanceId,
                Contract.CapabilityProtocol.ProviderAuthentication("claude"),
                "ProviderUnauthorized",
                "Claude login is unavailable for the coding service user.",
                DateTime.UtcNow,
                "snapshot-provider-auth-failure"));

        using var response = await client.GetAsync("/api/v1/management/remote-hosts");

        response.EnsureSuccessStatusCode();
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
        var snapshots = await response.Content.ReadFromJsonAsync<
            IReadOnlyList<Contract.RunnerCapabilitySnapshotDto>>();
        var snapshot = Assert.Single(snapshots!);
        Assert.Equal(runnerId, snapshot.RunnerId);
        Assert.Equal(instanceId, snapshot.InstanceId);
        Assert.Equal(6, snapshot.RoleMaxParallelism);
        Assert.Contains(
            snapshot.Capabilities,
            capability => capability.Key == "cli-execution:claude"
                          && capability.AdvertisedStatus == "ready"
                          && capability.IsFresh
                          && capability.Identity == "/usr/bin/claude");
        Assert.Contains(
            snapshot.Capabilities,
            capability => capability.Key == "provider-auth:claude"
                          && capability.AdvertisedStatus == "ready"
                          && capability.HealthState == Contract.CapabilityHealthStates.Suspect
                          && capability.ConsecutiveFailures == 1
                          && capability.FirstFailureAt is not null);

        var expiresAt = DateTime.UtcNow.AddDays(10);
        registry.AdvertiseCapabilities(
            runnerId,
            new Contract.CapabilityAdvertisementRequest(
                runnerId,
                instanceId,
                Contract.CapabilityProtocol.CurrentSchemaVersion,
                DateTime.UtcNow,
                180,
                2,
                [
                    new Contract.AdvertisedCapabilityDto(
                        Contract.CapabilityProtocol.CliExecution("claude"),
                        "cli-execution"),
                    new Contract.AdvertisedCapabilityDto(
                        Contract.CapabilityProtocol.ProviderAuthentication("claude"),
                        "provider-auth",
                        "ready",
                        Signal: "credentials-expiring",
                        ExpiresAt: expiresAt),
                ]));

        var recovered = Assert.Single(registry.ListCapabilitySnapshots());
        var recoveredAuth = Assert.Single(
            recovered.Capabilities,
            capability => capability.Key == "provider-auth:claude");
        Assert.Equal(Contract.CapabilityHealthStates.Healthy, recoveredAuth.HealthState);
        Assert.Equal(0, recoveredAuth.ConsecutiveFailures);
        Assert.Equal("credentials-expiring", recoveredAuth.Signal);
        Assert.Equal(expiresAt, recoveredAuth.ExpiresAt);
        Assert.Contains(
            recoveredAuth.RecoveryHistory,
            item => item.FromState == Contract.CapabilityHealthStates.Suspect
                    && item.ToState == Contract.CapabilityHealthStates.Healthy);
    }

    [Fact]
    public async Task ProviderAuthProvisioning_RequiresOperator_AndDoesNotEchoTheSecret()
    {
        const string secret = "sk-ant-oat01-provider-secret-fixture";
        var provisioner = new RecordingProviderAuthProvisioner();
        await using var factory = BuildFactory(provisioner: provisioner);
        using var client = factory.CreateClient();
        var request = new ProviderAuthProvisioningRequest(
            "agent@runner-01",
            "agent-runner-01",
            "CLAUDE_CODE_OAUTH_TOKEN",
            secret);

        var denied = await client.PostAsJsonAsync(
            "/api/v1/management/remote-hosts/provider-auth",
            request);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Null(provisioner.LastRequest);

        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var accepted = await client.PostAsJsonAsync(
            "/api/v1/management/remote-hosts/provider-auth",
            request);

        accepted.EnsureSuccessStatusCode();
        Assert.Equal(secret, provisioner.LastRequest?.Secret);
        var body = await accepted.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.Contains("awaiting-probe", body, StringComparison.Ordinal);
        Assert.Contains("no-store", accepted.Headers.CacheControl?.ToString() ?? "");
    }

    [Theory]
    [InlineData("runner;touch /tmp/x", "CLAUDE_CODE_OAUTH_TOKEN", "valid-provider-secret-fixture")]
    [InlineData("agent@runner", "UNSUPPORTED_TOKEN", "valid-provider-secret-fixture")]
    [InlineData("agent@runner", "ANTHROPIC_API_KEY", "secret with whitespace")]
    public async Task ProviderAuthProvisioning_RejectsUnsafeInputBeforeTransport(
        string sshTarget,
        string environmentVariable,
        string secret)
    {
        var provisioner = new RecordingProviderAuthProvisioner();
        await using var factory = BuildFactory(provisioner: provisioner);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var response = await client.PostAsJsonAsync(
            "/api/v1/management/remote-hosts/provider-auth",
            new ProviderAuthProvisioningRequest(
                sshTarget,
                "agent-runner-01",
                environmentVariable,
                secret));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(provisioner.LastRequest);
    }

    [Fact]
    public async Task CodexSignIn_StreamsDevicePrompt_ThenReportsConfirmedStatusWithoutPersistingCode()
    {
        const string code = "ABCD-EFGH";
        var transport = new FakeCodexDeviceAuthTransport(code);
        await using var factory = BuildFactory(codexTransport: transport);
        using var client = factory.CreateClient();

        var denied = await client.PostAsJsonAsync(
            "/api/v1/management/remote-hosts/agent-runner-01/codex-sign-in",
            new CodexSignInStartRequest("agent@runner-01"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var started = await client.PostAsJsonAsync(
            "/api/v1/management/remote-hosts/agent-runner-01/codex-sign-in",
            new CodexSignInStartRequest("agent@runner-01"));
        started.EnsureSuccessStatusCode();
        var prompt = await started.Content.ReadFromJsonAsync<CodexSignInStartResponse>();
        Assert.NotNull(prompt);
        Assert.Equal("https://auth.openai.com/codex/device", prompt.VerificationUrl);
        Assert.Equal(code, prompt.UserCode);

        var pending = await client.GetStringAsync(
            $"/api/v1/management/remote-hosts/agent-runner-01/codex-sign-in/{prompt.Handle}");
        Assert.Contains("pending", pending, StringComparison.Ordinal);
        Assert.DoesNotContain(code, pending, StringComparison.Ordinal);

        transport.Complete();
        await transport.WaitUntilReleasedAsync();
        CodexSignInStatusResponse? terminalStatus = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            terminalStatus = await client.GetFromJsonAsync<CodexSignInStatusResponse>(
                $"/api/v1/management/remote-hosts/agent-runner-01/codex-sign-in/{prompt.Handle}");
            if (terminalStatus?.State == "completed") break;
            await Task.Delay(100);
        }
        Assert.NotNull(terminalStatus);
        Assert.Equal("completed", terminalStatus.State);
        Assert.True(terminalStatus.ProbeRefreshTriggered);
        var terminal = System.Text.Json.JsonSerializer.Serialize(terminalStatus);
        Assert.DoesNotContain(code, terminal, StringComparison.Ordinal);
        string persisted = "";
        for (var attempt = 0; attempt < 100; attempt++)
        {
            persisted = string.Join('\n', Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
            if (persisted.Contains("provider_sign_in", StringComparison.Ordinal)) break;
            await Task.Delay(100);
        }
        Assert.Equal(1, persisted.Split("provider_sign_in", StringSplitOptions.None).Length - 1);
        Assert.Contains("agent-runner-01", persisted, StringComparison.Ordinal);
        Assert.Contains("local-default", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(code, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupCreate_VerifiesRealArchive_OutsideDataDirectory()
    {
        await using var factory = BuildFactory(Environments.Production);
        using var client = factory.CreateClient();
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        Assert.Equal(Environments.Production, environment.EnvironmentName);
        Assert.False(environment.IsDevelopment());
        var relativeToTemp = Path.GetRelativePath(Path.GetFullPath(Path.GetTempPath()), _root);
        Assert.False(
            Path.IsPathRooted(relativeToTemp)
            || relativeToTemp == ".."
            || relativeToTemp.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        await EnterMaintenance(client, "backup-maintenance-key");
        var response = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "backup-create", dryRun = false, confirmation = "backup-create", idempotencyKey = "backup-test-key"
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("affected").GetInt32());
        var backups = Directory.GetFiles(_backups, "backup-*.zip");
        Assert.Single(backups);
        Assert.True(File.Exists(backups[0] + ".manifest.json"));
        Assert.True(new FileInfo(backups[0]).Length > 0);
        Assert.DoesNotContain(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, Path.GetFullPath(backups[0]));

        var verify = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "restore-verify", dryRun = false, confirmation = "restore-verify", idempotencyKey = "restore-test-key"
        });
        verify.EnsureSuccessStatusCode();
        var verifyBody = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, verifyBody.GetProperty("affected").GetInt32());
        var stagingRoot = Path.Combine(_backups, ".restore-verification");
        Assert.False(Directory.Exists(stagingRoot) && Directory.EnumerateFileSystemEntries(stagingRoot).Any());
    }

    [Fact]
    public async Task RestoreVerification_RejectsValidArchiveWhoseCreationChecksumChanged()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        await EnterMaintenance(client, "tamper-maintenance-key");
        (await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "backup-create", dryRun = false, confirmation = "backup-create", idempotencyKey = "tamper-backup-key"
        })).EnsureSuccessStatusCode();

        var archivePath = Assert.Single(Directory.GetFiles(_backups, "backup-*.zip"));
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(archive.CreateEntry("tampered-after-creation.txt").Open());
            writer.Write("changed");
        }

        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/management/status");
        Assert.Equal("failed", status.GetProperty("backups").GetProperty("items")[0].GetProperty("verificationState").GetString());
        var verify = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "restore-verify", dryRun = false, confirmation = "restore-verify", idempotencyKey = "tamper-restore-key"
        });
        verify.EnsureSuccessStatusCode();
        var body = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("affected").GetInt32());
        Assert.Contains("failed before extraction", body.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task MigrationProjection_IsDurableAndControlsReadiness()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var migrations = factory.Services.GetRequiredService<MigrationStateStore>();

        migrations.Begin("schema-regression", "Applying schema regression fixture.");
        var running = await client.GetFromJsonAsync<JsonElement>("/api/v1/management/status");
        Assert.False(running.GetProperty("health").GetProperty("ready").GetBoolean());
        Assert.Equal("maintenance", running.GetProperty("health").GetProperty("state").GetString());
        Assert.Contains("migration-running", running.GetProperty("health").GetProperty("reasons").EnumerateArray().Select(x => x.GetString()));

        migrations.Fail("schema-regression", "fixture failed");
        var failed = await client.GetFromJsonAsync<JsonElement>("/api/v1/management/status");
        Assert.Equal("degraded", failed.GetProperty("health").GetProperty("state").GetString());
        Assert.Equal("failed", failed.GetProperty("migrations")[0].GetProperty("state").GetString());

        migrations.Complete("schema-regression");
        var recovered = await client.GetFromJsonAsync<JsonElement>("/api/v1/management/status");
        Assert.True(recovered.GetProperty("health").GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task ConflictingHeaderAndBodyIdempotencyKeys_AreRejectedBeforeAudit()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/management/commands")
        {
            Content = JsonContent.Create(new
            {
                kind = "archive-sweep", dryRun = true, idempotencyKey = "body-key",
            }),
        };
        request.Headers.Add("Idempotency-Key", "header-key");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_root, ".audit", "management.jsonl")));
    }

    [Fact]
    public async Task WhitespacePaddedOwnerCommand_IsRejectedForOperatorBeforeAudit()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var response = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = " runner-credential-rotate ",
            dryRun = true,
            idempotencyKey = "padded-owner-command-key",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("owner-required", body.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(_root, ".audit", "management.jsonl")));
    }

    [Fact]
    public async Task ReusedIdempotencyKey_WithDifferentPayload_IsRejected()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        const string key = "retention-payload-key";

        var first = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "backup-retention", dryRun = true, idempotencyKey = key, retentionCount = 2,
        });
        first.EnsureSuccessStatusCode();

        var conflicting = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "backup-retention", dryRun = true, idempotencyKey = key, retentionCount = 3,
        });
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        var body = await conflicting.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency-key-conflict", body.GetProperty("error").GetString());

        var audit = File.ReadLines(Path.Combine(_root, ".audit", "management.jsonl")).ToArray();
        Assert.Equal(2, audit.Length);
        Assert.All(audit, row => Assert.Contains("requestFingerprint", row));
    }

    [Fact]
    public async Task MaintenanceMode_RefusesOrdinaryMutations_ButManagementRemainsAvailable()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        await EnterMaintenance(client, "maintenance-admission-key");

        var blocked = await client.PostAsJsonAsync("/api/tasks", new { id = "blocked-in-maintenance" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/management/status");
        Assert.Equal("maintenance", status.GetProperty("maintenance").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task RecoveryConsole_IsServerHosted_AndNamesServiceManagerBoundary()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/recovery");
        Assert.Contains("Task Server bootstrap and recovery", html);
        Assert.Contains("service manager owns process start and restart", html);
        Assert.Contains("/api/v1/management/status", html);
    }

    private WebApplicationFactory<Program> BuildFactory(
        string environment = "Test",
        IProviderAuthProvisioner? provisioner = null,
        ICodexDeviceAuthTransport? codexTransport = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Management:BackupDirectory"] = _backups,
            ["Logging:BackendFile:LogDirectory"] = _logs,
            ["WatchPaths:0:Name"] = "Management Test",
            ["WatchPaths:0:Path"] = _root,
            ["WatchPaths:0:RootPath"] = _root,
            ["Supervisor:StuckResumeWindowMinutes"] = "0",
            ["CodexModels:WarmupOnBoot"] = "false",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            if (provisioner is not null)
            {
                services.RemoveAll<IProviderAuthProvisioner>();
                services.AddSingleton(provisioner);
            }
            if (codexTransport is not null)
            {
                services.RemoveAll<ICodexDeviceAuthTransport>();
                services.AddSingleton(codexTransport);
            }
        });
    });

    private sealed class FakeCodexDeviceAuthTransport(string code) : ICodexDeviceAuthTransport
    {
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete() => _completed.TrySetResult();

        public Task WaitUntilReleasedAsync() => _released.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public async Task<CodexDeviceAuthTransportResult> RunAsync(
            string sshTarget,
            Action<string> observeOutput,
            CancellationToken cancellationToken)
        {
            Assert.Equal("agent@runner-01", sshTarget);
            observeOutput("Open this URL in your browser: https://auth.openai.com/codex/device");
            observeOutput("Enter this one-time code (expires in 15 minutes)");
            observeOutput(code);
            await _completed.Task.WaitAsync(cancellationToken);
            _released.TrySetResult();
            return new CodexDeviceAuthTransportResult(
                0,
                true,
                true,
                "Codex sign-in completed and runner units were restarted for a fresh provider probe.");
        }
    }

    private sealed class RecordingProviderAuthProvisioner : IProviderAuthProvisioner
    {
        public ProviderAuthProvisioningRequest? LastRequest { get; private set; }

        public Task<ProviderAuthProvisioningResponse> ProvisionAsync(
            ProviderAuthProvisioningRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new ProviderAuthProvisioningResponse(
                "claude",
                request.EnvironmentVariable,
                request.SshTarget,
                "awaiting-probe",
                "Credential installed and daemon environment verified.",
                DateTime.UtcNow,
                ["agent-runner.service"],
                true));
        }
    }

    private static string CreateServerDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-studio-server-data", "AGT-2194",
            "server-" + Guid.NewGuid().ToString("N"));
        return Path.GetFullPath(root);
    }

    private static async Task EnterMaintenance(HttpClient client, string key)
    {
        var response = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "maintenance-enter", dryRun = false,
            confirmation = "maintenance-enter", idempotencyKey = key,
        });
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception ex) { SilentCatch.Note(ex, "ManagementApiTests root cleanup"); }
        try { Directory.Delete(_backups, true); } catch (Exception ex) { SilentCatch.Note(ex, "ManagementApiTests backup cleanup"); }
        try { Directory.Delete(_logs, true); } catch (Exception ex) { SilentCatch.Note(ex, "ManagementApiTests log cleanup"); }
    }
}
