using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PtyProbeEnvironmentGuardTests
{
    private static readonly Regex PtySpawn = new(
        @"PtySession\.SpawnAsync\((?<arguments>[\s\S]*?)\);",
        RegexOptions.Compiled);

    [Fact]
    public void Probe_environment_disables_cli_updaters_and_keeps_terminal_settings()
    {
        var environment = CliEnvironment.ProbeEnvironment();

        Assert.Equal("1", environment["CLAUDE_CODE_DISABLE_AUTOUPDATER"]);
        Assert.Equal("1", environment["DISABLE_AUTOUPDATER"]);
        Assert.Equal("xterm-256color", environment["TERM"]);
        Assert.Equal("truecolor", environment["COLORTERM"]);
        Assert.Equal("0", environment["FORCE_COLOR"]);
        Assert.Equal("1", environment["NO_COLOR"]);
    }

    [Fact]
    public void Every_cli_pty_probe_and_discovery_spawn_uses_guarded_environment()
    {
        var cliFeatureRoot = Path.Combine(RepoRoot(), "backend", "Features", "Cli");
        var spawns = Directory.EnumerateFiles(cliFeatureRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => PtySpawn.Matches(File.ReadAllText(file))
                .Select(match => new
                {
                    File = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/'),
                    Arguments = match.Groups["arguments"].Value,
                }))
            .ToArray();

        Assert.Equal(5, spawns.Length);
        Assert.All(spawns, spawn => Assert.Contains(
            "extraEnv: CliEnvironment.ProbeEnvironment()",
            spawn.Arguments,
            StringComparison.Ordinal));
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var current = Path.GetDirectoryName(sourceFile);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
