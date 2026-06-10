using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the resolution behind <c>GET /api/tasks/code-review/defaults</c>.
/// The panel seeds its CLI + model picker from this when the operator has
/// no remembered last-used pair, so the precedence (configured value wins,
/// hard fallback otherwise) is the load-bearing contract: a deployment can
/// set <c>CodeReviewStep:DefaultModel</c> and have it show up in the UI.
/// </summary>
public class CodeReviewDefaultsEndpointTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (k, v) in pairs) dict[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void ResolveDefaults_EmptyConfig_UsesHardFallbacks()
    {
        var (cli, model) = TaskCodeReviewEndpoints.ResolveDefaults(Config());

        Assert.Equal(TaskCodeReviewEndpoints.DefaultCliFallback, cli);
        Assert.Equal(TaskCodeReviewEndpoints.DefaultModelFallback, model);
        Assert.Equal("claude-opus-4-8", model);
    }

    [Fact]
    public void ResolveDefaults_ConfiguredValues_WinOverFallbacks()
    {
        var config = Config(
            (TaskCodeReviewEndpoints.DefaultCliConfigKey, "codex"),
            (TaskCodeReviewEndpoints.DefaultModelConfigKey, "gpt-5-codex"));

        var (cli, model) = TaskCodeReviewEndpoints.ResolveDefaults(config);

        Assert.Equal("codex", cli);
        Assert.Equal("gpt-5-codex", model);
    }

    [Fact]
    public void ResolveDefaults_WhitespaceConfig_FallsBack()
    {
        var config = Config(
            (TaskCodeReviewEndpoints.DefaultCliConfigKey, "   "),
            (TaskCodeReviewEndpoints.DefaultModelConfigKey, ""));

        var (cli, model) = TaskCodeReviewEndpoints.ResolveDefaults(config);

        Assert.Equal(TaskCodeReviewEndpoints.DefaultCliFallback, cli);
        Assert.Equal(TaskCodeReviewEndpoints.DefaultModelFallback, model);
    }
}
