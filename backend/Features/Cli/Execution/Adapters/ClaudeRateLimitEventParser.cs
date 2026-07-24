using System.Globalization;
using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Provider fields extracted from one Claude Code <c>rate_limit_event</c>.
/// Field names are intentionally kept separate from the normalized event
/// window so the legacy marker tail can retain its stable raw value.
/// </summary>
internal sealed record ClaudeRateLimitFrame(
    string? Window,
    string? Status,
    long ResetsAt,
    string? OverageStatus,
    bool IsUsingOverage);

/// <summary>
/// Forgiving adapter for Claude Code rate-limit frames. Claude has shipped
/// camelCase fields, while other stream surfaces commonly use snake_case.
/// Both are accepted. Missing, unknown, or differently typed fields degrade
/// to null/zero instead of failing the CLI read loop.
/// </summary>
internal static class ClaudeRateLimitEventParser
{
    public static bool TryParse(string? jsonLine, out ClaudeRateLimitFrame? frame)
    {
        frame = null;
        if (string.IsNullOrWhiteSpace(jsonLine) || jsonLine.TrimStart()[0] != '{') return false;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonLine); }
        catch { return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !string.Equals(String(root, "type"), "rate_limit_event", StringComparison.Ordinal))
                return false;

            var info = Object(root, "rate_limit_info", "rateLimitInfo");
            frame = new ClaudeRateLimitFrame(
                Window: String(info, "rateLimitType", "rate_limit_type", "window"),
                Status: String(info, "status"),
                ResetsAt: UnixSeconds(info, "resetsAt", "resets_at"),
                OverageStatus: String(info, "overageStatus", "overage_status"),
                IsUsingOverage: Boolean(info, "isUsingOverage", "is_using_overage"));
            return true;
        }
    }

    public static bool TryMap(string? jsonLine, string jobKey, out CliRunEvent.RateLimitObserved? mapped)
    {
        mapped = null;
        if (!TryParse(jsonLine, out var frame) || frame == null) return false;

        mapped = new CliRunEvent.RateLimitObserved(
            NormalizeWindow(frame.Window),
            frame.Status,
            frame.ResetsAt,
            frame.OverageStatus,
            frame.IsUsingOverage);
        return true;
    }

    private static JsonElement Object(JsonElement parent, params string[] names)
    {
        if (parent.ValueKind != JsonValueKind.Object) return default;
        foreach (var name in names)
        {
            if (parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
                return value;
        }
        return default;
    }

    private static string? String(JsonElement parent, params string[] names)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!parent.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String)
                return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString();
        }
        return null;
    }

    private static bool Boolean(JsonElement parent, params string[] names)
    {
        if (parent.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in names)
        {
            if (!parent.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed))
                return parsed;
        }
        return false;
    }

    private static long UnixSeconds(JsonElement parent, params string[] names)
    {
        if (parent.ValueKind != JsonValueKind.Object) return 0;
        foreach (var name in names)
        {
            if (!parent.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            if (value.ValueKind != JsonValueKind.String) continue;
            var raw = value.GetString();
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number;
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
                return timestamp.ToUnixTimeSeconds();
        }
        return 0;
    }

    private static string? NormalizeWindow(string? window)
        => window?.ToLowerInvariant() switch
        {
            "five_hour" or "five-hour" => "5-hour",
            "seven_day" or "seven-day" => "weekly",
            _ => window?.Replace('_', '-')
        };
}
