using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Prevents the completed 2026-07-28 five-card repair from returning to the
/// permanent startup path. Accepted integration recovery is now generic and
/// archive-inclusive; incident-specific task keys must not become runtime
/// configuration by accident.
/// </summary>
public sealed class LegacyIntegrationBackfillRemovalTests
{
    [Fact]
    public void BackendStartup_HasNoIncidentSpecificRemoteDeliveryBackfill()
    {
        var root = RepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "backend", "Host", "Program.cs"));
        var legacyService = Path.Combine(
            root,
            "backend",
            "Features",
            "Tasks",
            "RemoteDeliveryBackfillService.cs");

        Assert.False(File.Exists(legacyService));
        Assert.DoesNotContain("RemoteDeliveryBackfill", program, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "agent-taskboard.sln")))
                return current;
            current = Path.GetDirectoryName(current);
        }

        throw new InvalidOperationException(
            "agent-taskboard.sln not found above the test base directory.");
    }
}
