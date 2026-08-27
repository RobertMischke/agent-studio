using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliSelfHealTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-cli-shim-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Inspect_distinguishes_missing_shim_from_truly_uninstalled()
    {
        var claudePackage = Path.Combine(_root, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(claudePackage);
        File.WriteAllText(
            Path.Combine(claudePackage, "package.json"),
            JsonSerializer.Serialize(new { name = "@anthropic-ai/claude-code", version = "2.1.234" }));

        var missingShim = NpmCliShimInspection.Inspect(CliTypes.Claude, _root);
        var uninstalled = NpmCliShimInspection.Inspect(CliTypes.Codex, _root);

        Assert.True(missingShim.IsMissingShimWithPackagePresent);
        Assert.Equal("2.1.234", missingShim.PackageVersion);
        Assert.Contains("claude.cmd", missingShim.MissingShims);
        Assert.False(uninstalled.PackagePresent);
        Assert.False(uninstalled.IsMissingShimWithPackagePresent);
    }

    [Fact]
    public void Inspect_requires_the_windows_cmd_shim_even_when_the_shell_shim_survives()
    {
        var codexPackage = Path.Combine(_root, "node_modules", "@openai", "codex");
        Directory.CreateDirectory(codexPackage);
        File.WriteAllText(Path.Combine(codexPackage, "package.json"), "{\"version\":\"0.200.0\"}");
        File.WriteAllText(Path.Combine(_root, "codex"), "#!/bin/sh");

        var inspection = NpmCliShimInspection.Inspect(CliTypes.Codex, _root);

        Assert.True(inspection.IsMissingShimWithPackagePresent);
        Assert.Contains("codex.cmd", inspection.MissingShims);
    }

    [Fact]
    public void Inspect_treats_present_cmd_shim_as_installed()
    {
        var package = Path.Combine(_root, "node_modules", "@openai", "codex");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"version\":\"0.200.0\"}");
        File.WriteAllText(Path.Combine(_root, "codex.cmd"), "@echo off");

        var inspection = NpmCliShimInspection.Inspect(CliTypes.Codex, _root);

        Assert.True(inspection.PackagePresent);
        Assert.True(inspection.InvocableShimPresent);
        Assert.False(inspection.IsMissingShimWithPackagePresent);
    }

    [Fact]
    public void Attempt_policy_allows_only_one_attempt_per_hour()
    {
        var first = DateTimeOffset.Parse("2026-08-18T09:00:00Z");

        Assert.False(NpmCliShimInspection.CanAttempt(first, first.AddMinutes(59)));
        Assert.True(NpmCliShimInspection.CanAttempt(first, first.AddHours(1)));
        Assert.True(NpmCliShimInspection.CanAttempt(null, first));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
