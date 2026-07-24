using Xunit;

namespace AgentStudio.Tests;

public class ClaudeRateLimitEventParserTests
{
    [Fact]
    public void TryMap_PreservesLegacyCamelCaseFrame()
    {
        const string frame = """
        {"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour","status":"allowed","resetsAt":1777999999,"overageStatus":"allowed","isUsingOverage":false}}
        """;

        Assert.True(ClaudeRateLimitEventParser.TryMap(frame, "::test-job", out var mapped));
        Assert.NotNull(mapped);
        Assert.Equal("5-hour", mapped.Window);
        Assert.Equal("allowed", mapped.Status);
        Assert.Equal(1777999999L, mapped.ResetsAt);
        Assert.Equal("allowed", mapped.OverageStatus);
        Assert.False(mapped.IsUsingOverage);
    }

    [Fact]
    public void TryMap_AcceptsSnakeCaseStringsAndIgnoresUnknownFields()
    {
        const string frame = """
        {"type":"rate_limit_event","unknown_top_level":{"future":true},"rate_limit_info":{"rate_limit_type":"seven_day","status":"allowed_warning","resets_at":"2026-07-30T12:00:00Z","overage_status":"not_allowed","is_using_overage":"true","future_field":[1,2,3]}}
        """;

        Assert.True(ClaudeRateLimitEventParser.TryMap(frame, "::test-job", out var mapped));
        Assert.NotNull(mapped);
        Assert.Equal("weekly", mapped.Window);
        Assert.Equal("allowed_warning", mapped.Status);
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), mapped.ResetsAt);
        Assert.Equal("not_allowed", mapped.OverageStatus);
        Assert.True(mapped.IsUsingOverage);
    }

    [Theory]
    [InlineData("""{"type":"rate_limit_event","rate_limit_info":null,"future":42}""")]
    [InlineData("""{"type":"rate_limit_event","rate_limit_info":{"status":{"future":"shape"},"resetsAt":[]}}""")]
    public void TryMap_UnparseableOptionalFieldsDegradeToUnknown(string frame)
    {
        var exception = Record.Exception(() =>
            ClaudeRateLimitEventParser.TryMap(frame, "::test-job", out _));

        Assert.Null(exception);
        Assert.True(ClaudeRateLimitEventParser.TryMap(frame, "::test-job", out var mapped));
        Assert.NotNull(mapped);
        Assert.Null(mapped.Window);
        Assert.Equal(0, mapped.ResetsAt);
    }
}
