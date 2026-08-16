using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// W34 S4 project-filtered SignalR. A public visitor's connection joins the
/// seeded demo projects and nothing else: no unscoped group, and no group for a
/// project the read allowlist would refuse. The same filter therefore governs
/// REST reads and live events.
/// </summary>
public sealed class PublicDemoHubScopeTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "studio-public-demo-hub-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_public_visitor_joins_only_the_seeded_demo_projects()
    {
        var (hub, groups) = BuildHub(PublicDemoProfile.ProfileName);

        await hub.OnConnectedAsync();

        Assert.Contains("project:proj-001", groups.Joined);
        Assert.Contains("project:proj-002", groups.Joined);
        Assert.DoesNotContain("project:proj-003", groups.Joined);
        Assert.DoesNotContain(TaskHub.UnscopedSecurityGroup, groups.Joined);
    }

    [Fact]
    public async Task The_local_profile_keeps_its_wide_fan_out()
    {
        var (hub, groups) = BuildHub(SecurityProfiles.Local);

        await hub.OnConnectedAsync();

        Assert.Contains("project:proj-003", groups.Joined);
        Assert.Contains(TaskHub.UnscopedSecurityGroup, groups.Joined);
    }

    [Fact]
    public void The_scope_resolves_a_demo_handle_however_it_is_written()
    {
        var scope = BuildScope();

        Assert.True(scope.Allows("demo-app"));
        Assert.True(scope.Allows("PROJ-001"));
        Assert.True(scope.Allows("Demo App"));
        Assert.False(scope.Allows("Production App"));
        Assert.False(scope.Allows(null));
    }

    private PublicDemoProjectScope BuildScope()
    {
        var contract = PublicDemoStartup.BuildContract(Configuration(PublicDemoProfile.ProfileName));
        return new PublicDemoProjectScope(contract, Registry());
    }

    private (TaskHub Hub, RecordingGroupManager Groups) BuildHub(string profile)
    {
        var configuration = Configuration(profile);
        var registry = Registry();
        var contract = PublicDemoStartup.BuildContract(configuration);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration);
        var scanner = new TaskScannerService(
            configuration, NullLogger<TaskScannerService>.Instance, summary, projectRegistry: registry);
        var hub = new TaskHub(
            configuration,
            new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance),
            scanner,
            registry,
            new PublicDemoProjectScope(contract, registry))
        {
            Context = new FakeHubCallerContext(),
        };

        var groups = new RecordingGroupManager();
        hub.Groups = groups;
        return (hub, groups);
    }

    private IConfiguration Configuration(string profile) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["Security:Profile"] = profile,
        })
        .Build();

    /// <summary>Two seeded demo projects plus one that must stay outside the scene.</summary>
    private AgentStudio.Registry.ProjectRegistry Registry()
    {
        var metadata = Path.Combine(_workspace, ".metadata");
        Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(metadata, "workspaces.json"), """
        { "schemaVersion": 1, "workspaces": [ { "id": "WS-001", "displayName": "Demo", "sortOrder": 1 } ] }
        """);
        File.WriteAllText(Path.Combine(metadata, "projects.json"), """
        {
          "schemaVersion": 1,
          "projects": [
            { "id": "PROJ-001", "displayName": "Demo App", "shortCode": "DEMO", "workspaceId": "WS-001", "sortOrder": 1, "storageLocation": "/demo/projects/demo-app" },
            { "id": "PROJ-002", "displayName": "Demo Platform", "shortCode": "PLAT", "workspaceId": "WS-001", "sortOrder": 2, "storageLocation": "/demo/projects/demo-platform" },
            { "id": "PROJ-003", "displayName": "Production App", "shortCode": "PROD", "workspaceId": "WS-001", "sortOrder": 3, "storageLocation": "/demo/projects/production-app" }
          ]
        }
        """);

        var configuration = Configuration(SecurityProfiles.Local);
        return new AgentStudio.Registry.ProjectRegistry(
            configuration,
            NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance);
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<string> Joined { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Joined.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Joined.Remove(groupName);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public override string ConnectionId => "connection-1";
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch (IOException) { }
    }
}
