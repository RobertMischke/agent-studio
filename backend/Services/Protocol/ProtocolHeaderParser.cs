using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrchestratorApi.Services.Protocol;

/// <summary>
/// Result of parsing a status.md document. Exactly one of these is
/// populated:
/// <list type="bullet">
///   <item><see cref="Header"/> when a structured header was found and validated.</item>
///   <item><see cref="ParseWarning"/> when a header block was found but did not validate (the document still renders; the warning surfaces in logs).</item>
/// </list>
/// <see cref="LeadParagraph"/> always carries the document's first prose
/// paragraph, with a hard limit of two lines, so the UI can fall back to
/// it when the structured header is absent.
/// </summary>
public sealed record ProtocolHeaderParseResult(
    ProtocolHeader? Header,
    string? ParseWarning,
    string LeadParagraph);

/// <summary>
/// Tolerant parser for the structured header carried at the top of a
/// job's <c>status.md</c>. Two surface forms are accepted, in this
/// order of preference:
/// <list type="number">
///   <item>An HTML comment of the form <c>&lt;!-- header-json: { ... } --&gt;</c> anywhere in the document.</item>
///   <item>A fenced code block tagged <c>```json header</c> (or just <c>```json</c> if it is the first fenced block in the document).</item>
/// </list>
/// The parser never throws on a malformed payload: it returns a
/// <see cref="ProtocolHeaderParseResult"/> with <see cref="ProtocolHeaderParseResult.ParseWarning"/>
/// set and <see cref="ProtocolHeaderParseResult.Header"/> null. The
/// caller (frontend header card, executive summary aggregator) is
/// expected to fall back to the document's lead paragraph when the
/// header is absent or invalid.
/// </summary>
public static class ProtocolHeaderParser
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Regex HtmlCommentHeader = new(
        @"<!--\s*header-json\s*:\s*(?<json>\{[\s\S]*?\})\s*-->",
        RegexOptions.Compiled);

    // Matches ```json header or ```json followed by a JSON object.
    private static readonly Regex FencedHeader = new(
        @"```json(?:\s+header)?\s*\r?\n(?<json>\{[\s\S]*?\})\s*\r?\n```",
        RegexOptions.Compiled);

    public static ProtocolHeaderParseResult Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new ProtocolHeaderParseResult(null, null, string.Empty);

        var lead = ExtractLeadParagraph(markdown);

        var (json, source) = LocatePayload(markdown);
        if (json is null) return new ProtocolHeaderParseResult(null, null, lead);

        try
        {
            var raw = JsonSerializer.Deserialize<RawHeader>(json, ReadOptions);
            if (raw is null)
                return new ProtocolHeaderParseResult(null, $"empty {source} payload", lead);

            if (string.IsNullOrWhiteSpace(raw.Phase))
                return new ProtocolHeaderParseResult(null, $"{source} missing phase", lead);

            if (!ProtocolPhases.TryParse(raw.Phase, out var phase))
                return new ProtocolHeaderParseResult(null, $"{source} unknown phase '{raw.Phase}'", lead);

            if (string.IsNullOrWhiteSpace(raw.Summary))
                return new ProtocolHeaderParseResult(null, $"{source} missing summary", lead);

            var header = new ProtocolHeader(
                Phase: phase,
                Summary: Truncate(raw.Summary, 240),
                NextAction: NullIfBlank(raw.NextAction is null ? null : Truncate(raw.NextAction, 240)),
                DecisionsOpen: Math.Max(0, raw.DecisionsOpen ?? 0),
                LastDecisionAt: raw.LastDecisionAt,
                CorrelationId: NullIfBlank(raw.CorrelationId),
                Agent: NullIfBlank(raw.Agent),
                Model: NullIfBlank(raw.Model),
                Runs: raw.Runs.HasValue && raw.Runs.Value < 0 ? null : raw.Runs,
                SchemaVersion: NullIfBlank(raw.SchemaVersion) ?? "1");

            return new ProtocolHeaderParseResult(header, null, lead);
        }
        catch (JsonException ex)
        {
            return new ProtocolHeaderParseResult(null, $"{source} invalid JSON: {ex.Message}", lead);
        }
    }

    private static (string? Json, string Source) LocatePayload(string markdown)
    {
        var html = HtmlCommentHeader.Match(markdown);
        if (html.Success) return (html.Groups["json"].Value, "header-comment");

        var fenced = FencedHeader.Match(markdown);
        if (fenced.Success) return (fenced.Groups["json"].Value, "header-fence");

        return (null, "");
    }

    /// <summary>
    /// First non-empty prose paragraph, capped at two lines and 480
    /// characters so the UI fallback is always renderable. Skips
    /// markdown headings, fenced blocks, and HTML comments.
    /// </summary>
    private static string ExtractLeadParagraph(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inFence = false;
        var collected = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                if (collected.Count > 0) break;
                continue;
            }
            if (inFence) continue;
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                if (collected.Count > 0) break;
                continue;
            }
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith('#')) continue;
            collected.Add(trimmed);
            if (collected.Count == 2) break;
        }
        var paragraph = string.Join(' ', collected);
        return Truncate(paragraph, 480);
    }

    private static string Truncate(string input, int max) =>
        input.Length <= max ? input : input.Substring(0, max);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record RawHeader(
        string? Phase,
        string? Summary,
        string? NextAction,
        int? DecisionsOpen,
        DateTime? LastDecisionAt,
        string? CorrelationId,
        string? Agent,
        string? Model,
        int? Runs,
        string? SchemaVersion);
}
