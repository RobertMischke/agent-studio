using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

public enum AgentsWikiSyncVerdict
{
    Skipped,
    Created,
    Updated,
    Error,
}

/// <summary>
/// Outcome of one <see cref="AgentsWikiSyncPostStepRunner.Run"/> pass. Reporting
/// only: the counts and <see cref="Findings"/> surface in the pipeline step
/// verdict/reason and structured logs, and never gate the task lane decision.
/// </summary>
public sealed record AgentsWikiSyncResult(
    AgentsWikiSyncVerdict Verdict,
    string Reason,
    int TopicCount = 0,
    int MatchedTopics = 0,
    int MissingPages = 0,
    string AgentsPointer = "n/a",
    IReadOnlyList<string>? Findings = null);

/// <summary>
/// Deterministic project-wiki upkeep for the optional
/// <c>post-agents-wiki-sync</c> pipeline step. It keeps the AGENTS.md -&gt; wiki
/// pointers for a set of <b>designated topics</b> consistent (no dead / missing
/// link) and, for each designated topic, maintains a machine-owned
/// "Current State / Progress" page in the wiki so agents read the current state
/// of a topic instead of re-discovering it every run ("gegen im Kreis drehen").
///
/// <para>
/// The step is deterministic (no LLM call, like the sibling
/// <see cref="WikiMaintenancePostStepRunner"/> and
/// <see cref="WikiLearningsPostStepRunner"/>): the per-topic current-state line is
/// derived from the task's own evidence (title, newest attributed commit, typed
/// outcome issue), and topic relevance is decided by shared tags or changed-file
/// path prefixes. It is opt-in per project and self-provisioning: activating it
/// seeds an empty designated-topics registry the operator fills in.
/// </para>
///
/// <para>
/// It writes only under <c>docs/concepts/designated-topics/</c> plus, when
/// self-healing a missing pointer, a single managed block at the end of the
/// project's <c>AGENTS.md</c>. It never edits a hand-maintained concept page in
/// place (those pages are HTML/Markdown owned by humans); the machine-maintained
/// current-state block lives in the sibling per-topic page and the concept page is
/// referenced by a validated pointer.
/// </para>
/// </summary>
public sealed class AgentsWikiSyncPostStepRunner
{
    /// <summary>Wiki-root-relative folder that holds the registry + generated pages.</summary>
    public const string TopicsFolderRel = "concepts/designated-topics";

    /// <summary>Repo-relative index the AGENTS.md pointer targets.</summary>
    public const string IndexRepoRel = "docs/concepts/designated-topics/README.md";

    private const string AgentsBeginMarker = "<!-- designated-topics:begin (managed by post-agents-wiki-sync) -->";
    private const string AgentsEndMarker = "<!-- designated-topics:end -->";

    /// <summary>Max progress rows kept per topic; oldest fall off so pages stay bounded.</summary>
    private const int MaxEntriesPerTopic = 25;

