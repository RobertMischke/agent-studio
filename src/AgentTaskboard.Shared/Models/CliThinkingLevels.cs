namespace OrchestratorApi.Models;

/// <summary>
/// Capability table for CLI thinking / reasoning levels. Empty levels mean the
/// CLI/model has no supported selector and the runner should omit any flag.
/// </summary>
public static class CliThinkingLevels
{
    public const string Minimal = "minimal";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string XHigh = "xhigh";
    public const string Max = "max";

    private static readonly IReadOnlyList<string> OpenAiLevels = [Minimal, Low, Medium, High];
    private static readonly IReadOnlyList<string> ClaudeBasicLevels = [Low, Medium, High];
    private static readonly IReadOnlyList<string> ClaudeOpus45And46Levels = [Low, Medium, High, Max];
    private static readonly IReadOnlyList<string> ClaudeOpus47And48Levels = [Low, Medium, High, XHigh, Max];

    public static IReadOnlyList<string> For(string? cliType, string? model)
    {
        var cli = CliTypes.Normalize(cliType);
        var m = (model ?? string.Empty).Trim();

        if (string.Equals(cli, CliTypes.Codex, StringComparison.OrdinalIgnoreCase))
            return IsForeignCodexModel(m) ? [] : OpenAiLevels;

        if (string.Equals(cli, CliTypes.Claude, StringComparison.OrdinalIgnoreCase))
        {
            var normalized = m.Replace('.', '-').ToLowerInvariant();
            if (normalized.Contains("haiku-4-5", StringComparison.Ordinal)) return [];
            if (normalized.Contains("opus-4-8", StringComparison.Ordinal)
                || normalized.Contains("opus-4-7", StringComparison.Ordinal))
                return ClaudeOpus47And48Levels;
            if (normalized.Contains("opus-4-6", StringComparison.Ordinal)
                || normalized.Contains("opus-4-5", StringComparison.Ordinal))
                return ClaudeOpus45And46Levels;
            if (normalized.Contains("sonnet-4-6", StringComparison.Ordinal)) return ClaudeBasicLevels;
            if (normalized.StartsWith("claude-opus-", StringComparison.Ordinal)) return ClaudeOpus45And46Levels;
            if (normalized.StartsWith("claude-sonnet-", StringComparison.Ordinal)) return ClaudeBasicLevels;
            return [];
        }

        return [];
    }

    public static string? DefaultFor(string? cliType, string? model)
    {
        var levels = For(cliType, model);
        if (levels.Count == 0) return null;
        return string.Equals(CliTypes.Normalize(cliType), CliTypes.Codex, StringComparison.OrdinalIgnoreCase)
            ? Medium
            : High;
    }

    public static string? Normalize(string? cliType, string? model, string? requested)
    {
        var levels = For(cliType, model);
        if (levels.Count == 0) return null;
        var value = string.IsNullOrWhiteSpace(requested)
            ? DefaultFor(cliType, model)
            : requested.Trim().ToLowerInvariant();
        if (value is null) return null;
        return levels.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? levels.First(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))
            : DefaultFor(cliType, model);
    }

    private static bool IsForeignCodexModel(string model)
        => model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
           || model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
           || model.StartsWith("copilot", StringComparison.OrdinalIgnoreCase);
}
