using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Projection.Sources;

/// <summary>
/// Projects <c>logs/cli-output.log</c> into typed conversation events.
///
/// This is the source that the F22 prompt was queued against, so two
/// behaviors are non-negotiable:
/// <list type="bullet">
/// <item>
///   The CLI sometimes writes a JSON-encoded body inline on a single log
///   line, leaving real newlines escaped as the two-character sequence
///   <c>\n</c>. The frontend used to render those as literal backslash-n.
///   <see cref="UnescapeStreamJsonBody"/> decodes them once on the way in so
///   markdown sees real <c>\n</c> characters and paragraphs break the way
///   the operator expects.
/// </item>
/// <item>
///   Each emitted event must keep a stable id (timestamp + stream + line)
///   so the projector's delta-comparison logic does not see spurious
///   "new" events on every re-projection.
/// </item>
/// </list>
///
/// The classification here is intentionally narrower than the TS
/// <c>conversation-projection.ts</c> (no toolBurst merging yet). Bringing
/// the burst-window heuristics across is a follow-up; the current shape
/// already gives the client a stream of typed events with rendered HTML.
/// </summary>
public sealed partial class CliOutputSource : IConversationEventSource
{
    public string SourceKind => "cli";

    public Task<IReadOnlyList<RawSourceEvent>> ReadAsync(JobInfo jobInfo, CancellationToken ct)
    {
        var path = GetLogPath(jobInfo);
        if (path is null || !File.Exists(path))
        {
            return Task.FromResult<IReadOnlyList<RawSourceEvent>>(Array.Empty<RawSourceEvent>());
        }

        var lines = CliOutputLogParser.ParseFile(path);
        var events = new List<RawSourceEvent>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ev = Classify(lines[i], i);
            if (ev is not null) events.Add(ev);
        }
        return Task.FromResult<IReadOnlyList<RawSourceEvent>>(events);
    }

    public DateTime GetSourceMTimeUtc(JobInfo jobInfo)
    {
        var path = GetLogPath(jobInfo);
        if (path is null || !File.Exists(path)) return DateTime.MinValue;
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static string? GetLogPath(JobInfo jobInfo)
    {
        if (string.IsNullOrWhiteSpace(jobInfo.FolderPath)) return null;
        return Path.Combine(jobInfo.FolderPath, "logs", "cli-output.log");
    }

    internal static RawSourceEvent? Classify(CliOutputLine line, int idx)
    {
        var text = line.Text ?? string.Empty;
        var ts = line.Timestamp;
        var id = $"cli:{ts:O}:{idx}";
        var bodyMd = UnescapeStreamJsonBody(text);

        switch (line.Stream)
        {
            case "user":
                return new RawSourceEvent
                {
                    Id = id,
                    Kind = "message.user",
                    SourceKind = "cli",
                    Role = "user",
                    TimestampUtc = ts,
                    BodyMarkdown = bodyMd,
                    Summary = TrimToSingleLine(bodyMd)
                };

            case "orchestrator":
                return ClassifyOrchestrator(text, bodyMd, ts, id);

            case "supervisor":
                return new RawSourceEvent
                {
                    Id = id,
                    Kind = "message.supervisor",
                    SourceKind = "cli",
                    Role = "supervisor",
                    TimestampUtc = ts,
                    BodyMarkdown = bodyMd,
                    Severity = ProjectedEventSeverity.Info,
                    Summary = TrimToSingleLine(bodyMd)
                };

            case "stderr":
                return new RawSourceEvent
                {
                    Id = id,
                    Kind = "message.taskAgent",
                    SourceKind = "cli",
                    Role = "agent",
                    TimestampUtc = ts,
                    BodyMarkdown = bodyMd,
                    Severity = ProjectedEventSeverity.Warn,
                    Summary = TrimToSingleLine(bodyMd)
                };

            default: // stdout, system, anything else
                return new RawSourceEvent
                {
                    Id = id,
                    Kind = "message.taskAgent",
                    SourceKind = "cli",
                    Role = "agent",
                    TimestampUtc = ts,
                    BodyMarkdown = bodyMd,
                    Summary = TrimToSingleLine(bodyMd)
                };
        }
    }

    private static RawSourceEvent ClassifyOrchestrator(string text, string bodyMd, DateTime ts, string id)
    {
        if (WatchdogPrefix().IsMatch(text))
        {
            // Default severity for any [watchdog] message is Warn: the watchdog
            // only logs when something is off (quiet stream, missing capture).
            // Escalate to Error on confirmed kills.
            var sev = text.Contains("killed", StringComparison.OrdinalIgnoreCase)
                ? ProjectedEventSeverity.Error
                : ProjectedEventSeverity.Warn;
            return new RawSourceEvent
            {
                Id = id,
                Kind = "supervisor.wait",
                SourceKind = "cli",
                Role = "supervisor",
                TimestampUtc = ts,
                BodyMarkdown = bodyMd,
                Severity = sev,
                Summary = TrimToSingleLine(bodyMd)
            };
        }
        if (CaptureFailPrefix().IsMatch(text))
        {
            return new RawSourceEvent
            {
                Id = id,
                Kind = "system.captureFail",
                SourceKind = "cli",
                Role = "orchestrator",
                TimestampUtc = ts,
                BodyMarkdown = bodyMd,
                Severity = ProjectedEventSeverity.Warn,
                Summary = TrimToSingleLine(bodyMd)
            };
        }
        if (SchemaDriftPrefix().IsMatch(text))
        {
            return new RawSourceEvent
            {
                Id = id,
                Kind = "system.schemaDrift",
                SourceKind = "cli",
                Role = "orchestrator",
                TimestampUtc = ts,
                BodyMarkdown = bodyMd,
                Severity = ProjectedEventSeverity.Warn,
                Summary = TrimToSingleLine(bodyMd)
            };
        }
        if (BracketPrefix().IsMatch(text))
        {
            return new RawSourceEvent
            {
                Id = id,
                Kind = "decision.orchestrator",
                SourceKind = "cli",
                Role = "orchestrator",
                TimestampUtc = ts,
                BodyMarkdown = bodyMd,
                Summary = TrimToSingleLine(bodyMd)
            };
        }
        return new RawSourceEvent
        {
            Id = id,
            Kind = "message.orchestrator",
            SourceKind = "cli",
            Role = "orchestrator",
            TimestampUtc = ts,
            BodyMarkdown = bodyMd,
            Summary = TrimToSingleLine(bodyMd)
        };
    }

    /// <summary>
    /// F22 newline fix. The Claude CLI occasionally JSON-encodes a message
    /// body before writing it as a single log line, which leaves real
    /// newlines as the two-character escape <c>\n</c>. We decode the
    /// standard set of JSON-style escapes here so markdown sees real
    /// characters; otherwise the frontend used to show literal "\n" in the
    /// rendered bubble.
    /// </summary>
    internal static string UnescapeStreamJsonBody(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!text.Contains('\\')) return text;

        // Only decode when the line carries the F22 trigger: a literal `\n`
        // somewhere in the body. Without that signal we assume the line is
        // ordinary text (e.g. a Windows path like C:\Users\rmisc\file.txt)
        // and a blanket sweep over \r / \t / \\ would corrupt it.
        if (!ContainsLiteralBackslashN(text)) return text;

        // Walk char-by-char so we never double-decode a real backslash that
        // the user wrote intentionally (e.g. a Windows path inside a code
        // fence). The sequence \\ collapses to a single backslash; anything
        // not in the JSON whitelist passes through unchanged.
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i == text.Length - 1)
            {
                sb.Append(c);
                continue;
            }
            var next = text[i + 1];
            switch (next)
            {
                case 'n': sb.Append('\n'); i++; break;
                case 'r': sb.Append('\r'); i++; break;
                case 't': sb.Append('\t'); i++; break;
                case '"': sb.Append('"'); i++; break;
                case '\\': sb.Append('\\'); i++; break;
                default: sb.Append(c); break; // leave the backslash + next char alone
            }
        }
        return sb.ToString();
    }

    private static bool ContainsLiteralBackslashN(string text)
    {
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '\\' && text[i + 1] == 'n') return true;
        }
        return false;
    }

    private static string TrimToSingleLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var firstBreak = s.IndexOfAny(['\r', '\n']);
        var oneLine = firstBreak >= 0 ? s[..firstBreak] : s;
        return oneLine.Length > 160 ? oneLine[..160] + "…" : oneLine;
    }

    [GeneratedRegex(@"^\s*\[watchdog[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex WatchdogPrefix();

    [GeneratedRegex(@"^\s*\[capture-fail\]", RegexOptions.IgnoreCase)]
    private static partial Regex CaptureFailPrefix();

    [GeneratedRegex(@"^\s*\[schema-drift\]", RegexOptions.IgnoreCase)]
    private static partial Regex SchemaDriftPrefix();

    [GeneratedRegex(@"^\s*\[[^\]]+\]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketPrefix();
}
