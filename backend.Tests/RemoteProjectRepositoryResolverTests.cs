using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteProjectRepositoryResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "remote-project-repo-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Repository_path_derives_origin_and_default_branch()
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
            DisplayName = "Quality Studio",
            StorageLocation = Path.Combine(_root, "tasks"),
            RepositoryPath = _root,
        }, "develop");

        Assert.NotNull(result);
        Assert.Equal("PROJ-007", result!.ProjectId);
        Assert.Equal("git@github.com:agent-orc/quality-studio.git", result.RepositoryUrl);
        Assert.Equal("main", result.DefaultBranch);
        Assert.Equal("repository-path", result.Source);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}
