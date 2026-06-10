using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks in the contract for <see cref="PromptEnhancementService"/>. The
/// Haiku subprocess is stubbed via the test seam so the suite never bills
/// tokens; the JSON parser / sanitiser is exercised directly.
/// </summary>
public class PromptEnhancementTests
{
    [Fact]
    public void Parse_PlainJson_ReturnsAllThreeFields()
    {
        var raw = "{\"refinedPrompt\":\"Add login form\",\"intent\":\"Wire up authentication\",\"tags\":[\"frontend\",\"auth\"]}";
        var r = PromptEnhancementService.Parse(raw);
        Assert.Equal("Add login form", r.RefinedPrompt);
        Assert.Equal("Wire up authentication", r.Intent);
        Assert.Equal(new[] { "frontend", "auth" }, r.Tags);
    }

    [Fact]
    public void Parse_StripsCodeFence()
    {
        var raw = "```json\n{\"refinedPrompt\":\"Add login form\",\"intent\":\"Wire up auth\",\"tags\":[\"frontend\"]}\n```";
        var r = PromptEnhancementService.Parse(raw);
        Assert.Equal("Add login form", r.RefinedPrompt);
        Assert.Equal("Wire up auth", r.Intent);
        Assert.Single(r.Tags);
    }

    [Fact]
    public void Parse_TolerateProseWrapper()
    {
        var raw = "Sure, here you go:\n{\"refinedPrompt\":\"X\",\"intent\":\"Y\",\"tags\":[\"backend\"]}\nThanks!";
        var r = PromptEnhancementService.Parse(raw);
        Assert.Equal("X", r.RefinedPrompt);
        Assert.Equal("Y", r.Intent);
        Assert.Single(r.Tags);
    }

    [Fact]
    public void Parse_NormalisesTagsToKebabCase()
    {
        var raw = "{\"refinedPrompt\":\"X\",\"intent\":\"Y\",\"tags\":[\"Frontend\",\"UI Improvement\",\"bug_fix\"]}";
        var r = PromptEnhancementService.Parse(raw);
        Assert.Equal(new[] { "frontend", "ui-improvement", "bug-fix" }, r.Tags);
    }

    [Fact]
    public void Parse_DedupesCaseInsensitively()
    {
        var raw = "{\"refinedPrompt\":\"X\",\"intent\":\"Y\",\"tags\":[\"Frontend\",\"frontend\",\"FRONTEND\",\"backend\"]}";
        var r = PromptEnhancementService.Parse(raw);
        Assert.Equal(new[] { "frontend", "backend" }, r.Tags);
    }

    [Fact]
    public void Parse_DropsEmptyAndOverflowingTags()
    {
        var raw = "{\"refinedPrompt\":\"X\",\"intent\":\"Y\",\"tags\":[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\"]}";
        var r = PromptEnhancementService.Parse(raw);
        Assert.Equal(5, r.Tags.Count);
    }

    [Fact]
    public void Parse_TrimsTrailingPeriodAndCollapsesIntent()
    {
        var raw = "{\"refinedPrompt\":\"X\",\"intent\":\"Refactor the auth layer.\\n(extra noise)\",\"tags\":[]}";
        var r = PromptEnhancementService.Parse(raw);
        Assert.Equal("Refactor the auth layer", r.Intent);
    }

    [Fact]
    public void Parse_NoJsonObject_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => PromptEnhancementService.Parse("just prose, no braces"));
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => PromptEnhancementService.Parse("{broken json"));
    }

    [Fact]
    public async Task EnhanceAsync_EmptyInput_ShortCircuits()
    {
        var svc = BuildService("Should never be returned");
        var r = await svc.EnhanceAsync("   ");
        Assert.Equal("", r.RefinedPrompt);
        Assert.Equal("", r.Intent);
        Assert.Empty(r.Tags);
        Assert.Equal(0, svc.InvocationCount);
    }

    [Fact]
    public async Task EnhanceAsync_ReturnsParsedResult()
    {
        var svc = BuildService("```json\n{\"refinedPrompt\":\"Add roadmap intake\",\"intent\":\"Build intake flow\",\"tags\":[\"frontend\",\"backend\"]}\n```");
        var r = await svc.EnhanceAsync("user dump describing roadmap intake");
        Assert.Equal("Add roadmap intake", r.RefinedPrompt);
        Assert.Equal("Build intake flow", r.Intent);
        Assert.Equal(new[] { "frontend", "backend" }, r.Tags);
        Assert.Equal(1, svc.InvocationCount);
    }

    [Fact]
    public async Task EnhanceAsync_TruncatesOversizedInput()
    {
        var svc = BuildService("{\"refinedPrompt\":\"x\",\"intent\":\"y\",\"tags\":[]}");
        var huge = new string('x', 50_000);
        await svc.EnhanceAsync(huge);
        Assert.NotNull(svc.LastPrompt);
        Assert.True(svc.LastPrompt!.Length < 16_000,
            $"rendered prompt was {svc.LastPrompt.Length} chars, expected truncation");
    }

    [Fact]
    public async Task EnhanceAsync_FailureSurfaces_AsInvalidOperation()
    {
        var svc = BuildService(""); // empty raw -> invokes but service rejects
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.EnhanceAsync("real prompt"));
    }

    private static FakePromptEnhancementService BuildService(string fakeResponse)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PromptTemplates:RuntimePath"] = FindPromptRoot()
            })
            .Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        return new FakePromptEnhancementService(NullLogger<PromptEnhancementService>.Instance, config, prompts, fakeResponse);
    }

    private static string FindPromptRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "prompts", "runtime");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate prompts/runtime from test base directory.");
    }

    private sealed class FakePromptEnhancementService : PromptEnhancementService
    {
        private readonly string _response;
        public string? LastPrompt { get; private set; }
        public int InvocationCount { get; private set; }

        public FakePromptEnhancementService(
            Microsoft.Extensions.Logging.ILogger<PromptEnhancementService> logger,
            IConfiguration config,
            RuntimePromptService prompts,
            string response)
            : base(logger, config, prompts)
        {
            _response = response;
        }

        protected override Task<(bool Ok, string? Raw, string? Error)> InvokeAsync(
            string prompt, CancellationToken ct)
        {
            LastPrompt = prompt;
            InvocationCount++;
            return Task.FromResult((true, (string?)_response, (string?)null));
        }
    }
}
