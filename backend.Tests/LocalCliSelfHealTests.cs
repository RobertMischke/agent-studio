using System.Text.Json;
using AgentStudio.Cli;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class LocalCliSelfHealTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "local-cli-self-heal-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Missing_shims_with_an_existing_package_are_distinct_from_uninstalled()
    {
        var prefix = Path.Combine(_root, "npm");
        var packageRoot = CreatePackage(prefix, "@anthropic-ai/claude-code", "2.1.234");
        var definition = new NpmCliDefinition(
            "claude",
            "Claude CLI",
            "claude",
            "@anthropic-ai/claude-code");

        var installed = LocalCliSelfHeal.Inspect(definition, [prefix]);
        var uninstalled = LocalCliSelfHeal.Inspect(definition, [Path.Combine(_root, "empty")]);

        Assert.Equal(NpmCliInstallState.MissingShimWithPackage, installed.State);
        Assert.Equal("2.1.234", installed.PackageVersion);
        Assert.Equal(packageRoot, installed.PackageRoot);
        Assert.Empty(installed.ExistingShims);
        Assert.Equal(NpmCliInstallState.TrulyUninstalled, uninstalled.State);
    }

    [Fact]
    public void A_present_shim_is_not_classified_as_the_missing_shim_defect()
    {
        var prefix = Path.Combine(_root, "npm");
        CreatePackage(prefix, "@openai/codex", "1.2.3");
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "codex.cmd"), "shim");

        var inspection = LocalCliSelfHeal.Inspect(
            new NpmCliDefinition("codex", "Codex CLI", "codex", "@openai/codex"),
            [prefix]);

        Assert.Equal(NpmCliInstallState.NonShimFailure, inspection.State);
        Assert.Single(inspection.ExistingShims);
    }

    [Fact]
    public void A_leftover_powershell_shim_does_not_hide_a_missing_command_shim()
    {
        var prefix = Path.Combine(_root, "npm");
        CreatePackage(prefix, "@anthropic-ai/claude-code", "2.1.234");
        File.WriteAllText(Path.Combine(prefix, "claude.ps1"), "shim");

        var inspection = LocalCliSelfHeal.Inspect(
            new NpmCliDefinition(
                "claude",
                "Claude CLI",
                "claude",
                "@anthropic-ai/claude-code"),
            [prefix]);

        Assert.Equal(NpmCliInstallState.MissingShimWithPackage, inspection.State);
        Assert.Equal("claude.ps1", Path.GetFileName(Assert.Single(inspection.ExistingShims)));
    }

    [Fact]
    public async Task Repair_journals_versions_and_surfaces_a_quiet_success_note()
    {
        var prefix = Path.Combine(_root, "npm");
        CreatePackage(prefix, "@anthropic-ai/claude-code", "2.1.234");
        var now = new DateTime(2026, 8, 27, 10, 15, 0, DateTimeKind.Utc);
        var launches = 0;
        var healer = new LocalCliSelfHeal(
            NullLogger<LocalCliSelfHeal>.Instance,
            Path.Combine(_root, "runtime"),
            () => now,
            (_, arguments, _) =>
            {
                launches++;
                Assert.Equal(["install", "-g", "@anthropic-ai/claude-code"], arguments);
                File.WriteAllText(Path.Combine(prefix, "claude.cmd"), "shim");
                return Task.FromResult(new ProcessResult(0, "installed", ""));
            },
            isWindows: true,
            configuredPrefix: prefix);

        var repaired = await healer.TryRepairAsync(
            "claude",
            "claude",
            "2.1.231",
            () => (true, "2.1.234 (Claude Code)", Path.Combine(prefix, "claude.cmd")),
            CancellationToken.None);

        Assert.True(repaired);
        Assert.Equal(1, launches);
        var status = Assert.Single(healer.Snapshot());
        Assert.Equal("repaired", status.State);
        Assert.Equal("CLI repaired at 2026-08-27T10:15:00.0000000Z", status.Message);
        Assert.Equal("2.1.231", status.VersionBefore);
        Assert.Equal("2.1.234 (Claude Code)", status.VersionAfter);

        var journalPath = Path.Combine(_root, "runtime", LocalCliSelfHeal.JournalFileName);
        var entry = File.ReadAllLines(journalPath)
            .Select(line => JsonSerializer.Deserialize<CliRepairJournalEntry>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .Single(item => item!.Outcome == "repaired");
        Assert.NotNull(entry);
        Assert.Equal("missing-shim-with-package", entry.Classification);
        Assert.Equal("2.1.231", entry.CliVersionBefore);
        Assert.Equal("2.1.234", entry.PackageVersionBefore);
        Assert.Equal("2.1.234 (Claude Code)", entry.CliVersionAfter);
        Assert.Equal("repaired", entry.Outcome);
    }

    [Fact]
    public async Task Failed_repair_remains_the_alarm_while_followup_attempts_are_hourly_throttled()
    {
        var prefix = Path.Combine(_root, "npm");
        CreatePackage(prefix, "@openai/codex", "3.4.5");
        var now = new DateTime(2026, 8, 27, 10, 15, 0, DateTimeKind.Utc);
        var launches = 0;
        var healer = new LocalCliSelfHeal(
            NullLogger<LocalCliSelfHeal>.Instance,
            Path.Combine(_root, "runtime"),
            () => now,
            (_, _, _) =>
            {
                launches++;
                return Task.FromResult(new ProcessResult(1, "", "failed"));
            },
            isWindows: true,
            configuredPrefix: prefix);

        Assert.False(await healer.TryRepairAsync(
            "codex", "codex", "3.4.4", () => (false, null, "codex"), CancellationToken.None));
        now = now.AddMinutes(30);
        Assert.False(await healer.TryRepairAsync(
            "codex", "codex", "3.4.4", () => (false, null, "codex"), CancellationToken.None));

        Assert.Equal(1, launches);
        var status = Assert.Single(healer.Snapshot());
        Assert.Equal("repair-failed", status.State);
        Assert.Contains("CLI repair failed at", status.Message, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 8, 27, 11, 15, 0, DateTimeKind.Utc), status.NextAttemptAt);

        var entries = File.ReadAllLines(Path.Combine(
                _root,
                "runtime",
                LocalCliSelfHeal.JournalFileName))
            .Select(line => JsonSerializer.Deserialize<CliRepairJournalEntry>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .ToArray();
        Assert.Equal(
            ["attempt-started", "repair-failed", "throttled"],
            entries.Select(entry => entry!.Outcome));
    }

    [Fact]
    public void Npm_log_summary_captures_update_commands_and_redacts_auth_material()
    {
        var log = Path.Combine(_root, "npm-debug.log");
        Directory.CreateDirectory(_root);
        File.WriteAllLines(log, [
            "0 verbose cli C:\\Program Files\\nodejs\\node.exe",
            "1 verbose title npm install @anthropic-ai/claude-code",
            "2 verbose argv install --global @anthropic-ai/claude-code token=secret-value",
        ]);

        var summary = LocalCliSelfHeal.SummarizeNpmLog(
            log,
            "@anthropic-ai/claude-code");

        Assert.Contains("npm install @anthropic-ai/claude-code", summary, StringComparison.Ordinal);
        Assert.Contains("token=[redacted]", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", summary, StringComparison.Ordinal);
    }

    private string CreatePackage(string prefix, string packageName, string version)
    {
        var packageRoot = Path.Combine(
            prefix,
            "node_modules",
            packageName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(
            Path.Combine(packageRoot, "package.json"),
            JsonSerializer.Serialize(new { name = packageName, version }));
        return packageRoot;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
