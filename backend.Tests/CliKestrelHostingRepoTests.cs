using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrchestratorApi.Services.Cli;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Reproduction probes for the original "agent silent after init" hang
/// inside the actual ASP.NET host, using
/// <see cref="WebApplicationFactory{TEntryPoint}"/>. These tests are
/// load-bearing: they pin the seam where claude-code#771 (Anthropic's
/// upstream bug, plus the OSS-convergent default-deny-stdin pattern)
/// could resurface.
///
/// <para>
/// The previous integration tests in
/// <see cref="CliSpawnIntegrationTests"/> spawn from a <c>dotnet test</c>
/// process. The hang only reproduced under <c>dotnet run</c> +
/// Kestrel - i.e. under hosting that inherits the IDE's interactive
/// stdin (or whatever non-terminal stdin Kestrel ships with). These
/// hosting tests cover that gap. The class boots the real
/// <c>OrchestratorApi</c> host in-process via the test factory, then
/// resolves <see cref="ClaudeCliService"/> from DI and drives a real
/// claude run through it. If the hang regresses (e.g. someone toggles
/// <c>RedirectStandardInput</c> back to unconditional <c>true</c>), this
/// test goes from green to a watchdog kill within 60 s.
/// </para>
/// <para>
/// Like all live-CLI tests it is gated behind <c>RUN_CLI_INTEGRATION=1</c>
/// so default <c>dotnet test</c> does not burn quota.
/// </para>
/// </summary>
[Collection("LiveCli")]
public class CliKestrelHostingRepoTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CliKestrelHostingRepoTests(WebApplicationFactory<Program> factory) { _factory = factory; }

    private static string? ClaudeExePath
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("CLAUDE_EXE");
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidate = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    [Xunit.SkippableFact]
    public async Task HostedClaudeCliService_StartAsync_StreamsPastInit()
    {
        Skip.IfNot(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RUN_CLI_INTEGRATION")),
            "Integration test, opt-in via RUN_CLI_INTEGRATION=1.");
        var exe = ClaudeExePath;
        Skip.IfNot(exe != null, "claude.exe not found; set CLAUDE_EXE or install via npm.");

        // Boot the real host. The factory wires up DI exactly as the
        // running backend would, including Kestrel hosting characteristics.
        // We override the Claude CLI path so the test does not depend on
        // the host's ambient appsettings.
        using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ClaudeCli:Path"] = exe
                });
            });
        });
        // CreateClient triggers host startup; we don't actually use the HTTP client.
        _ = factory.CreateClient();

        var svc = factory.Services.GetRequiredService<ClaudeCliService>();

        var jobId = $"hosting-it-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";
        var (exec, err) = await svc.StartAsync(
            jobId, jobKey,
            prompt: "Reply with exactly four words and nothing else: hosted test ready ack",
            workingDirectory: Path.GetTempPath(),
            sessionName: null, resumeSession: false,
            model: "claude-haiku-4-5");

        Assert.Null(err);
        Assert.NotNull(exec);

        // Wait up to 60 s for at least one model frame past Session init.
        // If ADR-0014's stdin default-deny is intact, this completes in
        // single-digit seconds. If it regresses to the pre-fix code path,
        // the synthetic exit line fires only after the watchdog kills the
        // hung run - long enough to fail this assertion.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = svc.GetOutput(jobKey);
            var modelLines = snapshot.Count(l => l.Stream == "stdout"
                && !l.Text.Contains("Session init"));
            if (modelLines >= 1) break;
            await Task.Delay(250);
        }
        svc.Stop(jobKey);

        var final = svc.GetOutput(jobKey);
        Assert.Contains(final, l => l.Stream == "stdout" && l.Text.Contains("Session init"));
        Assert.True(
            final.Count(l => l.Stream == "stdout") >= 2,
            "Hosted claude run produced only the Session init line within 60s. " +
            "claude-code#771 / ADR-0014 regression: stdin default-deny may have " +
            "been undone. Output:\n" +
            string.Join("\n", final.Select(l => $"  [{l.Stream}] {l.Text}")));
    }

    /// <summary>
    /// Pins the R5 (Win32 handle-scrub) spawn path. With
    /// <c>ClaudeCli:UseHandleScrub=true</c>, claude is spawned via
    /// <see cref="OrchestratorApi.Services.Cli.Win.WindowsHandleScrubSpawner"/>
    /// (CreateProcessW + STARTUPINFOEX + curated handle list +
    /// \\.\NUL-as-stdin when no payload). This test exists because
    /// commit c5cfc63's first scrub wiring broke init-frame delivery
    /// (hStdInput=NULL gave the child INVALID_HANDLE_VALUE under
    /// STARTF_USESTDHANDLES); the v2 path opens \\.\NUL explicitly.
    ///
    /// <para>
    /// If this test goes red, the scrub path has regressed and must
    /// not ship - flip <c>ClaudeCli:UseHandleScrub</c> off in
    /// production until the cause is found. Today the path is OFF by
    /// default; this test runs only under
    /// <c>RUN_CLI_INTEGRATION=1</c>.
    /// </para>
    /// </summary>
    [Xunit.SkippableFact]
    public async Task HostedClaudeCliService_HandleScrubFlag_StreamsPastInit()
    {
        Skip.IfNot(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RUN_CLI_INTEGRATION")),
            "Integration test, opt-in via RUN_CLI_INTEGRATION=1.");
        var exe = ClaudeExePath;
        Skip.IfNot(exe != null, "claude.exe not found.");
        Skip.IfNot(OperatingSystem.IsWindows(), "Handle scrub is Windows-specific.");

        using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ClaudeCli:Path"] = exe,
                    ["ClaudeCli:UseHandleScrub"] = "true"
                });
            });
        });
        _ = factory.CreateClient();

        var svc = factory.Services.GetRequiredService<ClaudeCliService>();
        var jobId = $"hosting-scrub-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";
        var (exec, err) = await svc.StartAsync(
            jobId, jobKey,
            prompt: "Reply with exactly four words and nothing else: scrub test ready ack",
            workingDirectory: Path.GetTempPath(),
            sessionName: null, resumeSession: false,
            model: "claude-haiku-4-5");
        Assert.Null(err);
        Assert.NotNull(exec);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = svc.GetOutput(jobKey);
            var initLines = snapshot.Count(l => l.Stream == "stdout" && l.Text.Contains("Session init"));
            var modelLines = snapshot.Count(l => l.Stream == "stdout" && !l.Text.Contains("Session init"));
            if (initLines >= 1 && modelLines >= 1) break;
            await Task.Delay(250);
        }
        svc.Stop(jobKey);

        var final = svc.GetOutput(jobKey);
        Assert.Contains(final, l => l.Stream == "stdout" && l.Text.Contains("Session init"));
        Assert.True(
            final.Count(l => l.Stream == "stdout") >= 2,
            "Handle-scrub spawn (CreateProcessW + STARTUPINFOEX) failed to deliver " +
            "post-init stdout frames within 60s. Likely cause: hStdInput / stdin " +
            "handling regression when wantStdin=false. Output:\n" +
            string.Join("\n", final.Select(l => $"  [{l.Stream}] {l.Text}")));
    }
}
