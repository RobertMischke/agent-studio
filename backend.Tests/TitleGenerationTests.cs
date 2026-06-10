using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks in the contract for <see cref="TitleGenerationService"/>. The
/// Haiku subprocess is stubbed via the test seam so the suite never bills
/// tokens; the sanitiser is exercised directly.
/// </summary>
public class TitleGenerationTests
{
    [Fact]
    public void Sanitize_StripsCodeFence()
    {
        var raw = "```\nAdd login form\n```";
        Assert.Equal("Add login form", TitleGenerationService.SanitizeTitle(raw));
    }

    [Fact]
    public void Sanitize_StripsLanguageFence()
    {
        var raw = "```text\nAdd login form\n```";
        Assert.Equal("Add login form", TitleGenerationService.SanitizeTitle(raw));
    }

    [Fact]
    public void Sanitize_StripsLeadingTitlePrefix()
    {
        Assert.Equal("Refactor user store", TitleGenerationService.SanitizeTitle("Title: Refactor user store"));
        Assert.Equal("Refactor user store", TitleGenerationService.SanitizeTitle("TITLE: Refactor user store"));
        Assert.Equal("Refactor user store", TitleGenerationService.SanitizeTitle("Task: Refactor user store"));
    }

    [Fact]
    public void Sanitize_StripsWrappingQuotes()
    {
        Assert.Equal("Add roadmap intake", TitleGenerationService.SanitizeTitle("\"Add roadmap intake\""));
        Assert.Equal("Add roadmap intake", TitleGenerationService.SanitizeTitle("'Add roadmap intake'"));
    }

    [Fact]
    public void Sanitize_KeepsOnlyFirstLine()
    {
        var raw = "Add login form\nFollow-up explanation that should not appear.";
        Assert.Equal("Add login form", TitleGenerationService.SanitizeTitle(raw));
    }

    [Fact]
    public void Sanitize_TrimsTrailingPeriod()
    {
        Assert.Equal("Add login form", TitleGenerationService.SanitizeTitle("Add login form."));
    }

    [Fact]
    public void Sanitize_KeepsTrailingEllipsis()
    {
        Assert.Equal("Add login form...", TitleGenerationService.SanitizeTitle("Add login form..."));
    }

    [Fact]
    public void Sanitize_ClampsLongTitleAt80Chars()
    {
        var raw = new string('x', 200);
        var result = TitleGenerationService.SanitizeTitle(raw);
        Assert.True(result.Length <= 80);
    }

    [Fact]
    public void Sanitize_EmptyInput_ReturnsFallback()
    {
        Assert.Equal("Untitled task", TitleGenerationService.SanitizeTitle(""));
        Assert.Equal("Untitled task", TitleGenerationService.SanitizeTitle("   "));
    }

    [Fact]
    public async Task GenerateAsync_EmptyInput_ShortCircuitsWithoutSpawningHaiku()
    {
        var svc = BuildService("Should never be returned");
        var title = await svc.GenerateAsync("   ");
        Assert.Equal("Untitled task", title);
        Assert.Equal(0, svc.InvocationCount);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsSanitisedTitle()
    {
        var svc = BuildService("```\nAdd roadmap intake\n```\n");
        var title = await svc.GenerateAsync("user dump describing roadmap intake");
        Assert.Equal("Add roadmap intake", title);
        Assert.Equal(1, svc.InvocationCount);
    }

    [Fact]
    public async Task GenerateAsync_TruncatesOversizedInput()
    {
        var svc = BuildService("Add big feature");
        var huge = new string('x', 50_000);
        await svc.GenerateAsync(huge);
        Assert.NotNull(svc.LastPrompt);
        // The service caps at 8k chars before rendering. With a small
        // template wrapper, the rendered prompt should comfortably fit
        // under 16k.
        Assert.True(svc.LastPrompt!.Length < 16_000,
            $"rendered prompt was {svc.LastPrompt.Length} chars, expected truncation");
    }

    [Fact]
    public async Task GenerateAsync_FailureSurfaces_AsInvalidOperation()
    {
        var svc = BuildService(""); // empty raw -> invokes but returns ""
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GenerateAsync("real prompt"));
    }

    private static FakeTitleService BuildService(string fakeResponse)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PromptTemplates:RuntimePath"] = FindPromptRoot()
            })
            .Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        return new FakeTitleService(NullLogger<TitleGenerationService>.Instance, config, prompts, fakeResponse);
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

    private sealed class FakeTitleService : TitleGenerationService
    {
        private readonly string _response;
        public string? LastPrompt { get; private set; }
        public int InvocationCount { get; private set; }

        public FakeTitleService(
            Microsoft.Extensions.Logging.ILogger<TitleGenerationService> logger,
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
            // Empty raw response should propagate as an InvalidOperationException
            // from the caller, mimicking a Haiku call that returned no usable text.
            return Task.FromResult((true, (string?)_response, (string?)null));
        }
    }
}
