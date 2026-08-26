using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>The terminal outcome an agent signs its run off with.</summary>
public enum RunOutcomeKind { Done, Blocked, NeedsInput, NoOp, Unknown, EnvironmentFailure, QuotaWait }

public sealed record RunOutcome(RunOutcomeKind Kind, string? Reason)
{
    /// <summary>
    /// Lane expected from the server's normal remote-run completion policy for
    /// a coding run. Environment failures return to Ready while the
    /// server-owned retry budget remains; the exhausted attempt is promoted to
    /// Escalated by the server. An Epic planning run is not a coding run and
    /// does not follow this mapping: it carries no Result-SHA and the server
    /// completes it into 5-human-review.
    /// </summary>
    public string TargetState => Kind switch
    {
        RunOutcomeKind.Done or RunOutcomeKind.NoOp => "4-auto-review",
        RunOutcomeKind.EnvironmentFailure or RunOutcomeKind.QuotaWait => "2-ready",
        _ => "5-human-review",
    };

    public string SummaryPrefix => Kind switch
    {
        RunOutcomeKind.Done => "Remote run completed",
        RunOutcomeKind.Blocked => "Remote run blocked",
        RunOutcomeKind.NeedsInput => "Remote run needs input",
        RunOutcomeKind.NoOp => "Remote run was a no-op",
        RunOutcomeKind.EnvironmentFailure => "Remote claim environment preparation failed",
        RunOutcomeKind.QuotaWait => "Provider account limit; waiting for automatic recovery",
        _ => "Remote run ended without a terminal sentinel",
    };
}

/// <summary>
/// Recognises the canonical terminal sentinel the agent emits
/// (<c>[[TASK_DONE]]</c> / <c>[[TASK_BLOCKED:reason]]</c> / ...), mirroring the
/// server's authoritative <c>AgentOutcomeAnalyzer.SentinelRegex</c>. Structured
/// CLI output is reduced to the final agent reply before scanning, so tool
/// output, diffs, and diagnostics cannot impersonate an agent verdict.
/// </summary>
public static class SentinelScanner
{
    private static readonly Regex TerminalSentinel = new(
        @"(?:\A|(?<=\n))[ \t]*\[\[\s*TASK[\s_-]*(?<keyword>DONE|BLOCKED|NEEDS[\s_-]*INPUT|NOOP)\s*(?::\s*(?<reason>[^\]\r\n]*?))?\s*\]\][ \t]*(?:\r?\n)?[ \t]*\z",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static RunOutcome Scan(string text)
    {
        if (string.IsNullOrEmpty(text)) return new RunOutcome(RunOutcomeKind.Unknown, null);

        var finalReply = ExtractFinalAgentReply(text, out var structuredOutput);
        var replyToScan = finalReply ?? (structuredOutput ? null : text);
        if (string.IsNullOrWhiteSpace(replyToScan)) return new RunOutcome(RunOutcomeKind.Unknown, null);

        var match = TerminalSentinel.Match(replyToScan);
        if (!match.Success) return new RunOutcome(RunOutcomeKind.Unknown, null);

        var keyword = Regex.Replace(match.Groups["keyword"].Value, @"[\s_-]+", "_").ToUpperInvariant();
        var reason = match.Groups["reason"].Success && match.Groups["reason"].Value.Length > 0
            ? match.Groups["reason"].Value.Trim()
            : null;

        var kind = keyword switch
        {
            "DONE" => RunOutcomeKind.Done,
            "BLOCKED" => RunOutcomeKind.Blocked,
            "NEEDS_INPUT" => RunOutcomeKind.NeedsInput,
            "NOOP" => RunOutcomeKind.NoOp,
            _ => RunOutcomeKind.Unknown,
        };
        if (reason is null && kind == RunOutcomeKind.Blocked)
            reason = "Agent emitted TASK_BLOCKED without a stated reason.";
        else if (reason is null && kind == RunOutcomeKind.NeedsInput)
            reason = "Agent emitted TASK_NEEDS_INPUT without a stated question.";
        return new RunOutcome(kind, reason);
    }

    private static string? ExtractFinalAgentReply(string output, out bool structuredOutput)
    {
        structuredOutput = false;
        string? finalAgentMessage = null;
        string? finalResult = null;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith('{')) continue;

            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var typeElement)
                    || typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = typeElement.GetString();
                if (!IsStructuredCliFrame(type)) continue;
                structuredOutput = true;

                if (string.Equals(type, "item.completed", StringComparison.Ordinal)
                    && root.TryGetProperty("item", out var item)
                    && item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var itemType)
                    && itemType.ValueKind == JsonValueKind.String
                    && string.Equals(itemType.GetString(), "agent_message", StringComparison.Ordinal)
                    && item.TryGetProperty("text", out var textElement)
                    && textElement.ValueKind == JsonValueKind.String)
                {
                    finalAgentMessage = textElement.GetString();
                    continue;
                }

                if (string.Equals(type, "assistant", StringComparison.Ordinal)
                    && TryReadClaudeAssistantText(root, out var assistantText))
                {
                    finalAgentMessage = assistantText;
                    continue;
                }

                if (string.Equals(type, "result", StringComparison.Ordinal)
                    && root.TryGetProperty("result", out var resultElement)
                    && resultElement.ValueKind == JsonValueKind.String)
                {
                    finalResult = resultElement.GetString();
                }
            }
            catch (JsonException)
            {
                // A malformed or ordinary text line is not a structured CLI frame.
            }
        }

        // Claude's result frame is its explicit completion section. Codex has
        // no equivalent text-bearing completion frame, so its last completed
        // agent_message is authoritative even if telemetry/tool frames follow.
        return finalResult ?? finalAgentMessage;
    }

    private static bool TryReadClaudeAssistantText(JsonElement root, out string text)
    {
        text = string.Empty;
        if (!root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("type", out var partType)
                && partType.ValueKind == JsonValueKind.String
                && string.Equals(partType.GetString(), "text", StringComparison.Ordinal)
                && part.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String)
            {
                var value = textElement.GetString();
                if (!string.IsNullOrEmpty(value)) parts.Add(value);
            }
        }

        text = string.Join(Environment.NewLine, parts);
        return parts.Count > 0;
    }

    private static bool IsStructuredCliFrame(string? type)
        => type is "thread.started" or "turn.started" or "turn.completed" or "turn.failed"
            or "item.started" or "item.completed" or "session_meta"
            or "system" or "assistant" or "user" or "result" or "rate_limit_event";
}
