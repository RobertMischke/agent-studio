using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskPipelineEndpointTests : IDisposable
{
    private readonly string _watchPath = Path.Combine(
        Path.GetTempPath(),
        "task-pipeline-endpoint-" + Guid.NewGuid().ToString("N"));

    public TaskPipelineEndpointTests()
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        var job = Path.Combine(_watchPath, TaskStates.Backlog, "pipeline-capabilities");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "task.json"), JsonSerializer.Serialize(new
        {
            id = "pipeline-capabilities",
            title = "Pipeline capabilities",
            state = TaskStates.Backlog,
            order = 1,
            agent = "codex",
            mode = "coding",
        }));
        File.WriteAllText(Path.Combine(job, "status.md"), "# Result");
        File.WriteAllText(Path.Combine(job, "aspect-code-quality.md"), "# Code quality");
        File.WriteAllText(Path.Combine(job, "aspect-not-in-pipeline.md"), "# Unrelated");

        File.WriteAllText(Path.Combine(_watchPath, "project-settings.json"), JsonSerializer.Serialize(
            new Dictionary<string, ProjectSettings>
            {
                ["pipeline-capabilities"] = new()
                {
                    PipelineSteps = new Dictionary<string, PipelineStepSetting>
                    {
                        ["aspect-code-quality"] = new()
                        {
                            Enabled = true,
                            Prompt = "Keep this custom prompt",
                            Condition = new PipelineStepCondition
                            {
                                When = PipelineStepConditions.Tag,
                                Value = "security",
                            },
                        },
                    },
                },
            }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { }
    }

    [Fact]
    public async Task GetPipeline_ConfigExposesCatalogueDisableCapabilities()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "pipeline-capabilities",
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _watchPath,
                    ["TaskRepository"] = _watchPath,
                }));
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/tasks/pipeline-capabilities/pipeline?watchPath={Uri.EscapeDataString(_watchPath)}");
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var config = body.RootElement.GetProperty("config");

        Assert.False(config.GetProperty(PipelineCatalogue.CoreAgentRunStepId).GetProperty("canDisable").GetBoolean());
        Assert.False(config.GetProperty(PipelineCatalogue.LoopGuardStepId).GetProperty("canDisable").GetBoolean());
        Assert.True(config.GetProperty(PipelineCatalogue.LintScssStepId).GetProperty("canDisable").GetBoolean());

        var qualityConfig = config.GetProperty("aspect-code-quality");
        Assert.Equal("Keep this custom prompt", qualityConfig.GetProperty("prompt").GetString());
        Assert.Equal(PipelineStepConditions.Tag,
            qualityConfig.GetProperty("condition").GetProperty("when").GetString());
        Assert.Equal("security",
            qualityConfig.GetProperty("condition").GetProperty("value").GetString());

        var resultFiles = body.RootElement.GetProperty("resultFiles");
        Assert.Equal("status.md",
            resultFiles.GetProperty(PipelineCatalogue.CoreAgentRunStepId).GetString());
        Assert.Equal("aspect-code-quality.md",
            resultFiles.GetProperty("aspect-code-quality").GetString());
        Assert.False(resultFiles.TryGetProperty("aspect-requirement-fit", out _));
        Assert.False(resultFiles.TryGetProperty("aspect-not-in-pipeline", out _));
    }
}
