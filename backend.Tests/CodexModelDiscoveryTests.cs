using OrchestratorApi.Services.Pty;
using Xunit;

namespace OrchestratorApi.Tests;

public class CodexModelDiscoveryTests
{
    [Fact]
    public void ParseDebugModelsJson_ReturnsVisibleModelsInPriorityOrder()
    {
        const string output = """
        leading terminal noise
        {"models":[
          {"slug":"gpt-5.4-mini","display_name":"GPT-5.4-Mini","visibility":"list","priority":4},
          {"slug":"codex-auto-review","display_name":"Codex Auto Review","visibility":"hide","priority":29},
          {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","priority":0},
          {"slug":"gpt-5.4","display_name":"gpt-5.4","visibility":"list","priority":2}
        ]}
        trailing terminal noise
        """;

        var models = CodexModelDiscovery.ParseDebugModelsJson(output, activeModel: "gpt-5.4");

        Assert.Equal(["gpt-5.5", "gpt-5.4", "gpt-5.4-mini"], models.Select(m => m.Id));
        Assert.DoesNotContain(models, m => m.Id == "codex-auto-review");
        Assert.Equal("gpt-5.4", Assert.Single(models, m => m.IsDefault).Id);
        Assert.All(models, m => Assert.Equal("openai", m.Vendor));
        Assert.All(models, m =>
        {
            Assert.Equal(["minimal", "low", "medium", "high"], m.ThinkingLevels);
            Assert.Equal("medium", m.DefaultThinkingLevel);
        });
    }

    [Fact]
    public void ParseDebugModelsJson_FallsBackToFirstVisibleModelAsDefault()
    {
        const string output = """
        {"models":[
          {"slug":"gpt-5.4","display_name":"gpt-5.4","visibility":"list","priority":2},
          {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","priority":0}
        ]}
        """;

        var models = CodexModelDiscovery.ParseDebugModelsJson(output);

        Assert.Equal("gpt-5.5", Assert.Single(models, m => m.IsDefault).Id);
    }
}
