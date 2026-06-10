using System.Text.RegularExpressions;

namespace AgentStudio.Supervisor;

/// <summary>
/// Pure parsing for the supervisor's soft-reasoning agent output. Extracts
/// <c>[[SUPERVISOR_OBSERVATION: severity=...; topic=...; message=...]]</c>
/// sentinels into typed advisories. Kept static so the rules are unit-testable
/// without running a CLI.
/// </summary>
public static class SoftReasoningParsing
{
    private static readonly Regex SentinelRegex = new(
        @"\[\[SUPERVISOR_OBSERVATION:\s*(?<body>[^\]]+)\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<SupervisorAdvisory> Parse(
        string output,
        string project,
        DateTime atUtc,
        string? jobId)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<SupervisorAdvisory>();

        var result = new List<SupervisorAdvisory>();
        foreach (Match match in SentinelRegex.Matches(output))
        {
            var body = match.Groups["body"].Value;
            var fields = ParseFields(body);
            if (fields == null) continue;

            var severity = ParseSeverity(fields.GetValueOrDefault("severity"));
            var topic = fields.GetValueOrDefault("topic")?.Trim() ?? "soft-reasoning";
            var message = fields.GetValueOrDefault("message")?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(message)) continue;

            result.Add(new SupervisorAdvisory(
                CreatedAt: atUtc,
                Project: project,
                Severity: severity,
                Source: SupervisorSource.SoftReasoning,
                Topic: topic,
                Message: message,
                JobId: jobId));
        }
        return result;
    }

    private static Dictionary<string, string>? ParseFields(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();
            if (key.Length == 0) continue;
            dict[key] = value;
        }
        return dict.Count == 0 ? null : dict;
    }

    private static SupervisorSeverity ParseSeverity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return SupervisorSeverity.Info;
        return value.Trim().ToLowerInvariant() switch
        {
            "high" => SupervisorSeverity.High,
            "warn" or "warning" => SupervisorSeverity.Warn,
            _ => SupervisorSeverity.Info,
        };
    }
}
