using AgentStudio.Registry;
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
}
