using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Regression for the 2026-05-12 mass-flip bug: PUT /api/tasks/{id}/cli-type
/// updated <c>cliType</c> but left the parallel <c>agent</c> field on its
/// previous value. Cards then rendered the new icon (from <c>cliType</c>)
/// next to the old text label (from <c>agent</c>), producing a "Claude
/// label, Codex icon" visual drift. The fix keeps both fields in lockstep.
/// </summary>
public class SetJobCliTypeSyncsAgentTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public SetJobCliTypeSyncsAgentTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-clitype-sync-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData("claude", "codex")]
    [InlineData("codex", "claude")]
    [InlineData("claude", "copilot")]
    [InlineData("copilot", "gemini")]
    [InlineData("gemini", "claude")]
    public void SetJobCliType_AlsoUpdatesAgentField(string startAgent, string newCliType)
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        mutations.CreateJob(new CreateJobRequest
        {
            Id = "drift",
            Title = "Drift",
            WatchPath = _watchPath,
            Agent = startAgent,
            CliType = startAgent,
            TargetState = TaskStates.Ready
        });

        Assert.True(mutations.SetJobCliType("drift", newCliType, _watchPath));

        var info = scanner.FindJob("drift", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(newCliType, info!.CliType);
        Assert.Equal(newCliType, info.Agent);

        // Verify the on-disk job.json is consistent so a fresh boot or external
        // reader sees the synced value (the cache invalidation in Updated()
        // would otherwise be invisible to a clean process).
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(Path.Combine(info.FolderPath, "job.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(newCliType, raw["cliType"].GetString());
        Assert.Equal(newCliType, raw["agent"].GetString());
    }

    [Fact]
    public void SetJobCliType_SameValue_StillEnsuresAgentMatches()
    {
        // Backfill case: a legacy job carries agent=claude, cliType=codex.
        // Re-issuing a same-value SetJobCliType("codex") should heal agent.
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var jobDir = Path.Combine(_watchPath, TaskStates.Ready, "legacy");
        Directory.CreateDirectory(jobDir);
        File.WriteAllText(Path.Combine(jobDir, "job.json"), """
            {
              "id": "legacy",
              "title": "Legacy",
              "state": "2-ready",
              "order": 10,
              "agent": "claude",
              "cliType": "codex"
            }
            """);

        Assert.True(mutations.SetJobCliType("legacy", "codex", _watchPath));

        var info = scanner.FindJob("legacy", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("codex", info!.CliType);
        Assert.Equal("codex", info.Agent);
    }

    private (TaskStateMachine machine, TaskScannerService scanner, TaskMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        return (machine, scanner, mutations);
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
}
