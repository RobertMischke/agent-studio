using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ManagementApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "task-server-management-non-development-" + Guid.NewGuid().ToString("N"));
    private readonly string _backups;

    public ManagementApiTests()
    {
        _backups = _root + "-backups";
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
        Assert.Equal(1, status.GetProperty("store").GetProperty("eventCount").GetInt64());

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
    public async Task BackupCreate_VerifiesRealArchive_OutsideDataDirectory()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        await EnterMaintenance(client, "backup-maintenance-key");
        var response = await client.PostAsJsonAsync("/api/v1/management/commands", new
        {
            kind = "backup-create", dryRun = false, confirmation = "backup-create", idempotencyKey = "backup-test-key"
        });
        response.EnsureSuccessStatusCode();
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

    private WebApplicationFactory<Program> BuildFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Management:BackupDirectory"] = _backups,
            ["WatchPaths:0:Name"] = "Management Test",
            ["WatchPaths:0:Path"] = _root,
            ["WatchPaths:0:RootPath"] = _root,
            ["Supervisor:StuckResumeWindowMinutes"] = "0",
        }));
    });

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
    }
}
