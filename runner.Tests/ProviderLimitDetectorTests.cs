using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Direct matrix over <see cref="ProviderLimitDetector"/>. The scope column is
/// the one the fleet steers on: only <see cref="ProviderLimitScope.Account"/>
/// pauses a CLI, so a row that drifts from Account to Request re-opens the
/// 2026-08-23 escalation storm, and a row that drifts the other way idles the
/// fleet on an ordinary throttle.
/// </summary>
public sealed class ProviderLimitDetectorTests
{
    /// <summary>Fixed clock so relative and wall-clock resets are assertable.</summary>
    private static readonly DateTimeOffset Now =
        new(2026, 8, 23, 20, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, string, ProviderLimitScope> ScopeCases => new()
    {
        // ---- The incident signature -------------------------------------
        {
            "claude session limit, the 2026-08-23 22:00 signature",
            "You've hit your session limit · resets 12:20am",
            ProviderLimitScope.Account
        },
        {
            "claude session limit with a zone",
            "You've hit your session limit · resets 8:10pm (Europe/Berlin)",
            ProviderLimitScope.Account
        },
        {
            "usage limit for this model",
            "You've reached your usage limit for this model.",
            ProviderLimitScope.Account
        },

        // ---- Structured provider evidence -------------------------------
        {
            "rate_limit_event rejected, camelCase epoch reset",
            """{"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour","status":"rejected","resetsAt":1787522400}}""",
            ProviderLimitScope.Account
        },
        {
            "rate_limit_event rejected, snake_case ISO reset",
            """{"type":"rate_limit_event","rate_limit_info":{"rate_limit_type":"seven_day","status":"rejected","resets_at":"2026-08-23T23:30:00Z"}}""",
            ProviderLimitScope.Account
        },
        {
            "rendered marker, rejected",
            "● Rate limit · five-hour · rejected · reset in 3,6 h  [window=five_hour status=rejected resetsAt=1787522400 overage=rejected usingOverage=false]",
            ProviderLimitScope.Account
        },

        // ---- Informational frames must never pause anything --------------
        {
            "P22 fixture: allowed_warning frame on a healthy run",
            """{"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour","status":"allowed_warning","resetsAt":1777999999,"overageStatus":"allowed","isUsingOverage":false}}""",
            ProviderLimitScope.None
        },
        {
            "rendered marker, allowed",
            "● Rate limit · five-hour · allowed · reset in 109 min  [window=five_hour status=allowed resetsAt=1787522400 overage=allowed usingOverage=false]",
            ProviderLimitScope.None
        },
        {
            "no limit language at all",
            "Build succeeded. 0 warnings.",
            ProviderLimitScope.None
        },

        // ---- Per-request throttles stay on the existing retry path -------
        {
            "bare 429 with no reset evidence",
            "Error: 429 too many requests",
            ProviderLimitScope.Request
        },
        {
            "rate limit exceeded with no reset evidence",
            "Error: rate_limit_exceeded",
            ProviderLimitScope.Request
        },
        {
            "throttle clearing inside the account-limit floor",
            "rate limit exceeded; retry after 30s",
            ProviderLimitScope.Request
        },

        // ---- A throttle whose own reset proves the account is parked -----
        {
            "codex P22 fixture: turn.failed carrying a five-hour retry-after",
            """{"type":"turn.failed","error":{"message":"rate limit exceeded; retry after 5h"}}""",
            ProviderLimitScope.Account
        },
    };

    [Theory]
    [MemberData(nameof(ScopeCases))]
    public void Detect_AssignsTheSteeringScope(string label, string text, ProviderLimitScope expected)
    {
        var signal = ProviderLimitDetector.Detect(text, Now);
        Assert.Equal(expected, signal.Scope);
        Assert.Equal(expected == ProviderLimitScope.Account, signal.IsAccountLimit);
        if (expected != ProviderLimitScope.None)
            Assert.False(string.IsNullOrWhiteSpace(signal.Evidence), $"{label} must carry operator evidence.");
    }

    [Fact]
    public void Detect_StructuredEpochReset_IsResolvedExactly()
    {
        var signal = ProviderLimitDetector.Detect(
            """{"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour","status":"rejected","resetsAt":1787522400}}""",
            Now);

        Assert.Equal(ProviderLimitScope.Account, signal.Scope);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787522400), signal.ResetAt);
        Assert.Equal("five_hour", signal.Window);
    }

    [Fact]
    public void Detect_StructuredIsoReset_IsResolvedExactly()
    {
        var signal = ProviderLimitDetector.Detect(
            """{"type":"rate_limit_event","rate_limit_info":{"rate_limit_type":"seven_day","status":"rejected","resets_at":"2026-08-23T23:30:00Z"}}""",
            Now);

        Assert.Equal(new DateTimeOffset(2026, 8, 23, 23, 30, 0, TimeSpan.Zero), signal.ResetAt);
        Assert.Equal("seven_day", signal.Window);
    }

    [Fact]
    public void Detect_RelativeReset_UsesTheOperatorsDecimalSeparator()
    {
        // The CLI renders "reset in 3,6 h" in a comma-decimal locale; reading
        // that as "3" or failing to parse would resume the fleet 36 minutes early.
        var signal = ProviderLimitDetector.Detect(
            "You've hit your session limit · reset in 3,6 h",
            Now);

        Assert.Equal(Now.AddHours(3.6), signal.ResetAt);
    }

    [Fact]
    public void Detect_ZonedWallClockReset_ResolvesToTheNextOccurrence()
    {
        // 20:00Z on 2026-08-23 is 22:00 in Berlin (CEST, +02:00), so a stated
        // 8:10pm reset has already passed today and belongs to tomorrow.
        var signal = ProviderLimitDetector.Detect(
            "You've hit your session limit · resets 8:10pm (Europe/Berlin)",
            Now);

        Assert.Equal(ProviderLimitScope.Account, signal.Scope);
        Assert.NotNull(signal.ResetAt);
        Assert.True(signal.ResetAt > Now, "A resolved reset must be in the future.");
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 20, 10, 0, TimeSpan.FromHours(2)), signal.ResetAt);
    }

    [Fact]
    public void Detect_WallClockResetWithoutAZone_LeavesTheResetUnresolved()
    {
        // "resets 12:20am" carries no offset. Guessing UTC would restart the
        // fleet at the wrong hour, so the caller must apply its own bounded
        // default instead. This is the exact text from the incident night.
        var signal = ProviderLimitDetector.Detect(
            "You've hit your session limit · resets 12:20am",
            Now);

        Assert.Equal(ProviderLimitScope.Account, signal.Scope);
        Assert.Null(signal.ResetAt);
    }

    [Fact]
    public void Detect_ElapsedReset_IsNotTrusted()
    {
        // The window turned over while the run was dying. Acting on a past
        // reset would advertise a limit that is already lifted.
        var signal = ProviderLimitDetector.Detect(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resets_at":"2026-08-23T19:00:00Z"}}""",
            Now);

        Assert.Equal(ProviderLimitScope.Account, signal.Scope);
        Assert.Null(signal.ResetAt);
    }

    [Fact]
    public void Detect_ImplausiblyDistantReset_IsNotTrusted()
    {
        // One malformed line must not park the CLI for a week.
        var signal = ProviderLimitDetector.Detect(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resets_at":"2026-09-30T00:00:00Z"}}""",
            Now);

        Assert.Equal(ProviderLimitScope.Account, signal.Scope);
        Assert.Null(signal.ResetAt);
    }

    [Fact]
    public void Detect_AllowedFrameWins_OverIncidentalThrottleProse()
    {
        // A healthy run that merely prints throttle wording alongside an
        // authoritative "allowed" frame must not pause the fleet.
        var text = string.Join('\n',
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1787522400}}""",
            "note: the previous attempt saw rate limit exceeded and recovered",
            """{"type":"result","subtype":"success","is_error":false,"result":"Done.\n\n[[TASK_DONE]]"}""");

        Assert.Equal(ProviderLimitScope.None, ProviderLimitDetector.Detect(text, Now).Scope);
    }

    [Fact]
    public void Detect_RejectedFrameWins_EvenWhenAnEarlierFrameWasAllowed()
    {
        // The window closes mid-run: the last word is the rejection.
        var text = string.Join('\n',
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1787522400}}""",
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resets_at":"2026-08-23T23:30:00Z"}}""");

        var signal = ProviderLimitDetector.Detect(text, Now);
        Assert.Equal(ProviderLimitScope.Account, signal.Scope);
        Assert.Equal(new DateTimeOffset(2026, 8, 23, 23, 30, 0, TimeSpan.Zero), signal.ResetAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Detect_EmptyOutput_IsNotALimit(string? text)
        => Assert.Equal(ProviderLimitScope.None, ProviderLimitDetector.Detect(text, Now).Scope);
}
