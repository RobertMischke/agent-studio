using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

public sealed record WikiMaintenanceResult(
    WikiMaintenanceVerdict Verdict,
    string Reason,
    string? Slug = null);

public enum WikiMaintenanceVerdict
{
    Skipped,
    Updated,
    Created,
    Error,
}

/// <summary>
/// Deterministic project-wiki maintenance for the optional
/// <c>post-wiki-maintenance</c> pipeline step. It uses the task's typed outcome
/// issue and stable log needles as cheap signals, then updates the watched
/// project's own <c>docs/common-problems</c> library without an LLM call.
/// </summary>
public sealed partial class WikiMaintenancePostStepRunner
{
    private static readonly Regex FrontmatterRegex = new(
        @"\A---\r?\n(?<body>.*?)\r?\n---\r?\n",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly ILogger<WikiMaintenancePostStepRunner> _logger;

    public WikiMaintenancePostStepRunner(ILogger<WikiMaintenancePostStepRunner> logger)
    {
        _logger = logger;
    }

    public WikiMaintenanceResult Run(
        TaskInfo task,
        WatchPathEntry entry,
        DateTime? nowUtc = null,
        EngineeringWorkstreamFrameLanguage? frameLanguage = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(entry.RootPath))
            return new WikiMaintenanceResult(WikiMaintenanceVerdict.Skipped, "project root is not configured");

        // Self-provisioning (AGT-2024): ensure the Workstream frame exists before
        // this step writes. Activating the step for a project is what creates the
        // frame - there is no separate onboarding gate. Idempotent and never
        // overwriting, so it is safe to call on every run.
        var docsRoot = Path.Combine(entry.RootPath, "docs");
        var language = frameLanguage ?? WorkstreamFrameLanguageResolver.Resolve(entry.Name, isPublicOverride: null);
        EnsureWorkstreamFrame(docsRoot, language, task, entry);

        var signal = DetectSignal(task);
        if (signal == null)
            return new WikiMaintenanceResult(WikiMaintenanceVerdict.Skipped, "no recurring-problem signal found");

        var wikiRoot = Path.Combine(docsRoot, "common-problems");
        try
        {
            var problemDir = Path.Combine(wikiRoot, signal.Slug);
            var existed = Directory.Exists(problemDir);
            Directory.CreateDirectory(problemDir);
            EnsureProblemFiles(problemDir, signal, task, now);
            UpsertReadme(Path.Combine(problemDir, "README.md"), signal, task, now, existed);
            AppendOccurrence(Path.Combine(problemDir, "occurrences.md"), signal, task, entry, now);
            RegenerateIndex(wikiRoot, now);

            _logger.LogInformation(
                "Wiki maintenance {Verdict} {Project}/{JobId} slug={Slug}",
                existed ? "updated" : "created", entry.Name, task.Id, signal.Slug);

            return new WikiMaintenanceResult(
                existed ? WikiMaintenanceVerdict.Updated : WikiMaintenanceVerdict.Created,
                existed ? "updated existing problem entry" : "created problem entry",
                signal.Slug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Wiki maintenance failed for {Project}/{JobId} slug={Slug}",
                entry.Name, task.Id, signal.Slug);
            return new WikiMaintenanceResult(WikiMaintenanceVerdict.Error, ex.Message, signal.Slug);
        }
    }

    /// <summary>
    /// Runs the shared ensure-frame primitive and logs only when it actually
    /// materialized (or failed to materialize) frame shells, so a warm project
    /// where the frame already exists stays quiet.
    /// </summary>
    private void EnsureWorkstreamFrame(
        string docsRoot, EngineeringWorkstreamFrameLanguage language, TaskInfo task, WatchPathEntry entry)
    {
        var result = EngineeringWorkstreamFrameSeeder.EnsureFrame(docsRoot, language);
        if (result.CreatedAnything || result.Failed.Count > 0)
        {
            _logger.LogInformation(
                "Workstream frame ensured for {Project}/{JobId} lang={Language} {Summary} created=[{Created}]",
                entry.Name, task.Id, language, result.Summary, string.Join(", ", result.Created));
        }
    }

