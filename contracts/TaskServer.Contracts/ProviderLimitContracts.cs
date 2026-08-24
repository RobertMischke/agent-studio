using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// How wide a provider limit reaches, which is the only property the fleet has
/// to steer on.
/// </summary>
public enum ProviderLimitScope
{
    /// <summary>No limit signal was found in the output.</summary>
    None,

    /// <summary>
    /// A per-request throttle: a bare <c>429</c> / "too many requests" with no
    /// reset evidence, or one that clears within
    /// <see cref="ProviderLimitDetector.AccountLimitMinimumWait"/>. The next
    /// request may well succeed, so this stays on the existing retry path and
    /// must NOT pause the fleet.
    /// </summary>
    Request,

    /// <summary>
    /// An account-level session / usage limit. Every run on this host shares the
    /// one provider account, so the next card walks into the identical rejection
    /// until the window resets. This is the 2026-08-23 signature ("You've hit
    /// your session limit - resets 12:20am"): treating it as a per-card failure
    /// escalated 32 cards in one night. It is not a task failure and never
    /// produces a per-card failure record.
    /// </summary>
    Account,
}

/// <summary>
/// One classified provider-limit observation. <see cref="ResetAt"/> is present
/// only when the provider stated a reset the parser could resolve without
/// guessing; callers apply their own bounded default when it is null.
/// </summary>
/// <param name="Scope">How wide the limit reaches.</param>
/// <param name="ResetAt">Resolved reset instant, or null when the provider gave none we trust.</param>
/// <param name="Window">Provider window label when named (<c>five_hour</c>, <c>seven_day</c>).</param>
/// <param name="Evidence">The matched text, trimmed to one line, safe to show an operator.</param>
public sealed record ProviderLimitSignal(
    ProviderLimitScope Scope,
    DateTimeOffset? ResetAt,
    string? Window,
    string Evidence)
{
    public static readonly ProviderLimitSignal None =
        new(ProviderLimitScope.None, null, null, "");

    public bool IsAccountLimit => Scope == ProviderLimitScope.Account;
}

/// <summary>
/// Pure classifier that separates an ACCOUNT-level provider session/usage limit
/// from an ordinary per-request throttle, and resolves the reset instant when
/// the provider stated one.
///
/// <para><b>Why the split matters.</b> The existing quota regex in
/// <see cref="ExecutionOutcomeAdapter"/> answers "was this a quota error?", which
/// is enough to label one card honestly but not to steer a fleet. On the night of
/// 2026-08-23 the operator's shared Claude account hit its session limit at
/// ~22:00; every subsequent claim spawned a CLI that died the same way, and each
/// death was reported as an unrecognised terminal outcome, so the board escalated
/// card after card until nothing claimable was left. The missing fact was not
/// "quota" but "this is the account, it will not clear before 00:20, so stop
/// claiming for this CLI". That is exactly what this type returns.</para>
///
/// <para><b>Evidence order.</b> Structured provider frames win over rendered
/// markers, which win over prose. Claude emits a <c>rate_limit_event</c> frame
/// whose <c>status</c> is authoritative: <c>allowed</c> / <c>allowed_warning</c>
/// are informational and must never pause anything (fixtures P22 prove a healthy
/// run carries them), while <c>rejected</c> is the hard stop. Only when no
/// structured evidence exists does the prose layer run.</para>
///
/// <para><b>Never guess a reset.</b> A wall-clock reset with no timezone
/// ("resets 12:20am") cannot be resolved without inventing an offset, and
/// inventing one is dangerous in both directions: too early re-opens the storm,
/// too late idles the fleet all night. Such input yields
/// <see cref="ProviderLimitSignal.ResetAt"/> = null so the caller applies a
/// bounded, re-probing default instead. This mirrors the admission-side rule that
/// unknown or suspicious reset data may not enter a wait branch.</para>
/// </summary>
public static class ProviderLimitDetector
{
    /// <summary>
    /// A rate-limit rejection that clears sooner than this is an ordinary
    /// per-request throttle. Anything further out means the shared account is
    /// parked, so the fleet has to stop offering that CLI.
    /// </summary>
    public static readonly TimeSpan AccountLimitMinimumWait = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Upper bound on a reset we are willing to believe. A parsed reset beyond
    /// this is treated as unresolved (null) rather than parking the CLI for a
    /// week on one malformed line.
    /// </summary>
    public static readonly TimeSpan MaximumTrustedReset = TimeSpan.FromHours(24);

    /// <summary>
    /// Prose that names the ACCOUNT's session or usage budget. These are
    /// unambiguous: the account is out, not this one request.
    /// </summary>
    private static readonly string[] AccountLimitNeedles =
    [
        "hit your session limit",
        "session limit reached",
        "usage limit reached",
        "reached your usage limit",
        "account usage limit",
        "insufficient_quota",
        "quota exhausted",
    ];

