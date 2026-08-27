using AgentStudio.Cli;
using AgentStudio.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliRepairServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "local-cli-repair-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Classifier_distinguishes_missing_shim_from_truly_uninstalled()
    {
        var npmBin = Path.Combine(_root, "npm");
        var package = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code");

        var missingShim = LocalCliRepairService.ClassifyInstall(
            CliTypes.Claude,
            npmBin,
            _ => false,
            path => path == package);
        var uninstalled = LocalCliRepairService.ClassifyInstall(
            CliTypes.Claude,
            npmBin,
            _ => false,
            _ => false);

        Assert.Equal(LocalCliInstallStates.MissingShimPackagePresent, missingShim.InstallState);
        Assert.Equal("@anthropic-ai/claude-code", missingShim.PackageName);
        Assert.Equal(LocalCliInstallStates.TrulyUninstalled, uninstalled.InstallState);
    }

    [Fact]
    public void Classifier_supports_codex_and_requires_the_invokable_cmd_shim()
    {
        var npmBin = Path.Combine(_root, "npm");
        var expected = Path.Combine(npmBin, "codex.cmd");

        var result = LocalCliRepairService.ClassifyInstall(
            CliTypes.Codex,
            npmBin,
            path => path == expected,
            _ => true);

        Assert.Equal(LocalCliInstallStates.Available, result.InstallState);
        Assert.Equal("@openai/codex", result.PackageName);

        var partial = LocalCliRepairService.ClassifyInstall(
            CliTypes.Codex,
            npmBin,
            path => path == Path.Combine(npmBin, "codex"),
            _ => true);
        Assert.Equal(LocalCliInstallStates.MissingShimPackagePresent, partial.InstallState);
    }

    [Fact]
    public void Hourly_bound_allows_first_and_exactly_one_hour_later()
    {
        var first = new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

        Assert.True(LocalCliRepairService.ShouldAttemptRepair(first, null, TimeSpan.FromHours(1)));
        Assert.False(LocalCliRepairService.ShouldAttemptRepair(first.AddMinutes(59), first, TimeSpan.FromHours(1)));
        Assert.True(LocalCliRepairService.ShouldAttemptRepair(first.AddHours(1), first, TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task Package_present_repair_journals_versions_and_suppresses_second_attempt()
    {
        var npmBin = Path.Combine(_root, "npm");
        var package = Path.Combine(npmBin, "node_modules", "@openai", "codex");
        Directory.CreateDirectory(package);
        await File.WriteAllTextAsync(Path.Combine(package, "package.json"), "{\"version\":\"2.1.231\"}");
        var now = new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);
        var installed = false;
        var installCalls = 0;
        var service = BuildService(
            npmBin,
            () => now,
            (_, _, _) =>
            {
                installCalls++;
                installed = true;
                return Task.FromResult(new LocalCliRepairProcessResult(0, "installed", string.Empty));
            });

        var repaired = await service.TryRepairMissingShimAsync(
            CliTypes.Codex,
            () => installed ? (true, "2.1.234", "codex.cmd") : (false, null, "codex"),
            CancellationToken.None);

        installed = false;
        var throttled = await service.TryRepairMissingShimAsync(
            CliTypes.Codex,
            () => (false, null, "codex"),
            CancellationToken.None);

        Assert.Equal(LocalCliRepairOutcomes.Repaired, repaired.Outcome);
        Assert.Equal("2.1.231", repaired.Event?.CliVersionBefore);
        Assert.Equal("2.1.234", repaired.Event?.CliVersionAfter);

        // Recreate the service to prove the one-hour bound is recovered from
        // the JSONL journal rather than held only in process memory.
        var restarted = BuildService(
            npmBin,
            () => now,
            (_, _, _) =>
            {
                installCalls++;
                return Task.FromResult(new LocalCliRepairProcessResult(0, string.Empty, string.Empty));
            });
        throttled = await restarted.TryRepairMissingShimAsync(
            CliTypes.Codex,
            () => (false, null, "codex"),
            CancellationToken.None);

        Assert.Equal(LocalCliRepairOutcomes.RateLimited, throttled.Outcome);
        Assert.Equal(1, installCalls);
        var journal = await File.ReadAllTextAsync(service.JournalPath);
        Assert.Contains("\"outcome\":\"repaired\"", journal);
        Assert.Contains("\"cliVersionBefore\":\"2.1.231\"", journal);
        Assert.Contains("\"cliVersionAfter\":\"2.1.234\"", journal);
    }

    private LocalCliRepairService BuildService(
        string npmBin,
        Func<DateTimeOffset> clock,
        Func<string, string, CancellationToken, Task<LocalCliRepairProcessResult>> installer)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["CliRepair:NpmBin"] = npmBin,
        }).Build();
        return new LocalCliRepairService(
            config,
            NullLogger<LocalCliRepairService>.Instance,
            new JsonlAppender(),
            clock,
            installer,
            isWindows: true);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* test cleanup */ }
    }
}