    private static WikiProblemSignal? DetectSignal(TaskInfo task)
    {
        if (task.OutcomeIssue != null && !string.IsNullOrWhiteSpace(task.OutcomeIssue.Kind))
        {
            var kind = task.OutcomeIssue.Kind.Trim();
            var slug = Slugify(kind);
            return new WikiProblemSignal(
                slug,
                TitleFrom(task.OutcomeIssue.Label, task.OutcomeIssue.Summary, kind),
                Status: "open",
                Severity: SeverityFrom(task.OutcomeIssue.Severity),
                Category: CategoryFrom(kind),
                Tags: TagsFrom(kind),
                Summary: FirstNonBlank(task.OutcomeIssue.Summary, task.OutcomeIssue.Label, kind),
                RootCause: "Tracked from the task outcome issue. Add root-cause detail when the recurrence is investigated.",
                Workaround: "Search prior occurrences and measures before rerunning the same diagnosis.",
                LongTerm: "Promote the confirmed root cause into measures.md and close the entry when fixed.");
        }

        var logPath = AgentStudio.Tasks.TaskPaths.CliOutputLog(task.FolderPath);
        var log = TryReadTail(logPath, maxChars: 80_000);
        if (string.IsNullOrWhiteSpace(log)) return null;

        if (log.Contains("[codex-silent-completion]", StringComparison.OrdinalIgnoreCase)
            || log.Contains("outcome-silent-finish", StringComparison.OrdinalIgnoreCase))
        {
            return new WikiProblemSignal(
                "codex-silent-finish",
                "Codex run finishes silently without a terminal sentinel",
                "open",
                "major",
                "cli",
                ["codex", "silent-finish", "missing-terminal-sentinel", "watchdog"],
                "Codex stopped producing output without the required terminal sentinel, so the runner had to use the silent-completion recovery path.",
                "Likely a CLI/session termination or watchdog edge; inspect the run tail and the last tool call before trusting completion.",
                "Treat silent completion as suspicious and reissue when open work remains.",
                "Prefer a typed CLI completion signal over silence-based recovery.");
        }

        return null;
    }

    private static void EnsureProblemFiles(string problemDir, WikiProblemSignal signal, TaskInfo task, DateTime now)
    {
        WriteIfMissing(Path.Combine(problemDir, "protocol.md"),
            "# Root-cause protocol\n\n" +
            $"{signal.RootCause}\n\n" +
            "## Reproducer\n\nTODO: add a minimal reproducer when known.\n\n" +
            "## Logs\n\nTODO: add relevant log excerpts.\n");
        WriteIfMissing(Path.Combine(problemDir, "measures.md"),
            "# Measures\n\n" +
            "Fix attempts and their status. Status vocabulary: `tried`, `applied`, `works`, `regressed`.\n\n" +
            "| Status | Date (UTC) | Measure | Owner | Outcome |\n" +
            "|---|---|---|---|---|\n" +
            $"| tried | {now:yyyy-MM-dd} | Added/updated wiki occurrence from `{task.Id}`. | orchestrator | Evidence recorded; root-cause fix still tracked separately. |\n");
        WriteIfMissing(Path.Combine(problemDir, "ideas.md"),
            "# Ideas\n\n" +
            "Hypotheses, open questions, ruled-out approaches. Move into measures.md once attempted.\n");
        WriteIfMissing(Path.Combine(problemDir, "related.md"),
            "# Related\n\n" +
            "- Tasks: `" + task.Id + "`\n");
    }

