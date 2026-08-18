using System.Text.Json;
using AgentStudio.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-W34 slice S4: what a public-demo visitor may learn about a project.
/// Storage locations, repository paths, roots, remotes, and watched URLs
/// describe the machine the demo happens to run on, not the demo content, and
/// the dossier's scrub contract names absolute paths and repository remotes
/// explicitly. The wire shape stays identical so no client has to know.
/// </summary>
public sealed class PublicDemoProjectExposureTests
{
    private static ProjectSummary Sample() => new()
    {
        Id = "demo-app",
        DisplayName = "Demo App",
        ShortCode = "DEMO",
        WorkspaceId = "demo",
        SortOrder = 1,
        Color = "#4f46e5",
        StorageLocation = "/srv/agent-studio-demo/projects/demo-app",
        RepositoryPath = "/srv/repos/demo-app",
        RootPath = "/srv/repos",
        RepositoryUrl = "git@internal.example.org:team/demo-app.git",
        Urls = [new ProjectUrlRecord { Id = "repo", Url = "https://internal.example.org/demo-app" }],
        OwnershipMappings = [new ComponentOwnershipMapping
        {
            Id = "api",
            Component = "api",
            Repository = "git@internal.example.org:team/demo-app.git",
            IntegrationHosts = ["build-01.internal.example.org"],
        }],
    };

    [Fact]
    public void HostRevealingFields_AreCleared()
    {
        var redacted = Sample().WithoutHostDetail();

        Assert.Equal(string.Empty, redacted.StorageLocation);
        Assert.Null(redacted.RepositoryPath);
        Assert.Null(redacted.RootPath);
        Assert.Null(redacted.RepositoryUrl);
        Assert.Empty(redacted.Urls);
        Assert.Empty(redacted.OwnershipMappings);
    }

    [Fact]
    public void EverythingTheBoardNeeds_Survives()
    {
        var redacted = Sample().WithoutHostDetail();

        Assert.Equal("demo-app", redacted.Id);
        Assert.Equal("Demo App", redacted.DisplayName);
        Assert.Equal("DEMO", redacted.ShortCode);
        Assert.Equal("demo", redacted.WorkspaceId);
        Assert.Equal("#4f46e5", redacted.Color);
        Assert.Equal(1, redacted.SortOrder);
    }

    /// <summary>
    /// A store that drifted or was mis-seeded must not be able to announce a
    /// project the demo never claimed - the same filter the hub applies.
    /// </summary>
    [Fact]
    public void OnlyAnnouncedProjects_SurviveTheFilter()
    {
        string[] announced = ["demo-app", "demo-platform"];
        string[] present = ["demo-app", "agent-taskboard", "demo-platform", "runbook"];

        Assert.Equal(
            ["demo-app", "demo-platform"],
            PublicDemoProjectScope.Filter(announced, present));
    }

    /// <summary>
    /// <c>/api/search</c> filters the same way <c>/api/projects</c> and the hub
    /// do (dossier §6, "Apply the same project filter to REST, search, and
    /// SignalR"). A search item's <c>ProjectName</c> can be a display name or a
    /// watch-path folder name rather than the registry id, so the endpoint
    /// resolves it through <see cref="ProjectRegistry.FindByIdOrDisplayName"/>
    /// first, exactly like <see cref="ProjectAccessAuthorization"/> and the hub's
    /// <c>ProjectGroup</c> do. This proves that resolution step, not the wiring
    /// through the minimal-API handler.
    /// </summary>
    [Fact]
    public void SearchItems_ResolveToTheRegistryIdBeforeTheScopeCheck()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "studio-public-demo-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RegistryPaths.MetadataDir(workspace));
        var projectsFile = new ProjectsFile
        {
            NextProjectIdSeq = 3,
            Projects =
            [
                new ProjectRecord { Id = "PROJ-001", DisplayName = "demo-app", WorkspaceId = "ws-default", ShortCode = "DEMO" },
                new ProjectRecord { Id = "PROJ-002", DisplayName = "agent-taskboard", WorkspaceId = "ws-default", ShortCode = "ATB" },
            ],
        };
        File.WriteAllText(RegistryPaths.ProjectsFilePath(workspace), JsonSerializer.Serialize(projectsFile));

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = workspace })
                .Build();
            var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
            string[] announced = ["PROJ-001"];

            // A demo item reported under its display name resolves to the
            // announced registry id and survives the filter.
            Assert.True(PublicDemoProjectScope.Allows(
                announced, registry.FindByIdOrDisplayName("demo-app")?.Id ?? "demo-app"));

            // A non-demo item resolves to an id that was never announced.
            Assert.False(PublicDemoProjectScope.Allows(
                announced, registry.FindByIdOrDisplayName("agent-taskboard")?.Id ?? "agent-taskboard"));

            // An unresolvable handle (e.g. a watch-path folder name with no
            // registry entry) falls back to the raw string, which the scope
            // check still fails closed on unless it happens to match.
            Assert.False(PublicDemoProjectScope.Allows(
                announced, registry.FindByIdOrDisplayName("unregistered-folder")?.Id ?? "unregistered-folder"));
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }
}
