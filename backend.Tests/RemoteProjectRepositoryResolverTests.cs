using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteProjectRepositoryResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "remote-project-repo-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Configured_integration_branch_wins_over_repository_origin_head()
    {
        var git = Path.Combine(_root, ".git");
        Directory.CreateDirectory(Path.Combine(git, "refs", "remotes", "origin"));
        File.WriteAllText(Path.Combine(git, "config"), """
            [core]
                repositoryformatversion = 0
            [remote "origin"]
                url = git@github.com:agent-orc/quality-studio.git
                fetch = +refs/heads/*:refs/remotes/origin/*
            """);
        File.WriteAllText(
            Path.Combine(git, "refs", "remotes", "origin", "HEAD"),
            "ref: refs/remotes/origin/main\n");

        var result = RemoteProjectRepositoryResolver.Resolve(new ProjectRecord
        {
            Id = "PROJ-007",
            DisplayName = "Agent Taskboard",
            StorageLocation = Path.Combine(_root, "tasks"),
            RepositoryPath = _root,
            Urls =
            [
                new ProjectUrlRecord
                {
                    Id = "repo",
                    Label = "Repository",
                    Url = "https://github.com/agent-orc/quality-studio.git",
                },
            ],
        }, "develop");

        Assert.NotNull(result);
        Assert.Equal("PROJ-007", result!.ProjectId);
        Assert.Equal(
            AgentStudio.TaskServer.Contracts.RepositoryIdentityContract.FromUrl(
                "https://github.com/agent-orc/quality-studio.git"),
            result.RepositoryId);
        Assert.Equal("https://github.com/agent-orc/quality-studio.git", result.RepositoryUrl);
        // origin/HEAD of the registered checkout says main, but the configured
        // integration branch is authoritative for where cards are cut from.
        Assert.Equal("develop", result.DefaultBranch);
        Assert.Equal("registry-url", result.Source);
    }

    [Fact]
    public void Repository_origin_head_is_the_fallback_when_no_branch_is_configured()
    {
        var git = Path.Combine(_root, ".git");
        Directory.CreateDirectory(Path.Combine(git, "refs", "remotes", "origin"));
        File.WriteAllText(
            Path.Combine(git, "refs", "remotes", "origin", "HEAD"),
            "ref: refs/remotes/origin/trunk\n");

        var result = RemoteProjectRepositoryResolver.Resolve(new ProjectRecord
        {
            Id = "PROJ-007",
            DisplayName = "Agent Taskboard",
            StorageLocation = Path.Combine(_root, "tasks"),
            RepositoryPath = _root,
            Urls =
            [
                new ProjectUrlRecord
                {
                    Id = "repo",
                    Label = "Repository",
                    Url = "https://github.com/agent-orc/quality-studio.git",
                },
            ],
        }, configuredDefaultBranch: null);

        Assert.NotNull(result);
        Assert.Equal("trunk", result!.DefaultBranch);
    }

    [Fact]
    public void Repository_path_origin_is_not_used_when_registry_url_is_missing()
    {
        var git = Path.Combine(_root, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "config"), """
            [remote "origin"]
                url = git@github.com-agentstudio:agent-orc/agent-studio.git
            """);

        var result = RemoteProjectRepositoryResolver.Resolve(new ProjectRecord
        {
            Id = "PROJ-016",
            DisplayName = "Quality Studio",
            StorageLocation = Path.Combine(_root, "tasks"),
            RepositoryPath = _root,
        }, "main");

        Assert.Null(result);
    }

    [Fact]
    public void Qs_registry_url_derives_materializable_identity_without_repository_path()
    {
        const string repositoryUrl = "https://github.com/example/quality-studio.git/";
        var result = RemoteProjectRepositoryResolver.Resolve(new ProjectRecord
        {
            Id = "PROJ-016",
            DisplayName = "Quality Studio",
            StorageLocation = Path.Combine(_root, "quality-studio-tasks"),
            Urls =
            [
                new ProjectUrlRecord
                {
                    Id = "repo",
                    Label = "Repository",
                    Url = repositoryUrl,
                },
            ],
        }, "main");

        Assert.NotNull(result);
        Assert.Equal("PROJ-016", result!.ProjectId);
        Assert.Equal(
            AgentStudio.TaskServer.Contracts.RepositoryIdentityContract.FromUrl(repositoryUrl),
            result.RepositoryId);
        Assert.StartsWith("repo_", result.RepositoryId, StringComparison.Ordinal);
        Assert.Equal(repositoryUrl, result.RepositoryUrl);
        Assert.Equal("registry-url", result.Source);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}
