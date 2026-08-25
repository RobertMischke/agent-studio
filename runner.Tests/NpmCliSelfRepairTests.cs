using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class NpmCliSelfRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-host-cli-repair-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Inspection_distinguishes_missing_shim_with_package_present()
    {
        var prefix = CreatePackage("@anthropic-ai/claude-code", "2.1.234");

        var inspection = NpmCliSelfRepair.Inspect("claude", "claude", prefix);

        Assert.True(inspection.PackagePresent);
        Assert.False(inspection.ShimPresent);
        Assert.True(inspection.MissingShimWithPackagePresent);
        Assert.Equal("2.1.234", inspection.PackageVersion);
        Assert.EndsWith("claude.cmd", inspection.ExpectedShim, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspection_distinguishes_truly_uninstalled_package()
    {
        var prefix = Path.Combine(_root, "npm");
        Directory.CreateDirectory(prefix);

        var inspection = NpmCliSelfRepair.Inspect("codex", "codex", prefix);

        Assert.False(inspection.PackagePresent);
        Assert.False(inspection.MissingShimWithPackagePresent);
        Assert.Contains(Path.Combine("@openai", "codex"), inspection.PackageDirectory);
    }

    [Fact]
    public async Task Successful_repair_journals_versions_and_publishes_ready_note()
    {
        var prefix = CreatePackage("@anthropic-ai/claude-code", "2.1.231");
        var appData = Directory.GetParent(prefix)!.FullName;
        var localAppData = Path.Combine(_root, "localappdata");
        var npmLogs = Path.Combine(localAppData, "npm-cache", "_logs");
        Directory.CreateDirectory(npmLogs);
        File.WriteAllText(
            Path.Combine(npmLogs, "2026-08-25-debug.log"),
            "verbose title npm update @anthropic-ai/claude-code\n_authToken=secret-value");
        var shim = Path.Combine(prefix, "claude.cmd");
        var launches = new List<IReadOnlyList<string>>();
        Task<ProcessResult> Launch(string _, IReadOnlyList<string> arguments, CancellationToken __)
        {
            launches.Add(arguments);
            if (arguments.SequenceEqual(["install", "--global", "@anthropic-ai/claude-code"]))
            {
                File.WriteAllText(shim, "@echo off");
                File.WriteAllText(
                    Path.Combine(prefix, "node_modules", "@anthropic-ai", "claude-code", "package.json"),
                    "{\"version\":\"2.1.234\"}");
                return Task.FromResult(new ProcessResult(0, "updated 1 package", ""));
            }
            return Task.FromResult(new ProcessResult(0, "2.1.234 (Claude Code)", ""));
        }
        var state = Path.Combine(_root, "state");
        var repair = new NpmCliSelfRepair(
            state,
            _ => { },
            clock: () => new DateTimeOffset(2026, 8, 25, 10, 15, 0, TimeSpan.Zero),
            launcher: Launch,
            executableExists: _ => File.Exists(shim),
            isWindows: () => true,
            environment: name => name switch
            {
                "APPDATA" => appData,
                "LOCALAPPDATA" => localAppData,
                _ => null,
            });

        var repaired = await repair.ProbeAsync(
            [("claude", "claude")],
            CancellationToken.None);

        Assert.Equal(["claude"], repaired);
        Assert.Equal(2, launches.Count);
        Assert.Contains("version before 2.1.231, after 2.1.234 (Claude Code)",
            repair.CapabilityDetails["claude"]);
        var journal = await File.ReadAllTextAsync(
            Path.Combine(_root, "state", "cli-repairs.jsonl"));
        Assert.Contains("\"outcome\":\"repaired\"", journal);
        Assert.Contains("\"versionBefore\":\"2.1.231\"", journal);
        Assert.Contains("\"versionAfter\":\"2.1.234 (Claude Code)\"", journal);
        Assert.Contains("npm update @anthropic-ai/claude-code", journal);
        Assert.Contains("_authToken=[redacted]", journal);
        Assert.DoesNotContain("secret-value", journal);
    }

    [Fact]
    public async Task Failed_repair_is_limited_to_one_attempt_per_hour()
    {
        var prefix = CreatePackage("@openai/codex", "0.90.0");
        var appData = Directory.GetParent(prefix)!.FullName;
        var now = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        var attempts = 0;
        var state = Path.Combine(_root, "state");
        var repair = new NpmCliSelfRepair(
            state,
            _ => { },
            clock: () => now,
            launcher: (_, _, _) =>
            {
                attempts++;
                return Task.FromResult(new ProcessResult(1, "", "network unavailable"));
            },
            executableExists: _ => false,
            isWindows: () => true,
            environment: name => name == "APPDATA" ? appData : null);

        await repair.ProbeAsync([("codex", "codex")], CancellationToken.None);
        now = now.AddMinutes(59);
        var restartedRepair = new NpmCliSelfRepair(
            state,
            _ => { },
            clock: () => now,
            launcher: (_, _, _) =>
            {
                attempts++;
                return Task.FromResult(new ProcessResult(1, "", "network unavailable"));
            },
            executableExists: _ => false,
            isWindows: () => true,
            environment: name => name == "APPDATA" ? appData : null);
        await restartedRepair.ProbeAsync([("codex", "codex")], CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Contains("next automatic attempt after", restartedRepair.CapabilityDetails["codex"]);
        Assert.Contains("network unavailable", restartedRepair.CapabilityDetails["codex"]);
    }

    private string CreatePackage(string packageName, string version)
    {
        var prefix = Path.Combine(_root, "appdata", "npm");
        var packageDirectory = Path.Combine(
            new[] { prefix, "node_modules" }.Concat(packageName.Split('/')).ToArray());
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(
            Path.Combine(packageDirectory, "package.json"),
            $"{{\"version\":\"{version}\"}}");
        return prefix;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
