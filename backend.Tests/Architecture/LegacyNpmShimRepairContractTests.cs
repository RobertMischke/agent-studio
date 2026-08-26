using System.Runtime.CompilerServices;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins local npm-shim repair to one bounded coordinator. Capability probes,
/// legacy launches, and one-shot launches may call that coordinator, while the
/// low-level historical healer is no longer invoked directly.
/// </summary>
public class LegacyNpmShimRepairContractTests
{
    [Fact]
    public void Repair_is_wired_through_the_shared_local_coordinator()
    {
        var root = RepoRoot();
        var coordinator = Source(root, "backend/Features/Cli/Execution/LocalCliRepairService.cs");
        var legacy = Source(root, "backend/Features/Cli/Execution/BuiltInCliBehaviors.cs");
        var engine = Source(root, "backend/Features/Cli/Execution/CliExecutionServiceBase.cs");
        var oneShot = Source(root, "backend/Features/Cli/Routing/OneShot/ClaudeOneShot.cs");
        var endpoints = Source(root, "backend/Features/Cli/CliEndpoints.cs");
        var runner = Source(root, "backend/Features/Runner/TaskRunnerService.cs");

        Assert.Contains("MissingShimWithPackage", coordinator, StringComparison.Ordinal);
        Assert.Contains("RepairCooldown = TimeSpan.FromHours(1)", coordinator, StringComparison.Ordinal);
        Assert.Contains("cli-repairs.jsonl", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("NpmShimHealer.TryHealClaudeAsync", legacy, StringComparison.Ordinal);
        Assert.Contains("_localCliRepair.EnsureAvailableAsync", engine, StringComparison.Ordinal);
        Assert.Contains("_localCliRepair.EnsureAvailableAsync", oneShot, StringComparison.Ordinal);
        Assert.Contains("repairs.ProbeConfiguredAsync", endpoints, StringComparison.Ordinal);
        Assert.Contains("EnsureCliHealthyAsync(stoppingToken)", runner, StringComparison.Ordinal);
        Assert.Equal(2, Count(runner, "await cli.EnsureCliHealthyAsync(ct)"));
    }

    private static string Source(string root, string relativePath)
        => File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static int Count(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var current = Path.GetDirectoryName(sourceFile);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
            current = Path.GetDirectoryName(current);
        }

        throw new InvalidOperationException("agent-taskboard.sln not found above test source.");
    }
}