    private static void UpsertReadme(
        string readmePath,
        WikiProblemSignal signal,
        TaskInfo task,
        DateTime now,
        bool existed)
    {
        if (!File.Exists(readmePath))
        {
            File.WriteAllText(readmePath, RenderReadme(signal, task, now, seenCount: 1), Encoding.UTF8);
            return;
        }

        var text = File.ReadAllText(readmePath, Encoding.UTF8);
        var seen = ExistingSeenCount(text);
        if (!existed) seen = 0;
        var nextSeen = Math.Max(1, seen + (OccurrenceAlreadyRecorded(Path.Combine(Path.GetDirectoryName(readmePath)!, "occurrences.md"), task.Id) ? 0 : 1));

        text = UpsertFrontmatterScalar(text, "last-seen", now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        text = UpsertFrontmatterScalar(text, "seen-count", nextSeen.ToString(CultureInfo.InvariantCulture));
        text = EnsureFrontmatterScalar(text, "id", signal.Slug);
        text = EnsureFrontmatterScalar(text, "title", Quote(signal.Title));
        text = EnsureFrontmatterScalar(text, "status", signal.Status);
        text = EnsureFrontmatterScalar(text, "first-seen", now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        text = EnsureFrontmatterScalar(text, "severity", signal.Severity);
        text = EnsureFrontmatterScalar(text, "category", signal.Category);
        text = EnsureFrontmatterList(text, "tags", signal.Tags);
        text = EnsureFrontmatterList(text, "affects", [task.ProjectName]);
        text = EnsureFrontmatterList(text, "related-tasks", [task.Id]);
        text = EnsureFrontmatterList(text, "related-adrs", []);
        File.WriteAllText(readmePath, text, Encoding.UTF8);
    }

    private static string RenderReadme(WikiProblemSignal signal, TaskInfo task, DateTime now, int seenCount)
    {
        var stamp = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return $"""
---
id: {signal.Slug}
title: {Quote(signal.Title)}
status: {signal.Status}
first-seen: {stamp}
last-seen: {stamp}
seen-count: {seenCount}
severity: {signal.Severity}
category: {signal.Category}
tags: [{string.Join(", ", signal.Tags)}]
affects:
  - "{EscapeYaml(task.ProjectName)}"
related-tasks: [{task.Id}]
related-adrs: []
---

# {signal.Title}

**What.** {signal.Summary}
**Why.** {signal.RootCause}
**Workaround.** {signal.Workaround}
**Long-term.** {signal.LongTerm}
""";
    }

    private static void AppendOccurrence(
        string occurrencesPath,
        WikiProblemSignal signal,
        TaskInfo task,
        WatchPathEntry entry,
        DateTime now)
    {
        var row = $"| {now:yyyy-MM-ddTHH:mm:ssZ} | `{task.Id}` | {CleanCell(task.CliType ?? task.Agent)} | `{CleanCell(task.FolderPath)}` | {CleanCell(signal.Summary)} |";
        if (!File.Exists(occurrencesPath))
        {
            File.WriteAllText(occurrencesPath,
                "# Occurrences\n\n" +
                "Chronological log. Newest at the top. UTC timestamps. One row per observation.\n\n" +
                "| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |\n" +
                "|---|---|---|---|---|\n" +
                row + "\n",
                Encoding.UTF8);
            return;
        }

        var text = File.ReadAllText(occurrencesPath, Encoding.UTF8);
        if (text.Contains($"`{task.Id}`", StringComparison.Ordinal)) return;
        var marker = "|---|---|---|---|---|";
        var idx = text.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            idx += marker.Length;
            text = text.Insert(idx, "\n" + row);
        }
        else
        {
            text = text.TrimEnd() + "\n" + row + "\n";
        }
        File.WriteAllText(occurrencesPath, text, Encoding.UTF8);
    }

    private static void RegenerateIndex(string commonProblemsRoot, DateTime now)
    {
        var entries = Directory.EnumerateDirectories(commonProblemsRoot)
            .Where(d => !string.Equals(Path.GetFileName(d), "archive", StringComparison.OrdinalIgnoreCase))
            .Select(d => ReadIndexEntry(d))
            .Where(e => e != null)
            .Cast<WikiIndexEntry>()
            .ToList();

        var tags = entries.SelectMany(e => e.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# Common Problems Index");
        sb.AppendLine();
        sb.AppendLine("Auto-generated by `scripts/wiki/regenerate-index.sh`. Do not edit manually.");
        sb.AppendLine();
        sb.AppendLine($"Last regenerated: {now:yyyy-MM-dd}");
        sb.AppendLine();
        EmitSection(sb, "Open", entries.Where(e => e.Status == "open"), includeFix: false);
        EmitSection(sb, "Mitigated", entries.Where(e => e.Status == "mitigated"), includeFix: false);
        EmitSection(sb, "Fixed", entries.Where(e => e.Status == "fixed"), includeFix: true);
        EmitSection(sb, "Archived", entries.Where(e => e.Status == "archived"), includeFix: false);
        sb.AppendLine("## Tag cloud");
        sb.AppendLine();
        sb.AppendLine(tags.Count == 0 ? "_None._" : string.Join(" ", tags.Select(t => $"`{t}`")));
        File.WriteAllText(Path.Combine(commonProblemsRoot, "README.md"), sb.ToString(), Encoding.UTF8);
    }

    private static void EmitSection(StringBuilder sb, string title, IEnumerable<WikiIndexEntry> rows, bool includeFix)
    {
        var ordered = rows.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList();
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        if (ordered.Count == 0)
        {
            sb.AppendLine("_None._");
            sb.AppendLine();
            return;
        }
        sb.AppendLine(includeFix
            ? "| ID | Title | Severity | Category | First seen | Last seen | Fix |"
            : "| ID | Title | Severity | Category | First seen | Last seen |");
        sb.AppendLine(includeFix
            ? "|---|---|---|---|---|---|---|"
            : "|---|---|---|---|---|---|");
        foreach (var e in ordered)
        {
            var row = $"| [{e.Id}]({e.Id}/) | {e.Title} | {e.Severity} | {e.Category} | {ShortDate(e.FirstSeen)} | {ShortDate(e.LastSeen)} |";
            sb.AppendLine(includeFix ? row + " see measures.md |" : row);
        }
        sb.AppendLine();
    }

    private static WikiIndexEntry? ReadIndexEntry(string dir)
    {
        var readme = Path.Combine(dir, "README.md");
        if (!File.Exists(readme)) return null;
        var text = File.ReadAllText(readme, Encoding.UTF8);
        var id = FrontmatterScalar(text, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new WikiIndexEntry(
            id,
            TrimQuotes(FrontmatterScalar(text, "title")) ?? id,
            FrontmatterScalar(text, "status") ?? "open",
            FrontmatterScalar(text, "severity") ?? "minor",
            FrontmatterScalar(text, "category") ?? "misc",
            FrontmatterScalar(text, "first-seen") ?? "",
            FrontmatterScalar(text, "last-seen") ?? "",
            FrontmatterInlineList(text, "tags"));
    }

    private static string? TryReadTail(string path, int maxChars)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8);
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
        catch
        {
            return null;
        }
    }

    private static void WriteIfMissing(string path, string text)
    {
        if (!File.Exists(path)) File.WriteAllText(path, text, Encoding.UTF8);
    }

    private static bool OccurrenceAlreadyRecorded(string path, string jobId) =>
        File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Contains($"`{jobId}`", StringComparison.Ordinal);

    private static int ExistingSeenCount(string text)
        => int.TryParse(FrontmatterScalar(text, "seen-count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static string EnsureFrontmatterScalar(string text, string key, string value) =>
        string.IsNullOrWhiteSpace(FrontmatterScalar(text, key)) ? UpsertFrontmatterScalar(text, key, value) : text;

    private static string EnsureFrontmatterList(string text, string key, IReadOnlyList<string> values) =>
        FrontmatterHasKey(text, key) ? text : UpsertFrontmatterScalar(text, key, values.Count == 0 ? "[]" : "[" + string.Join(", ", values.Select(EscapeYaml)) + "]");

    private static string UpsertFrontmatterScalar(string text, string key, string value)
    {
        var match = FrontmatterRegex.Match(text);
        if (!match.Success)
        {
            return $"---\n{key}: {value}\n---\n\n{text}";
        }

        var body = match.Groups["body"].Value;
        var lines = body.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(key + ":", StringComparison.Ordinal))
            {
                lines[i] = $"{key}: {value}";
                replaced = true;
                break;
            }
        }
        if (!replaced) lines.Add($"{key}: {value}");
        var next = "---\n" + string.Join("\n", lines) + "\n---\n";
        return next + text[match.Length..];
    }

    private static bool FrontmatterHasKey(string text, string key) =>
        FrontmatterScalar(text, key) != null || text.Contains("\n" + key + ":", StringComparison.Ordinal);

    private static string? FrontmatterScalar(string text, string key)
    {
        var match = FrontmatterRegex.Match(text);
        if (!match.Success) return null;
        foreach (var raw in match.Groups["body"].Value.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(key + ":", StringComparison.Ordinal))
                return line[(key.Length + 1)..].Trim();
        }
        return null;
    }

