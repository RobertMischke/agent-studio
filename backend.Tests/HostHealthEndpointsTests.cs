using System.Net;
using System.Text.Json;

using AgentStudio.HostHealth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// End-to-end break-and-heal rehearsal over real HTTP: a fake npm global bin
/// loses its <c>claude</c> shim, the operator hits the repair route, and the
/// host comes back healthy with a journal row and a status-bar note behind it.
/// Only the npm invocation is faked - routing, DI, diagnosis, the rate limit,
/// and the JSONL journal are the production ones.
///
/// <para>
/// MachineBound: the <c>--version</c> probe starts a real child process from a
/// real temporary directory, which is exactly the boundary this rehearsal is
/// meant to exercise.
/// </para>
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class HostHealthEndpointsTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "agt-2673-http-" + Guid.NewGuid().ToString("N"));

    private readonly string _npmBin;
    private readonly string _packageDirectory;

    public HostHealthEndpointsTests()
    {
        _npmBin = Path.Combine(_workspace, "npm");
        _packageDirectory = Path.Combine(_npmBin, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(_npmBin);
        WritePackage("2.1.231");
        WriteShim();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is harmless */ }
    }

    /// <summary>Stands in for <c>npm install --global</c>: puts the shim back the way a real reinstall would.</summary>
    private sealed class ShimRestoringInstaller : IGlobalNpmPackageInstaller
    {
        private readonly Action _restore;
        public ShimRestoringInstaller(Action restore) => _restore = restore;
        public int Calls { get; private set; }

        public Task<GlobalNpmInstallResult> InstallGlobalAsync(string packageId, CancellationToken ct)
        {
            Calls++;
            _restore();
            return Task.FromResult(new GlobalNpmInstallResult(true, 0, 4200, "added 1 package", null));
        }
    }

    [Fact]
    public async Task A_host_that_lost_its_shim_is_repaired_through_the_repair_route()
    {
        var installer = new ShimRestoringInstaller(() => { WriteShim(); WritePackage("2.1.234"); });
        using var factory = BuildFactory(installer);
        using var client = Authenticated(factory);

        // 1. Healthy baseline: the shim is on disk and the CLI runs.
        var healthy = await ReadHealthAsync(client);
        Assert.Equal("Ready", ClaudeState(healthy));

        // 2. Break it the way the control-plane host broke twice: remove the
        //    bin shims, leave the installed package alone.
        BreakShim();

        var broken = await ReadHealthAsync(client);
        Assert.Equal("ShimMissingPackagePresent", ClaudeState(broken));
        Assert.Equal("GlobalReinstall", ClaudeEntry(broken).GetProperty("action").GetString());
        Assert.False(ClaudeEntry(broken).GetProperty("available").GetBoolean());

        // 3. Heal it.
        using var repair = await client.PostAsync("/api/v1/host-health/cli/claude/repair", content: null);
        repair.EnsureSuccessStatusCode();
        var entry = JsonDocument.Parse(await repair.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(1, installer.Calls);
        Assert.Equal("Ready", entry.GetProperty("state").GetString());
        Assert.True(entry.GetProperty("available").GetBoolean());

        // 4. The fix is not silent: one journal row and one status-bar note.
        var row = JsonDocument.Parse(Assert.Single(JournalLines())).RootElement;
        Assert.True(row.GetProperty("repaired").GetBoolean());
        Assert.True(row.GetProperty("operatorRequested").GetBoolean());
        Assert.Equal("2.1.231", row.GetProperty("packageVersionBefore").GetString());
        Assert.Equal("2.1.234", row.GetProperty("packageVersionAfter").GetString());

        var healed = await ReadHealthAsync(client);
        var note = healed.GetProperty("recentRepairs").EnumerateArray().Single();
        Assert.True(note.GetProperty("repaired").GetBoolean());
        Assert.Contains("claude CLI repaired", note.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cli_this_host_cannot_reinstall_is_rejected_with_400()
    {
        using var factory = BuildFactory(new ShimRestoringInstaller(() => { }));
        using var client = Authenticated(factory);

        using var response = await client.PostAsync("/api/v1/host-health/cli/gemini/repair", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(JournalLines());
    }

    // ===== Helpers =====

    private WebApplicationFactory<Program> BuildFactory(IGlobalNpmPackageInstaller installer) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["HostHealth:NpmGlobalBin"] = _npmBin,
                        // The periodic loop is exercised by its own unit tests;
                        // this rehearsal drives the operator route explicitly.
                        ["HostHealth:CliSelfHealEnabled"] = "false",
                        // The fake CLI is the shim itself, so a missing shim is
                        // also a failing --version probe.
                        ["ClaudeCli:Path"] = Path.Combine(_npmBin, "claude"),
                    }));
                builder.ConfigureTestServices(services =>
                    services.AddSingleton(installer));
            });

    /// <summary>The access-security middleware identifies callers by client id, exactly as the UI does.</summary>
    private static HttpClient Authenticated(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    private async Task<JsonElement> ReadHealthAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/host-health/cli");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static JsonElement ClaudeEntry(JsonElement snapshot)
        => snapshot.GetProperty("clis").EnumerateArray()
            .Single(cli => cli.GetProperty("cliType").GetString() == "claude");

    private static string? ClaudeState(JsonElement snapshot)
        => ClaudeEntry(snapshot).GetProperty("state").GetString();

    private string[] JournalLines()
    {
        var path = Path.Combine(_workspace, "logs", "cli-repairs.jsonl");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    private void WriteShim()
    {
        var shim = Path.Combine(_npmBin, "claude");
        File.WriteAllText(shim, "#!/bin/sh\necho 2.1.234\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shim,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private void BreakShim() => File.Delete(Path.Combine(_npmBin, "claude"));

    private void WritePackage(string version)
    {
        Directory.CreateDirectory(_packageDirectory);
        File.WriteAllText(
            Path.Combine(_packageDirectory, "package.json"),
            $$"""{"name":"@anthropic-ai/claude-code","version":"{{version}}"}""");
    }
}
