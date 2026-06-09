using System.Text.RegularExpressions;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Outcome of parsing the orchestrator's free-text reply for an auto-decision
/// request. Three shapes today: a regular <see cref="Reply"/> the runner
/// re-issues to the agent, a <see cref="Steer"/> message the orchestrator
/// hands back to the user when it cannot pick a path on its own but can
/// formulate a concrete unblocking ask, and <see cref="Block"/> as the
/// last-resort opaque deferral.
///
/// <para>
/// The orchestrator was previously limited to {REPLY, BLOCK}. Block was a
/// silent dead end for the user: the agent asked, the orchestrator gave up,
/// the question sat in the chat with no signal that the orchestrator had
/// thought about it. STEER is the deliberate productive escalation: a
/// one-sentence ask, a one-sentence reason, optionally a small set of
/// labelled options, so the user knows exactly what would unblock the run.
/// </para>
/// </summary>
public enum OrchestratorReplyKind
{
    /// <summary>The orchestrator produced a user-style follow-up. Re-issue as a Continue.</summary>
    Reply,
    /// <summary>The orchestrator could not decide alone but identifies a concrete unblocking ask.</summary>
    Steer,
    /// <summary>The orchestrator declined to decide. Surface the agent's question to the user.</summary>
    Block
}

/// <summary>
/// Parsed orchestrator reply. <see cref="Need"/> / <see cref="Why"/> /
/// <see cref="Options"/> are populated only on STEER. <see cref="ReplyText"/>
/// is the full reply body the runner will feed back to the agent on REPLY.
/// </summary>
public sealed record OrchestratorReply(
    OrchestratorReplyKind Kind,
    string ReplyText,
    string? Need = null,
    string? Why = null,
    IReadOnlyList<string>? Options = null,
    string? ParseWarning = null);

/// <summary>
/// Pure parser for the <c>{REPLY | STEER | BLOCK}</c> reply contract. Kept
/// out of <see cref="ProjectRunner"/> so the rule can be unit-tested in
/// isolation; same input always produces the same parse, no I/O.
///
/// <para>
/// STEER grammar:
/// <code>
/// STEER
/// Need: &lt;one-sentence specific ask&gt;
/// Why: &lt;one-sentence reasoning&gt;
/// Options:                     (optional)
///   A) ...
///   B) ...
/// </code>
/// </para>
///
/// <para>
/// Robustness rules:
/// <list type="bullet">
///   <item>The leading <c>STEER</c> token may be on its own line or followed
///   by the body on the same line. Whitespace and case are forgiving.</item>
///   <item>If <c>Need:</c> is missing the parse fails closed (BLOCK with a
///   ParseWarning) so a malformed STEER never crashes the runner. The
///   caller surfaces the warning so the user can see why the orchestrator's
///   message did not land.</item>
///   <item>Options bullets accept any of <c>A)</c>, <c>A.</c>, <c>1)</c>,
///   <c>1.</c>, <c>-</c>, <c>*</c> as the leading marker.</item>
/// </list>
/// </para>
/// </summary>
public static class OrchestratorReplyParser
{
    private static readonly Regex BlockOnly = new(@"^\s*BLOCK\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Anchor-locked detection so prose that merely mentions "STEER" or "BLOCK"
    // (e.g. "The user said BLOCK requests are common") never escalates into
    // the corresponding decision. The keyword has to live at the start of
    // the reply.
    private static readonly Regex SteerHead = new(@"^\s*STEER\b\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NeedLine = new(@"^\s*Need\s*[:\-]\s*(?<v>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WhyLine = new(@"^\s*Why\s*[:\-]\s*(?<v>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OptionsHeader = new(@"^\s*Options\s*[:\-]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OptionItem = new(
        @"^\s*(?:[A-Za-z][\)\.]|\d+[\)\.]|[-*])\s*(?<v>.+?)\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Classify a raw orchestrator reply. Empty / whitespace-only input is
    /// treated as BLOCK so the runner uses the same fallback path it used
    /// before STEER existed.
    /// </summary>
    public static OrchestratorReply Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new OrchestratorReply(OrchestratorReplyKind.Block, string.Empty);

        var trimmed = raw.Trim();

        if (BlockOnly.IsMatch(trimmed))
            return new OrchestratorReply(OrchestratorReplyKind.Block, trimmed);

        var head = SteerHead.Match(trimmed);
        if (head.Success)
        {
            var body = trimmed[head.Length..];
            return ParseSteerBody(body, fullText: trimmed);
        }

        return new OrchestratorReply(OrchestratorReplyKind.Reply, trimmed);
    }

    private static OrchestratorReply ParseSteerBody(string body, string fullText)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        string? need = null;
        string? why = null;
        var options = new List<string>();
        var inOptions = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                // Blank lines do not break the Options block; the
                // orchestrator may emit "Options:\n\n  A) ...".
                continue;
            }

            var needMatch = NeedLine.Match(line);
            if (needMatch.Success)
            {
                need ??= needMatch.Groups["v"].Value.Trim();
                inOptions = false;
                continue;
            }

            var whyMatch = WhyLine.Match(line);
            if (whyMatch.Success)
            {
                why ??= whyMatch.Groups["v"].Value.Trim();
                inOptions = false;
                continue;
            }

            if (OptionsHeader.IsMatch(line))
            {
                inOptions = true;
                continue;
            }

            if (inOptions)
            {
                var optMatch = OptionItem.Match(line);
                if (optMatch.Success)
                {
                    options.Add(optMatch.Groups["v"].Value.Trim());
                }
                // Non-matching prose under "Options:" is ignored rather than
                // bleeding into another field. Keeps the parse robust against
                // light commentary the orchestrator might add.
            }
        }

        if (string.IsNullOrWhiteSpace(need))
        {
            // Malformed STEER: fall back to BLOCK so the orchestrator
            // does not strand the user with an opaque "STEER" line.
            return new OrchestratorReply(
                OrchestratorReplyKind.Block,
                fullText,
                ParseWarning: "Orchestrator emitted STEER but no Need: line was found.");
        }

        return new OrchestratorReply(
            Kind: OrchestratorReplyKind.Steer,
            ReplyText: fullText,
            Need: need,
            Why: string.IsNullOrWhiteSpace(why) ? null : why,
            Options: options.Count > 0 ? options : null);
    }

    /// <summary>
    /// Format a parsed steer message for the chat log. Emits Markdown so the
    /// frontend's existing orchestrator-stream renderer picks the structure
    /// up without a new transport channel.
    /// </summary>
    public static string FormatSteerForChat(OrchestratorReply reply)
    {
        if (reply.Kind != OrchestratorReplyKind.Steer)
            throw new ArgumentException("FormatSteerForChat called with a non-steer reply.", nameof(reply));

        var sb = new System.Text.StringBuilder();
        sb.Append("**Need:** ").Append(reply.Need);
        if (!string.IsNullOrWhiteSpace(reply.Why))
        {
            sb.Append("  **Why:** ").Append(reply.Why);
        }
        if (reply.Options is { Count: > 0 })
        {
            sb.Append("  **Options:** ");
            for (var i = 0; i < reply.Options.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append((char)('A' + i)).Append(") ").Append(reply.Options[i]);
            }
        }
        return sb.ToString();
    }
}