    private static readonly Regex FrontmatterRegex = new(
        @"\A---\r?\n(?<body>.*?)\r?\n---\r?\n",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions RegistryReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<AgentsWikiSyncPostStepRunner> _logger;

    public AgentsWikiSyncPostStepRunner(ILogger<AgentsWikiSyncPostStepRunner> logger)
    {
        _logger = logger;
    }

    public AgentsWikiSyncResult Run(
        TaskInfo task,
        WatchPathEntry entry,
        IReadOnlyList<string>? changedFiles = null,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(entry.RootPath))
            return new AgentsWikiSyncResult(AgentsWikiSyncVerdict.Skipped, "project root is not configured");

        // Self-provisioning (AGT-2024): the step bootstraps its own home under
        // docs/, exactly like the sibling wiki steps. Idempotent and never
        // overwriting.
        var docsRoot = Path.Combine(entry.RootPath, "docs");

        try
        {
            var topicsFolder = Path.Combine(docsRoot, ToNative(TopicsFolderRel));
            Directory.CreateDirectory(topicsFolder);

            var registryPath = Path.Combine(topicsFolder, "registry.json");
            var topics = LoadOrSeedRegistry(registryPath, out var seeded);

            var indexExisted = File.Exists(Path.Combine(topicsFolder, "README.md"));
            var findings = new List<string>();

            if (topics.Count == 0)
            {
                // Regenerate an empty index so the pointer target always resolves,
                // then stop: there is nothing to collect until the operator adds a
                // designated topic to the registry. A fresh seed is a real side
                // effect (Created); an already-empty registry is a no-op (Skipped).
                RegenerateIndex(topicsFolder, [], now);
                return seeded
                    ? new AgentsWikiSyncResult(AgentsWikiSyncVerdict.Created,
                        "seeded empty designated-topics registry; add topics to enable the sync", TopicCount: 0)
                    : new AgentsWikiSyncResult(AgentsWikiSyncVerdict.Skipped,
                        "no designated topics configured in registry.json", TopicCount: 0);
            }

            var matched = 0;
            var missing = 0;
            var rows = new List<IndexRow>(topics.Count);

            foreach (var topic in topics)
            {
                var pageAbs = Path.Combine(entry.RootPath, ToNative(topic.Page));
                var pageExists = File.Exists(pageAbs);
                if (!pageExists)
                {
                    missing++;
                    findings.Add($"designated topic '{topic.Slug}' points at a missing wiki page: {topic.Page}");
                }

                var (isMatch, matchedBy) = Match(task, changedFiles, topic);
                if (isMatch) matched++;

                var statePath = Path.Combine(topicsFolder, topic.Slug + ".md");
                UpsertStatePage(statePath, topicsFolder, entry.RootPath, topic, task, isMatch, matchedBy, pageExists, now);

                var stateText = File.ReadAllText(statePath, Utf8NoBom);
                rows.Add(new IndexRow(
                    topic,
                    pageExists,
                    RelLink(topicsFolder, entry.RootPath, topic.Page),
                    TrimQuotes(FrontmatterScalar(stateText, "state-note")) ?? "No task activity recorded yet.",
                    FrontmatterScalar(stateText, "last-task") ?? "-",
                    FrontmatterScalar(stateText, "last-synced") ?? ""));
            }

            RegenerateIndex(topicsFolder, rows, now);

            var agentsPointer = EnsureAgentsPointer(entry.RootPath, findings);

            _logger.LogInformation(
                "Agents-wiki-sync {Verdict} {Project}/{JobId} topics={Topics} matched={Matched} missingPages={Missing} agentsPointer={Pointer}",
                indexExisted ? "updated" : "created", entry.Name, task.Id, topics.Count, matched, missing, agentsPointer);

            var summary =
                $"synced {topics.Count} designated topic(s): {matched} matched this task, {missing} missing page(s); AGENTS pointer {agentsPointer}";
            return new AgentsWikiSyncResult(
                indexExisted ? AgentsWikiSyncVerdict.Updated : AgentsWikiSyncVerdict.Created,
                summary,
                TopicCount: topics.Count,
                MatchedTopics: matched,
                MissingPages: missing,
                AgentsPointer: agentsPointer,
                Findings: findings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Agents-wiki-sync failed for {Project}/{JobId}", entry.Name, task.Id);
            return new AgentsWikiSyncResult(AgentsWikiSyncVerdict.Error, ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Registry
    // ------------------------------------------------------------------

    /// <summary>
    /// Loads the designated-topics registry. When the file is absent it seeds a
    /// documented empty template (self-provisioning) and reports zero topics; a
    /// malformed file is treated as zero topics rather than throwing so a single
    /// bad edit never breaks the reporting-only step.
    /// </summary>
    private IReadOnlyList<DesignatedTopic> LoadOrSeedRegistry(string registryPath, out bool seeded)
    {
        seeded = false;
        if (!File.Exists(registryPath))
        {
            File.WriteAllText(registryPath, SeedRegistryJson(), Utf8NoBom);
            seeded = true;
            return [];
        }

        try
        {
            var dto = JsonSerializer.Deserialize<RegistryDto>(File.ReadAllText(registryPath, Utf8NoBom), RegistryReadOptions);
            if (dto?.Topics == null) return [];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var topics = new List<DesignatedTopic>();
            foreach (var t in dto.Topics)
            {
                var slug = Slugify(t.Slug ?? "");
                if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(t.Page)) continue;
                if (!seen.Add(slug)) continue; // first entry wins on duplicate slug
                topics.Add(new DesignatedTopic(
                    slug,
                    string.IsNullOrWhiteSpace(t.Title) ? slug : t.Title!.Trim(),
                    NormalizeRel(t.Page!),
                    CleanList(t.Tags),
                    (t.PathPrefixes ?? []).Select(NormalizeRel).Where(p => p.Length > 0).ToList()));
            }
            return topics;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Agents-wiki-sync: designated-topics registry.json is malformed; treating as no topics");
            return [];
        }
    }

    private static string SeedRegistryJson()
    {
        var dto = new RegistryDto
        {
            Note = "Designated topics for the post-agents-wiki-sync pipeline step. Each entry pins an "
                 + "AGENTS-surface pointer to a docs/concepts page and a machine-maintained "
                 + "'Current State / Progress' page in this folder, so agents read the current state of a "
                 + "topic instead of re-discovering it. A task is matched to a topic by shared tags or by a "
                 + "changed-file path prefix. Add entries to enable the sync.",
            Topics = [],
            Example = new TopicDto
            {
                Slug = "drive-to-conclusion",
                Title = "Orchestrator drive-to-conclusion",
                Page = "docs/concepts/orchestrator-drive-to-conclusion.html",
                Tags = ["drive-to-conclusion", "orchestrator"],
                PathPrefixes = ["backend/Features/Runner/"],
            },
        };
        return JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    // ------------------------------------------------------------------
    // Topic relevance
    // ------------------------------------------------------------------

    /// <summary>
    /// Decides whether a task is relevant to a designated topic: a shared tag or
    /// a changed file under one of the topic's path prefixes. Returns the match
    /// flag and a short "matched by" label for the progress row.
    /// </summary>
    private static (bool Matched, string By) Match(
        TaskInfo task, IReadOnlyList<string>? changedFiles, DesignatedTopic topic)
    {
        var byTag = TagMatch(task, topic);
        var byPath = PathMatch(changedFiles, topic);
        if (byTag && byPath) return (true, "tags+path");
        if (byTag) return (true, "tags");
        if (byPath) return (true, "path");
        return (false, "");
    }

    private static bool TagMatch(TaskInfo task, DesignatedTopic topic)
    {
        if (topic.Tags.Count == 0 || task.Tags.Count == 0) return false;
        return topic.Tags.Any(tt => task.Tags.Any(x =>
            string.Equals(x?.Trim(), tt, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool PathMatch(IReadOnlyList<string>? changedFiles, DesignatedTopic topic)
    {
        if (changedFiles == null || changedFiles.Count == 0 || topic.PathPrefixes.Count == 0) return false;
        return changedFiles.Any(f =>
        {
            var norm = NormalizeRel(f);
            return topic.PathPrefixes.Any(p => norm.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        });
    }

    // ------------------------------------------------------------------
    // Per-topic state page
    // ------------------------------------------------------------------

    /// <summary>
    /// Ensures the per-topic Current State / Progress page exists and reflects the
    /// current run. A fresh page is rendered when absent. A matched task prepends a
    /// deduped progress row (newest on top) and refreshes the one-line state note;
    /// a re-run on the same task, or an unmatched task, only refreshes the
    /// validation scalars (concept-page-exists / last-synced) so nothing is lost.
    /// </summary>
    private static void UpsertStatePage(
        string statePath,
        string topicsFolder,
        string repoRoot,
        DesignatedTopic topic,
        TaskInfo task,
        bool matched,
        string matchedBy,
        bool pageExists,
        DateTime now)
    {
        var stamp = Stamp(now);
        var taskKey = task.Key ?? task.Id;

        if (!File.Exists(statePath))
        {
            File.WriteAllText(statePath,
                RenderNewStatePage(topicsFolder, repoRoot, topic, task, matched, matchedBy, pageExists, now),
                Utf8NoBom);
            return;
        }

        var text = File.ReadAllText(statePath, Utf8NoBom);
        text = UpsertFrontmatterScalar(text, "concept-page-exists", pageExists ? "true" : "false");
        text = UpsertFrontmatterScalar(text, "last-synced", stamp);

        // Idempotent: an unmatched task, or a re-run on a task already recorded,
        // leaves the progress log and state note untouched.
        if (matched && !text.Contains($"`{taskKey}`", StringComparison.Ordinal))
        {
            var note = BuildNote(task);
            text = InsertProgressRow(text, RenderRow(now, taskKey, matchedBy, note, CommitCell(task)));
            text = TrimProgressRows(text, MaxEntriesPerTopic);
            text = UpsertFrontmatterScalar(text, "last-task", taskKey);
            text = UpsertFrontmatterScalar(text, "entry-count", CountRows(text).ToString(CultureInfo.InvariantCulture));
            text = UpsertFrontmatterScalar(text, "state-note", Quote(StateNote(taskKey, note, now)));
            text = ReplaceStateNoteSentence(text, StateNote(taskKey, note, now));
        }

        File.WriteAllText(statePath, text, Utf8NoBom);
    }

    private static string RenderNewStatePage(
        string topicsFolder,
        string repoRoot,
        DesignatedTopic topic,
        TaskInfo task,
        bool matched,
        string matchedBy,
        bool pageExists,
        DateTime now)
    {
        var stamp = Stamp(now);
        var taskKey = task.Key ?? task.Id;
        var conceptLink = pageExists
            ? $"[`{topic.Page}`]({RelLink(topicsFolder, repoRoot, topic.Page)})"
            : $"`{topic.Page}` (missing)";

        string stateNoteSentence;
        string lastTask;
        string firstRow;
        int entryCount;
        if (matched)
        {
            var note = BuildNote(task);
            stateNoteSentence = StateNote(taskKey, note, now);
            lastTask = taskKey;
            firstRow = "\n" + RenderRow(now, taskKey, matchedBy, note, CommitCell(task));
            entryCount = 1;
        }
        else
        {
            stateNoteSentence = "No task activity recorded yet.";
            lastTask = "-";
            firstRow = "";
            entryCount = 0;
        }

        return $"""
---
id: {topic.Slug}
title: {Quote(topic.Title)}
concept-page: {topic.Page}
concept-page-exists: {(pageExists ? "true" : "false")}
first-synced: {stamp}
last-synced: {stamp}
last-task: {lastTask}
entry-count: {entryCount}
state-note: {Quote(stateNoteSentence)}
---

# Designated topic: {topic.Title}

> Machine-maintained by the `post-agents-wiki-sync` pipeline step so agents read
> the current state of this topic instead of re-discovering it. Concept page:
> {conceptLink}. Add narrative to the concept page itself; the progress table
> below is regenerated from task evidence and should not be hand-edited.

## Current State / Progress

{stateNoteSentence}

| When (UTC) | Task | Matched by | Note | Commit |
|---|---|---|---|---|{firstRow}
""";
    }

    private static string RenderRow(DateTime now, string taskKey, string matchedBy, string note, string? commit) =>
        $"| {Stamp(now)} | `{CleanCell(taskKey)}` | {CleanCell(matchedBy)} | {CleanCell(note)} | {CleanCell(commit ?? "-")} |";

    private static string InsertProgressRow(string text, string row)
    {
        // Newest row on top: insert right after the table header separator.
        const string marker = "|---|---|---|---|---|";
        var idx = text.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            idx += marker.Length;
            return text.Insert(idx, "\n" + row);
        }
        return text.TrimEnd() + "\n" + row + "\n";
    }

    private static string TrimProgressRows(string text, int max)
    {
        // The progress table is the last block on the state page, so we can keep
        // the header/prefix verbatim and rebuild the data rows below the marker.
        const string marker = "|---|---|---|---|---|";
        var idx = text.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return text;
        var bodyStart = idx + marker.Length;
        var rowLines = text[bodyStart..]
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("| ", StringComparison.Ordinal) && l.Contains('`'))
            .ToList();
        if (rowLines.Count <= max) return text;
        var kept = rowLines.Take(max); // newest-on-top, so the tail overflow is oldest
        return text[..bodyStart] + "\n" + string.Join("\n", kept) + "\n";
    }

    private static string ReplaceStateNoteSentence(string text, string sentence)
    {
        var heading = "## Current State / Progress";
        var idx = text.IndexOf(heading, StringComparison.Ordinal);
        if (idx < 0) return text;
        var afterHeading = idx + heading.Length;
        var tableIdx = text.IndexOf("\n| When (UTC)", afterHeading, StringComparison.Ordinal);
        if (tableIdx < 0) return text;
        return text[..afterHeading] + "\n\n" + sentence + "\n" + text[tableIdx..];
    }

    private static int CountRows(string text)
        => Regex.Matches(text, @"^\| \d{4}-\d\d-\d\dT", RegexOptions.Multiline).Count;

    private static string StateNote(string taskKey, string note, DateTime now)
        => $"Latest: {taskKey} - {note} ({now:yyyy-MM-dd})";

    private static string BuildNote(TaskInfo task)
    {
        if (task.Commits.Count > 0)
        {
            var subject = FirstLine(task.Commits[^1].Message);
            if (subject.Length > 0) return Cap(subject, 200);
        }
        if (!string.IsNullOrWhiteSpace(task.Title)) return Cap(task.Title.Trim(), 200);
        if (task.OutcomeIssue is { } issue)
            return Cap(string.IsNullOrWhiteSpace(issue.Summary) ? issue.Label : issue.Summary, 200);
        return "(no summary)";
    }

    private static string? CommitCell(TaskInfo task)
    {
        if (task.Commits.Count == 0) return null;
        var newest = task.Commits[^1];
        if (string.IsNullOrWhiteSpace(newest.ShortSha)) return null;
        var subject = FirstLine(newest.Message);
        return subject.Length == 0 ? newest.ShortSha : $"{newest.ShortSha}: {Cap(subject, 80)}";
    }

    // ------------------------------------------------------------------
    // Index
    // ------------------------------------------------------------------

    private static void RegenerateIndex(string topicsFolder, IReadOnlyList<IndexRow> rows, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Designated Topics");
        sb.AppendLine();
        sb.AppendLine("Auto-generated by the `post-agents-wiki-sync` pipeline step. Do not edit manually.");
        sb.AppendLine("Keeps the AGENTS-surface pointers to the designated concept pages consistent (no");
        sb.AppendLine("dead / missing link) and carries a one-line current-state note per topic. Per-topic");
        sb.AppendLine("progress lives in the sibling `<slug>.md` pages; the operator-owned topic list is");
        sb.AppendLine("`registry.json`.");
        sb.AppendLine();
        sb.AppendLine($"Last regenerated: {now:yyyy-MM-dd}");
        sb.AppendLine();

        if (rows.Count == 0)
        {
            sb.AppendLine("_No designated topics configured yet. Add entries to `registry.json` to enable the sync._");
            File.WriteAllText(Path.Combine(topicsFolder, "README.md"), sb.ToString(), Utf8NoBom);
            return;
        }

        sb.AppendLine("| Topic | Concept page | Current state | Last task | Last synced |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in rows.OrderBy(r => r.Topic.Slug, StringComparer.OrdinalIgnoreCase))
        {
            var conceptCell = r.PageExists
                ? $"[`{Path.GetFileName(r.Topic.Page)}`]({r.ConceptRel})"
                : $"**missing**: `{r.Topic.Page}`";
            sb.AppendLine(
                $"| [{CleanCell(r.Topic.Title)}]({r.Topic.Slug}.md) | {conceptCell} | {CleanCell(r.StateNote)} | {CleanCell(r.LastTask)} | {ShortDate(r.LastSynced)} |");
        }
        sb.AppendLine();

        var missing = rows.Where(r => !r.PageExists).ToList();
        sb.AppendLine("## Pointer health");
        sb.AppendLine();
        if (missing.Count == 0)
        {
            sb.AppendLine($"All {rows.Count} designated concept page(s) resolve.");
        }
        else
        {
            sb.AppendLine("The following designated concept pages are missing (dead pointers). Create the");
            sb.AppendLine("page or fix the registry entry:");
            sb.AppendLine();
            foreach (var r in missing.OrderBy(r => r.Topic.Slug, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- `{r.Topic.Slug}` -> `{r.Topic.Page}`");
        }

        File.WriteAllText(Path.Combine(topicsFolder, "README.md"), sb.ToString(), Utf8NoBom);
    }

    // ------------------------------------------------------------------
    // AGENTS.md pointer consistency
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies (and self-heals) the AGENTS.md pointer to the designated-topics
    /// index. Returns <c>ok</c> when a pointer already exists, <c>healed</c> when a
    /// managed pointer block was appended, or <c>absent</c> when the project has no
    /// AGENTS.md (recorded as a finding, never fabricated).
    /// </summary>
    private static string EnsureAgentsPointer(string repoRoot, List<string> findings)
    {
        var agentsPath = Path.Combine(repoRoot, "AGENTS.md");
        if (!File.Exists(agentsPath))
        {
            findings.Add("AGENTS.md not found at project root; cannot verify the designated-topics pointer");
            return "absent";
        }

        var text = File.ReadAllText(agentsPath, Utf8NoBom);
        if (text.Contains(TopicsFolderRel, StringComparison.OrdinalIgnoreCase)
            || text.Contains("designated-topics", StringComparison.OrdinalIgnoreCase))
        {
            return "ok";
        }

        var block = new StringBuilder();
        if (!text.EndsWith("\n", StringComparison.Ordinal)) block.Append('\n');
        block.Append('\n').AppendLine(AgentsBeginMarker);
        block.AppendLine(
            $"- Designated-topic current state (avoid re-discovering the same ground): [{IndexRepoRel}]({IndexRepoRel}).");
        block.AppendLine(AgentsEndMarker);
        File.AppendAllText(agentsPath, block.ToString(), Utf8NoBom);
        findings.Add("AGENTS.md had no designated-topics pointer; appended a managed pointer block");
        return "healed";
    }

    // ------------------------------------------------------------------
    // Frontmatter + text helpers (mirrors the sibling wiki runners)
    // ------------------------------------------------------------------

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

    private static string RelLink(string fromDir, string repoRoot, string repoRelTarget)
    {
        var targetAbs = Path.GetFullPath(Path.Combine(repoRoot, ToNative(repoRelTarget)));
        return Path.GetRelativePath(fromDir, targetAbs).Replace('\\', '/');
    }

    private static IReadOnlyList<string> CleanList(IEnumerable<string>? values) =>
        (values ?? [])
            .Select(v => v?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Slugify(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in (value ?? "").Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    private static string ToNative(string relPath) => relPath.Replace('/', Path.DirectorySeparatorChar);
    private static string NormalizeRel(string path) => (path ?? "").Replace('\\', '/').TrimStart('/').Trim();
    private static string Stamp(DateTime now) => now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    private static string ShortDate(string value) => value.Contains('T') ? value[..value.IndexOf('T')] : value;
    private static string FirstLine(string? value) => (value ?? "").Replace("\r", string.Empty).Split('\n', 2)[0].Trim();
    private static string Cap(string value, int max) => value.Length <= max ? value : value[..(max - 3)].TrimEnd() + "...";
    private static string Quote(string value) => "\"" + EscapeYaml(value) + "\"";
    private static string EscapeYaml(string value) => (value ?? "").Replace("\"", "\\\"");
    private static string? TrimQuotes(string? value) => value?.Trim().Trim('"');
    private static string CleanCell(string value) =>
        (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();

    // ------------------------------------------------------------------
    // Records / DTOs
    // ------------------------------------------------------------------

    public sealed record DesignatedTopic(
        string Slug,
        string Title,
        string Page,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> PathPrefixes);

    private sealed record IndexRow(
        DesignatedTopic Topic,
        bool PageExists,
        string ConceptRel,
        string StateNote,
        string LastTask,
        string LastSynced);

    private sealed record RegistryDto
    {
        public string? Note { get; init; }
        public List<TopicDto>? Topics { get; init; }
        public TopicDto? Example { get; init; }
    }

    private sealed record TopicDto
    {
        public string? Slug { get; init; }
        public string? Title { get; init; }
        public string? Page { get; init; }
        public List<string>? Tags { get; init; }
        public List<string>? PathPrefixes { get; init; }
    }
}