    private static IReadOnlyList<string> FrontmatterInlineList(string text, string key)
    {
        var raw = FrontmatterScalar(text, key);
        if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith("[", StringComparison.Ordinal)) return [];
        return raw.Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TrimQuotes)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToList();
    }

    private static string Slugify(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    private static string TitleFrom(string? label, string? summary, string fallback) =>
        FirstNonBlank(label, summary, fallback).TrimEnd('.');

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static string SeverityFrom(string? severity) =>
        string.Equals(severity, "High", StringComparison.OrdinalIgnoreCase) ? "major"
        : string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase) ? "major"
        : string.Equals(severity, "Warn", StringComparison.OrdinalIgnoreCase) ? "minor"
        : "minor";

    private static string CategoryFrom(string kind)
    {
        var k = kind.ToLowerInvariant();
        if (k.Contains("permission") || k.Contains("eacces")) return "permission";
        if (k.Contains("filesystem") || k.Contains("folder") || k.Contains("orphan")) return "filesystem";
        if (k.Contains("cli") || k.Contains("codex") || k.Contains("claude") || k.Contains("copilot")) return "cli";
        if (k.Contains("runner") || k.Contains("watchdog") || k.Contains("sentinel")) return "runner";
        if (k.Contains("state") || k.Contains("verdict") || k.Contains("classifier")) return "state-machine";
        return "misc";
    }

    private static IReadOnlyList<string> TagsFrom(string kind) =>
        kind.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Slugify)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

    private static string Quote(string value) => "\"" + EscapeYaml(value) + "\"";
    private static string EscapeYaml(string value) => value.Replace("\"", "\\\"");
    private static string? TrimQuotes(string? value) => value?.Trim().Trim('"');
    private static string ShortDate(string value) => value.Contains('T') ? value[..value.IndexOf('T')] : value;
    private static string CleanCell(string value) => value.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();

    private sealed record WikiProblemSignal(
        string Slug,
        string Title,
        string Status,
        string Severity,
        string Category,
        IReadOnlyList<string> Tags,
        string Summary,
        string RootCause,
        string Workaround,
        string LongTerm);

    private sealed record WikiIndexEntry(
        string Id,
        string Title,
        string Status,
        string Severity,
        string Category,
        string FirstSeen,
        string LastSeen,
        IReadOnlyList<string> Tags);
}
