using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Registry;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

public sealed class CommitDeliveryCoordinateFixtureTests
{
    [Fact]
    public void Agt2307LegacyRecord_ParsesSevenRepositoryPrefixesOnce()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "task-attribution",
            "agt-2307-legacy-commits.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var commits = document.RootElement.EnumerateArray().ToList();

        Assert.Equal(20, commits.Count);
        Assert.All(commits, commit => Assert.False(commit.TryGetProperty("Repository", out _)));
        var repositories = commits
            .GroupBy(commit => TaskMutationService.LegacyRepositoryFromMessage(
                commit.GetProperty("Message").GetString()))
            .ToDictionary(group => group.Key!, group => group.Count());

        Assert.Equal(7, repositories.Count);
        Assert.Equal(5, repositories["agent-studio"]);
        Assert.Equal(4, repositories["runner"]);
        Assert.Equal(4, repositories["token-economy"]);
        Assert.Equal(3, repositories["chat"]);
        Assert.Equal(2, repositories["ai-patterns.dev"]);
        Assert.Equal(1, repositories["quality-studio"]);
        Assert.Equal(1, repositories[".github"]);
        Assert.Null(TaskMutationService.LegacyRepositoryFromMessage("feat: no repository prefix"));
    }

    [Fact]
    public void BackfillCommitDeliveryCoordinates_PersistsLegacyPrefixAndIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "commit-coordinate-" + Guid.NewGuid().ToString("N"));
        var watch = Path.Combine(root, "jobs");
        var taskFolder = Path.Combine(watch, TaskStates.HumanReview, "agt-2307");
        Directory.CreateDirectory(taskFolder);
        try
        {
            File.WriteAllText(Path.Combine(taskFolder, "task.json"), """
            {
              "id": "agt-2307",
              "key": "AGT-2307",
              "title": "Externalization sweep",
              "state": "5-human-review",
              "projectName": "Fixture",
              "integrationBranch": "refs/heads/develop",
              "commits": [
                {
                  "sha": "0000000000000000000000000000000000000001",
                  "shortSha": "0000000",
                  "message": "[runner] delivery 1",
                  "filesChanged": 1,
                  "files": ["runner.cs"],
                  "at": "2026-08-04T08:00:00Z"
                }
              ]
            }
            """);
            File.WriteAllText(Path.Combine(taskFolder, "prompt.md"), "fixture");
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = root,
                    ["WatchPaths:0:Name"] = "Fixture",
                    ["WatchPaths:0:Path"] = watch,
                    ["WatchPaths:0:RootPath"] = watch,
                    ["WatchPaths:0:RepositoryPath"] = watch,
                }).Build();
            var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
            var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
            var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
            var mutations = new TaskMutationService(
                scanner,
                new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
                new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
                new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
                NullLogger<TaskMutationService>.Instance,
                git: git);

            var first = mutations.BackfillCommitDeliveryCoordinates();
            var migrated = scanner.FindJob("agt-2307", watch)!;

            Assert.Equal(1, first.RepairedTasks);
            var commit = Assert.Single(migrated.Commits);
            Assert.Equal("runner", commit.Repository);
            Assert.Equal("develop", commit.Branch);
            Assert.Equal("[runner] delivery 1", commit.Message);

            var second = mutations.BackfillCommitDeliveryCoordinates();
            Assert.Equal(0, second.RepairedTasks);
            Assert.Equal(0, second.RepairedCommits);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
