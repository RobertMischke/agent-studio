using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>One distilled per-aspect orchestrator-review finding for the learnings page.</summary>
public sealed record WikiLearningFinding(string Aspect, string Verdict, string Reason);

/// <summary>
/// The structured run evidence the orchestrator already has in hand when the
/// post-bracket runs. The learnings step distills these into the page; keeping
/// it a plain record (no runner-namespace types) keeps the runner decoupled and
/// unit-testable without standing up an aspect run.
/// </summary>
public sealed record WikiLearningsRun(
    string Verdict,
    string? VerdictReason,
    IReadOnlyList<WikiLearningFinding> Findings,
    string? AgentNotes,
    string? StumblingBlock,
    string? ChangedSummary);

public sealed record WikiLearningsResult(
    WikiLearningsVerdict Verdict,
    string Reason,
    string? Slug = null);

public enum WikiLearningsVerdict
{
    Skipped,
    Updated,
    Created,
    Error,
}

/// <summary>
/// Deterministic, CLI-agnostic project-wiki distillation for the optional
/// <c>post-wiki-learnings</c> pipeline step. After a task's review settles it
/// folds the derived verdict, the per-aspect orchestrator-review findings, the
/// agent's own close-out notes, and any typed outcome stumbling block into a
/// per-task page under <c>docs/wiki/learnings/&lt;task&gt;.md</c> and regenerates
/// the learnings index - no LLM call. It is idempotent: each distilled run carries
/// a stable signature so a re-invocation on the same run state refreshes the page
/// timestamp instead of duplicating, while a genuine reissue (new signature)
/// prepends a fresh dated run block so nothing is lost and git keeps the history.
/// </summary>
public sealed class WikiLearningsPostStepRunner
{
    private static readonly Regex FrontmatterRegex = new(
        @"\A---\r?\n(?<body>.*?)\r?\n---\r?\n",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private const string SignatureMarkerPrefix = "<!-- wiki-learnings-sig: ";

    private readonly ILogger<WikiLearningsPostStepRunner> _logger;

    public WikiLearningsPostStepRunner(ILogger<WikiLearningsPostStepRunner> logger)
    {
        _logger = logger;
    }

    public WikiLearningsResult Run(
        TaskInfo task,
        WatchPathEntry entry,
        WikiLearningsRun run,
        DateTime? nowUtc = null,
        EngineeringWorkstreamFrameLanguage? frameLanguage = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(entry.RootPath))
            return new WikiLearningsResult(WikiLearningsVerdict.Skipped, "project root is not configured");

        // Self-provisioning (AGT-2024): ensure the Workstream frame exists before
        // this step writes. Activating the step for a project is what creates the
        // structure - the old "skip when docs/wiki is missing" gate is gone, since
        // an enabled step now bootstraps its own home under docs/. Idempotent and
        // never overwriting.
        var docsRoot = Path.Combine(entry.RootPath, "docs");
        var language = frameLanguage ?? WorkstreamFrameLanguageResolver.Resolve(entry.Name, isPublicOverride: null);
        EnsureWorkstreamFrame(docsRoot, language, task, entry);

        var slug = PageSlug(task);
        if (string.IsNullOrWhiteSpace(slug))
            return new WikiLearningsResult(WikiLearningsVerdict.Skipped, "task has no usable id for a page slug");

        try
        {
            var learningsRoot = Path.Combine(docsRoot, "wiki", "learnings");
            Directory.CreateDirectory(learningsRoot);

            var pagePath = Path.Combine(learningsRoot, slug + ".md");
            var signature = RunSignature(task, run);
            var verdict = UpsertPage(pagePath, task, run, signature, now);
            RegenerateIndex(learningsRoot, now);

            _logger.LogInformation(
                "Wiki learnings {Verdict} {Project}/{JobId} slug={Slug} sig={Sig}",
                verdict.ToString().ToLowerInvariant(), entry.Name, task.Id, slug, signature);

            return new WikiLearningsResult(
                verdict,
                verdict == WikiLearningsVerdict.Created ? "created learnings page" : "updated learnings page",
                slug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Wiki learnings failed for {Project}/{JobId} slug={Slug}",
                entry.Name, task.Id, slug);
            return new WikiLearningsResult(WikiLearningsVerdict.Error, ex.Message, slug);
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

    private static WikiLearningsVerdict UpsertPage(
        string pagePath,
        TaskInfo task,
        WikiLearningsRun run,
        string signature,
        DateTime now)
    {
        var stamp = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var runBlock = RenderRunBlock(run, signature, stamp);

        if (!File.Exists(pagePath))
        {
            File.WriteAllText(pagePath, RenderNewPage(task, run, runBlock, stamp), Encoding.UTF8);
            return WikiLearningsVerdict.Created;
        }

        var text = File.ReadAllText(pagePath, Encoding.UTF8);

        // Idempotent re-run on the same run state: the signature is already on
        // the page, so refresh last-distilled only and leave the run blocks and
        // count untouched (mirrors the occurrence-dedupe of wiki-maintenance).
        if (text.Contains(SignatureMarkerPrefix + signature, StringComparison.Ordinal))
        {
            text = UpsertFrontmatterScalar(text, "last-distilled", stamp);
            text = UpsertFrontmatterScalar(text, "status", run.Verdict);
            File.WriteAllText(pagePath, text, Encoding.UTF8);
            return WikiLearningsVerdict.Updated;
        }

        var nextCount = ExistingRunCount(text) + 1;
        text = InsertRunBlock(text, runBlock);
        text = UpsertFrontmatterScalar(text, "last-distilled", stamp);
        text = UpsertFrontmatterScalar(text, "status", run.Verdict);
        text = UpsertFrontmatterScalar(text, "run-count", nextCount.ToString(CultureInfo.InvariantCulture));
        File.WriteAllText(pagePath, text, Encoding.UTF8);
        return WikiLearningsVerdict.Updated;
    }

    private static string RenderNewPage(TaskInfo task, WikiLearningsRun run, string runBlock, string stamp)
    {
        var title = string.IsNullOrWhiteSpace(task.Title) ? (task.Key ?? task.Id) : task.Title.Trim();
        var key = task.Key ?? task.Id;
        return $"""
---
id: {PageSlug(task)}
title: {Quote(title)}
task-key: {key}
task-id: {task.Id}
project: {EscapeYaml(task.ProjectName)}
type: {EscapeYaml(task.TaskType)}
status: {run.Verdict}
first-distilled: {stamp}
last-distilled: {stamp}
run-count: 1
tags: [{string.Join(", ", DistinctTags(task))}]
---

# Learnings: {title}

> Auto-distilled by the `post-wiki-learnings` pipeline step after each run.
> Newest run on top. Operators and future agent instances read this for the
> "what changed and why" of this task without re-reading the whole log.

{runBlock}
""";
    }

    private static string RenderRunBlock(WikiLearningsRun run, string signature, string stamp)
    {
        var sb = new StringBuilder();
        sb.Append("## Run ").Append(stamp).Append(" - ").AppendLine(VerdictLabel(run.Verdict));
        sb.Append(SignatureMarkerPrefix).Append(signature).AppendLine(" -->");
        sb.AppendLine();
        sb.Append("**Outcome.** ").AppendLine(OutcomeSentence(run));
        sb.AppendLine();
        sb.AppendLine("**Review findings.**");
        if (run.Findings.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("_No aspect verdicts were recorded for this run._");
        }
        else
        {
            sb.AppendLine();
            foreach (var f in run.Findings)
                sb.Append("- **").Append(CleanInline(f.Aspect)).Append("** [").Append(CleanInline(f.Verdict))
                  .Append("]: ").AppendLine(string.IsNullOrWhiteSpace(f.Reason) ? "_no summary_" : CleanInline(f.Reason));
        }
        sb.AppendLine();
        sb.Append("**Stumbling blocks.** ")
          .AppendLine(string.IsNullOrWhiteSpace(run.StumblingBlock) ? "_None recorded._" : CleanInline(run.StumblingBlock));
        sb.AppendLine();
        sb.Append("**Agent notes.** ")
          .AppendLine(string.IsNullOrWhiteSpace(run.AgentNotes) ? "_None recorded._" : CleanInline(run.AgentNotes));
        sb.AppendLine();
        sb.Append("**Changed.** ")
          .AppendLine(string.IsNullOrWhiteSpace(run.ChangedSummary) ? "_No commit recorded for this run._" : CleanInline(run.ChangedSummary));
        return sb.ToString().TrimEnd();
    }

    private static string InsertRunBlock(string text, string runBlock)
    {
        // Newest run on top: insert before the first existing run heading; if
        // none exists yet (corrupted/legacy page), append at the end.
        var idx = text.IndexOf("\n## Run ", StringComparison.Ordinal);
        if (idx >= 0)
            return text[..(idx + 1)] + runBlock + "\n\n" + text[(idx + 1)..];
        return text.TrimEnd() + "\n\n" + runBlock + "\n";
    }

    private static void RegenerateIndex(string learningsRoot, DateTime now)
    {
        var pages = Directory.EnumerateFiles(learningsRoot, "*.md")
            .Where(p => !string.Equals(Path.GetFileName(p), "README.md", StringComparison.OrdinalIgnoreCase))
            .Select(ReadIndexEntry)
            .Where(e => e != null)
            .Cast<WikiLearningIndexEntry>()
            .OrderByDescending(e => e.LastDistilled, StringComparer.Ordinal)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Project Learnings");
        sb.AppendLine();
        sb.AppendLine("Auto-generated by the `post-wiki-learnings` pipeline step. Do not edit manually.");
        sb.AppendLine("One page per task; newest update on top.");
        sb.AppendLine();
        sb.AppendLine($"Last regenerated: {now:yyyy-MM-dd}");
        sb.AppendLine();
        if (pages.Count == 0)
        {
            sb.AppendLine("_No learnings distilled yet._");
        }
        else
        {
            sb.AppendLine("| Task | Title | Type | Latest verdict | Runs | Last updated |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var e in pages)
                sb.AppendLine($"| [{e.Key}]({e.Id}.md) | {CleanInline(e.Title)} | {e.Type} | {e.Status} | {e.RunCount} | {ShortDate(e.LastDistilled)} |");
        }
        File.WriteAllText(Path.Combine(learningsRoot, "README.md"), sb.ToString(), Encoding.UTF8);
    }

    private static WikiLearningIndexEntry? ReadIndexEntry(string pagePath)
    {
        var text = File.ReadAllText(pagePath, Encoding.UTF8);
        var id = FrontmatterScalar(text, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new WikiLearningIndexEntry(
            id,
            FrontmatterScalar(text, "task-key") ?? id,
            TrimQuotes(FrontmatterScalar(text, "title")) ?? id,
            FrontmatterScalar(text, "type") ?? "chore",
            FrontmatterScalar(text, "status") ?? "n/a",
            FrontmatterScalar(text, "run-count") ?? "1",
            FrontmatterScalar(text, "last-distilled") ?? "");
    }

    private static string RunSignature(TaskInfo task, WikiLearningsRun run)
    {
        // Prefer the newest attributed commit: it changes exactly when the run
        // produced real work, so re-distilling an unchanged run is a no-op while
        // a genuine reissue (new commit) appends a fresh block. With no commit
        // (read-only / no-change runs) fall back to a content hash of the
        // distilled signals so the dedupe still holds.
        var newest = task.Commits.Count > 0 ? task.Commits[^1] : task.Commit;
        if (newest != null && !string.IsNullOrWhiteSpace(newest.Sha))
            return ShortHash(newest.Sha);

        var material = string.Join("",
            run.Verdict,
            run.VerdictReason ?? "",
            run.StumblingBlock ?? "",
            run.AgentNotes ?? "",
            run.ChangedSummary ?? "",
            string.Join("|", run.Findings.Select(f => $"{f.Aspect}:{f.Verdict}:{f.Reason}")));
        return ShortHash(material);
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 5).ToLowerInvariant();
    }

    private static string OutcomeSentence(WikiLearningsRun run)
    {
        var label = VerdictLabel(run.Verdict);
        return string.IsNullOrWhiteSpace(run.VerdictReason)
            ? label + "."
            : label + " - " + CleanInline(run.VerdictReason);
    }

    private static string VerdictLabel(string verdict) => verdict switch
    {
        "accept" => "Accepted",
        "accept-with-concerns" => "Accepted with concerns",
        "reissue" => "Reissued",
        "escalate" => "Escalated",
        _ => string.IsNullOrWhiteSpace(verdict) ? "Recorded" : verdict,
    };

    private static IReadOnlyList<string> DistinctTags(TaskInfo task)
        => task.Tags
            .Select(t => t?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(EscapeYaml)
            .ToList();

    private static string PageSlug(TaskInfo task) => Slugify(task.Key ?? task.Id);

    private static int ExistingRunCount(string text)
    {
        var fromFrontmatter = FrontmatterScalar(text, "run-count");
        if (int.TryParse(fromFrontmatter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
            return n;
        // Fall back to counting run headings so a page that lost its counter
        // still increments rather than resetting.
        return Math.Max(0, Regex.Matches(text, @"^## Run ", RegexOptions.Multiline).Count);
    }

    private static string UpsertFrontmatterScalar(string text, string key, string value)
    {
        var match = FrontmatterRegex.Match(text);
        if (!match.Success)
            return $"---\n{key}: {value}\n---\n\n{text}";

        var lines = match.Groups["body"].Value.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
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
        return "---\n" + string.Join("\n", lines) + "\n---\n" + text[match.Length..];
    }

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

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder();
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    private static string Quote(string value) => "\"" + EscapeYaml(value) + "\"";
    private static string EscapeYaml(string value) => (value ?? string.Empty).Replace("\"", "\\\"");
    private static string? TrimQuotes(string? value) => value?.Trim().Trim('"');
    private static string ShortDate(string value) => value.Contains('T') ? value[..value.IndexOf('T')] : value;

    private static string CleanInline(string value)
    {
        var oneLine = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Replace("|", "\\|");
    }

    private sealed record WikiLearningIndexEntry(
        string Id,
        string Key,
        string Title,
        string Type,
        string Status,
        string RunCount,
        string LastDistilled);
}
