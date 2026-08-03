using System.Runtime.CompilerServices;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the temporary npm-shim repair ownership during the CAR rollout. CAR
/// owns repair for CAR-backed runs; only the explicit legacy rollback and the
/// non-agent Claude one-shot may retain the Studio helper until AGT-2373.
/// </summary>
public class LegacyNpmShimRepairContractTests
{
    [Fact]
    public void Repair_helper_is_wired_only_to_legacy_and_one_shot_paths()
    {
        var root = RepoRoot();
        var helper = Source(root, "backend/Features/Cli/Execution/NpmShimHealer.cs");
        var legacy = Source(root, "backend/Features/Cli/Execution/BuiltInCliBehaviors.cs");
        var oneShot = Source(root, "backend/Features/Cli/Routing/OneShot/ClaudeOneShot.cs");
        var car = Source(root, "backend/Features/Cli/Execution/BackendCarExecution.cs");

        Assert.Contains("TryHealClaudeAsync", helper, StringComparison.Ordinal);
        Assert.Equal(1, Count(legacy, "NpmShimHealer.TryHealClaudeAsync"));
        Assert.Equal(1, Count(oneShot, "NpmShimHealer.TryHealClaudeAsync"));
        Assert.DoesNotContain("NpmShimHealer", car, StringComparison.Ordinal);
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
