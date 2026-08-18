using System.Reflection;
using Xunit;

namespace AgentStudio.DemoReplayRunner.Tests;

/// <summary>
/// Structural coverage for the claim-free service surface. The slice promises
/// that no code path in this image can claim, lease, or complete work; these
/// tests assert that from the shipped assembly rather than from a comment.
/// </summary>
public sealed class ReplayServiceSurfaceTests
{
    private static readonly string[] ExecutionVerbs =
        ["claim", "lease", "completion", "complete", "artifact", "permit", "handoff", "reissue", "steer"];

    [Fact]
    public void The_server_client_exposes_nothing_but_the_replay_post_and_the_health_probe()
    {
        var methods = typeof(ReplayClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Where(name => name != nameof(IDisposable.Dispose))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal([nameof(ReplayClient.PostFrameAsync), nameof(ReplayClient.ProbeHealthAsync)], methods);
    }

    [Fact]
    public void No_public_member_in_the_service_advertises_an_execution_capability()
    {
        var offenders = typeof(ReplayClient).Assembly
            .GetTypes()
            .SelectMany(type => type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(member => $"{type.Name}.{member.Name}"))
            .Where(name => ExecutionVerbs.Any(verb => name.Contains(verb, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_service_does_not_reference_the_runner_the_backend_or_a_cli_host()
    {
        var forbidden = new[] { "AgentRunner", "OrchestratorApi", "CodingAgentRunner", "AgentStudio.CliHosting", "LibGit2Sharp" };

        var referenced = typeof(ReplayClient).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? "")
            .ToList();

        Assert.DoesNotContain(referenced, name => forbidden.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_allowed_path_list_is_exactly_the_replay_ingest_and_the_health_probe()
    {
        Assert.Equal(
            ["/api/runner/replay/events", "/healthz"],
            ReplayEgressLock.AllowedPaths.OrderBy(path => path, StringComparer.Ordinal));
    }
}
