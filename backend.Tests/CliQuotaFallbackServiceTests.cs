using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CliQuotaFallbackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atp-quota-fallback-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;

    public CliQuotaFallbackServiceTests()
    {
        Directory.CreateDirectory(_root);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
    }

    [Fact]
    public void Resolve_QuotaFull_SelectsConfiguredFallbackWithThinkingLevel()
    {
        var service = NewService();
        service.Set(new CliModelRouteProfile
        {
            CliType = "claude",
            PrimaryModel = "claude-opus",
            FallbackCliType = "codex",
            FallbackModel = "gpt-5.3-codex",
            FallbackThinkingLevel = "high",
        });

        var decision = service.Resolve("claude", null, null, cli =>
            cli == "claude"
                ? new CapEvaluation(true, "claude", "Weekly", 95, 100)
                : CapEvaluation.NotBlocked);

        Assert.True(decision.IsFallback);
        Assert.Equal("codex", decision.CliType);
        Assert.Equal("gpt-5.3-codex", decision.Model);
        Assert.Equal("high", decision.ThinkingLevel);
        Assert.Contains("Weekly", decision.Reason);
    }

    [Fact]
    public void Resolve_AfterQuotaReset_ReturnsPrimaryAgain()
    {
        var service = NewService();
        service.Set(new CliModelRouteProfile
        {
            CliType = "claude",
            PrimaryModel = "claude-opus",
            FallbackModel = "claude-sonnet",
        });

        var decision = service.Resolve("claude", null, null, _ => CapEvaluation.NotBlocked);

        Assert.False(decision.IsFallback);
        Assert.Equal("claude", decision.CliType);
        Assert.Equal("claude-opus", decision.Model);
    }

    [Fact]
    public void Resolve_QuotaFull_SupportsModelFallbackWithinSameCli()
    {
        var service = NewService();
        service.Set(new CliModelRouteProfile
        {
            CliType = "codex",
            PrimaryModel = "gpt-5.6-sol",
            FallbackModel = "gpt-5.3-codex",
            FallbackThinkingLevel = "medium",
        });

        var decision = service.Resolve("codex", null, null, _ =>
            new CapEvaluation(true, "codex", "Model window", 95, 100));

        Assert.True(decision.IsFallback);
        Assert.Equal("codex", decision.CliType);
        Assert.Equal("gpt-5.3-codex", decision.Model);
        Assert.Equal("medium", decision.ThinkingLevel);
    }

    [Fact]
    public void Resolve_DoesNotUseFallbackWhenItsCliIsAlsoBlocked()
    {
        var service = NewService();
        service.Set(new CliModelRouteProfile
        {
            CliType = "claude",
            FallbackCliType = "codex",
            FallbackModel = "gpt-5.3-codex",
        });

        var decision = service.Resolve("claude", null, null, cli =>
            new CapEvaluation(true, cli, "Weekly", 95, 100));

        Assert.False(decision.IsFallback);
        Assert.Contains("fallback codex", decision.Reason);
    }

    [Fact]
    public void Set_PersistsWorkspaceRoute()
    {
        NewService().Set(new CliModelRouteProfile { CliType = "codex", PrimaryModel = "gpt-5.3", FallbackModel = "gpt-5.2" });

        var profile = NewService().GetAll()["codex"];
        Assert.Equal("gpt-5.3", profile.PrimaryModel);
        Assert.Equal("gpt-5.2", profile.FallbackModel);
    }

    private CliQuotaFallbackService NewService() =>
        new(_config, NullLogger<CliQuotaFallbackService>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