    /// <summary>
    /// Prose that names a throttle without saying whose budget is gone. On its
    /// own this is <see cref="ProviderLimitScope.Request"/>; it is promoted to
    /// account scope only when accompanied by a reset further out than
    /// <see cref="AccountLimitMinimumWait"/>.
    /// </summary>
    private static readonly string[] RequestThrottleNeedles =
    [
        "rate limit exceeded",
        "rate_limit_exceeded",
        "too many requests",
        "quota exceeded",
        "429",
    ];

    /// <summary>The rendered marker the Claude output renderer writes for a rate-limit frame.</summary>
    private static readonly Regex RenderedMarker = new(
        @"\[window=(?<window>[^\s\]]+)\s+status=(?<status>[^\s\]]+)(?:\s+resetsAt=(?<reset>\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"reset in 109 min", "reset in 3,6 h", "retry after 5h", "try again in 45 minutes".</summary>
    private static readonly Regex RelativeReset = new(
        @"(?:reset(?:s)?\s+in|retry[-\s]after|try\s+again\s+in)\s*:?\s*(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A bare numeric Retry-After header value, which HTTP defines as seconds.</summary>
    private static readonly Regex RetryAfterSeconds = new(
        @"retry[-\s]after\s*:\s*(?<value>\d+)\s*(?:$|[^\w-])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>An ISO-8601 reset stated in prose.</summary>
    private static readonly Regex IsoReset = new(
        @"reset(?:s)?(?:\s+at)?\s*:?\s*(?<iso>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2})?(?:Z|[+-]\d{2}:?\d{2})?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The human wall-clock shape the CLI prints, optionally with an IANA zone:
    /// "resets 8:10pm (Europe/Berlin)", "resets 12:20am".
    /// </summary>
    private static readonly Regex WallClockReset = new(
        @"reset(?:s)?(?:\s+at)?\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<meridiem>am|pm)?\s*(?:\((?<zone>[A-Za-z_]+/[A-Za-z_+-]+|UTC)\))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Classifies <paramref name="text"/>. Callers pass the diagnostic side of a
    /// FAILED run; a healthy run's informational frames resolve to
    /// <see cref="ProviderLimitScope.None"/> on their own because their status is
    /// <c>allowed</c>.
    /// </summary>
    public static ProviderLimitSignal Detect(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return ProviderLimitSignal.None;

        var structured = DetectStructured(text, now);
        if (structured is not null) return structured;

        return DetectProse(text, now);
    }

    /// <summary>
    /// Reads Claude's <c>rate_limit_event</c> frames and the renderer's marker.
    /// Returns null when neither is present so the prose layer can run; returns
    /// <see cref="ProviderLimitSignal.None"/> when a frame IS present and says
    /// the request was allowed, which stops prose elsewhere in the same output
    /// from overriding authoritative provider state.
    /// </summary>
    private static ProviderLimitSignal? DetectStructured(string text, DateTimeOffset now)
    {
        ProviderLimitSignal? allowed = null;

        foreach (var line in EnumerateLines(text))
        {
            var frame = ReadRateLimitFrame(line);
            if (frame is null)
            {
                var marker = RenderedMarker.Match(line);
                if (!marker.Success) continue;
                frame = new RateLimitFrame(
                    marker.Groups["status"].Value,
                    marker.Groups["window"].Value,
                    marker.Groups["reset"].Success ? marker.Groups["reset"].Value : null);
            }

            if (!IsRejected(frame.Status))
            {
                allowed ??= ProviderLimitSignal.None;
                continue;
            }

            var reset = Trust(ResolveEpochOrIso(frame.ResetsAt, now), now);
            return new ProviderLimitSignal(
                ProviderLimitScope.Account,
                reset,
                Normalize(frame.Window),
                Summarize(line));
        }

        return allowed;
    }

    private static ProviderLimitSignal DetectProse(string text, DateTimeOffset now)
    {
        var accountLine = FindLine(text, AccountLimitNeedles);
        var throttleLine = accountLine is null ? FindLine(text, RequestThrottleNeedles) : null;
        if (accountLine is null && throttleLine is null) return ProviderLimitSignal.None;

        var evidence = accountLine ?? throttleLine!;
        var reset = Trust(ResolveProseReset(evidence, text, now), now);

        // An explicit session/usage-limit statement is account scope on its own.
        // A bare throttle is promoted only when its own reset proves the wait is
        // long enough that the next card would hit the same wall.
        var scope = accountLine is not null
                    || (reset is { } at && at - now >= AccountLimitMinimumWait)
            ? ProviderLimitScope.Account
            : ProviderLimitScope.Request;

        return new ProviderLimitSignal(scope, reset, null, Summarize(evidence));
    }

    private static DateTimeOffset? ResolveProseReset(string line, string fullText, DateTimeOffset now)
        => ResolveRelative(line, now)
           ?? ResolveIso(line)
           ?? ResolveWallClock(line, now)
           // The reset is often rendered on a neighbouring line, so fall back to
           // the whole output before giving up.
           ?? ResolveRelative(fullText, now)
           ?? ResolveIso(fullText)
           ?? ResolveWallClock(fullText, now);

    private static DateTimeOffset? ResolveRelative(string text, DateTimeOffset now)
    {
        var header = RetryAfterSeconds.Match(text);
        if (header.Success
            && int.TryParse(header.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return now.AddSeconds(seconds);

        var match = RelativeReset.Match(text);
        if (!match.Success) return null;
        // The CLI renders decimals with the operator's locale separator ("3,6 h").
        var raw = match.Groups["value"].Value.Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;

        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        return unit switch
        {
            "s" or "sec" or "secs" or "second" or "seconds" => now.AddSeconds(value),
            "m" or "min" or "mins" or "minute" or "minutes" => now.AddMinutes(value),
            _ => now.AddHours(value),
        };
    }

    private static DateTimeOffset? ResolveIso(string text)
    {
        var match = IsoReset.Match(text);
        return match.Success
               && DateTimeOffset.TryParse(
                   match.Groups["iso"].Value,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Resolves "resets 8:10pm (Europe/Berlin)" to the next such instant after
    /// <paramref name="now"/>. Without a zone the offset is unknowable, so this
    /// deliberately returns null rather than assuming UTC and resuming the fleet
    /// at the wrong hour.
    /// </summary>
    private static DateTimeOffset? ResolveWallClock(string text, DateTimeOffset now)
    {
        var match = WallClockReset.Match(text);
        if (!match.Success || !match.Groups["zone"].Success) return null;

        if (!int.TryParse(match.Groups["hour"].Value, out var hour) || hour is < 0 or > 23) return null;
        var minute = match.Groups["minute"].Success
            ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture)
            : 0;
        if (minute is < 0 or > 59) return null;

        var meridiem = match.Groups["meridiem"].Value.ToLowerInvariant();
        if (meridiem == "pm" && hour < 12) hour += 12;
        else if (meridiem == "am" && hour == 12) hour = 0;
        if (hour > 23) return null;

        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(match.Groups["zone"].Value); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = new DateTimeOffset(
            local.Year, local.Month, local.Day, hour, minute, 0, local.Offset);
        // A stated reset that already looks past belongs to tomorrow.
        if (candidate <= now) candidate = candidate.AddDays(1);
        return candidate.ToUniversalTime();
    }

    private static DateTimeOffset? ResolveEpochOrIso(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            if (epoch <= 0) return null;
            // Providers have shipped both seconds and milliseconds; disambiguate
            // by magnitude rather than trusting one shape.
            return epoch > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Drops a reset we should not act on: one already elapsed (the window
    /// turned over while the run was dying) or one implausibly far out.
    /// </summary>
    private static DateTimeOffset? Trust(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is not { } at) return null;
        if (at <= now) return null;
        return at - now > MaximumTrustedReset ? null : at;
    }

    private static bool IsRejected(string? status)
        => status is not null
           && (status.Equals("rejected", StringComparison.OrdinalIgnoreCase)
               || status.Contains("exceeded", StringComparison.OrdinalIgnoreCase)
               || status.Contains("exhausted", StringComparison.OrdinalIgnoreCase));

    private static RateLimitFrame? ReadRateLimitFrame(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return null;
        if (!trimmed.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Contains("rateLimit", StringComparison.Ordinal))
            return null;

        JsonDocument document;
        try { document = JsonDocument.Parse(trimmed); }
        catch (JsonException) { return null; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var type = ReadString(root, "type");
            if (!string.Equals(type, "rate_limit_event", StringComparison.OrdinalIgnoreCase)) return null;

            if (!TryObject(root, "rate_limit_info", out var info)
                && !TryObject(root, "rateLimitInfo", out info))
                return null;

            return new RateLimitFrame(
                ReadString(info, "status"),
                ReadString(info, "rateLimitType") ?? ReadString(info, "rate_limit_type") ?? ReadString(info, "window"),
                ReadScalar(info, "resetsAt") ?? ReadScalar(info, "resets_at"));
        }
    }

    private static IEnumerable<string> EnumerateLines(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static string? FindLine(string text, IReadOnlyList<string> needles)
    {
        foreach (var line in EnumerateLines(text))
        {
            foreach (var needle in needles)
            {
                if (line.Contains(needle, StringComparison.OrdinalIgnoreCase)) return line;
            }
        }
        return null;
    }

    private static string Summarize(string line)
    {
        var single = line.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 240 ? single : single[..240].TrimEnd() + "...";
    }

    private static string? Normalize(string? window)
        => string.IsNullOrWhiteSpace(window) ? null : window.Trim().ToLowerInvariant();

    private static string? ReadString(JsonElement element, string name)
        => TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads a field the provider has shipped as either a number or a string.</summary>
    private static string? ReadScalar(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static bool TryObject(JsonElement element, string name, out JsonElement value)
    {
        if (TryProperty(element, name, out var found) && found.ValueKind == JsonValueKind.Object)
        {
            value = found;
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private sealed record RateLimitFrame(string? Status, string? Window, string? ResetsAt);
}
