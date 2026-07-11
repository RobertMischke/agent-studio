using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Docs;

public sealed record WorkstreamRetroResult(bool Ran, int Signals, int Knowledge, int Decisions, string Reason);
public sealed record WorkstreamCurationResult(int Verified, int Merged, int Condensed, int Pruned, string Reason);

/// <summary>
/// EW-3 history bootstrap and bounded maintenance for collector-owned Workstream pages.
/// The retro pilot is exactly-once per project and the curator only mutates pages carrying
/// <c>managed-by: workstream-collector</c>. Operator-authored pages and the immutable shells
/// are therefore outside its authority.
/// </summary>
public sealed class WorkstreamCurationService
{
    internal const string ManagedBy = "workstream-collector";
    internal const int MaxEvidenceRows = 20;
    internal const int MaxManagedPagesPerArea = 40;
    private const string Frame = "engineering-workstream";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly WorkstreamPattern[] Patterns =
    [
        new("post-processing-robustness", "Post-processing robustness", "major",
            ["post-processing", "auto-review", "missing-terminal-sentinel", "silent completion", "verdict retry", "results missing"]),
        new("restart-resume-orphans", "Restart, resume, and orphan recovery", "critical",
            ["restart", "resume", "orphan", "no-active-run", "capture-fail", "session not found"]),
        new("reissue-wipe", "Reissue must preserve evidence", "critical",
            ["reissue", "wipe", "deleted results", "missing deliverables", "worktree teardown"]),
    ];

    private readonly ILogger<WorkstreamCurationService> _logger;

    public WorkstreamCurationService(ILogger<WorkstreamCurationService> logger) => _logger = logger;

    public WorkstreamRetroResult RunRetroPilot(WatchPathEntry project, IReadOnlyList<TaskInfo> history, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(project.RootPath))
            return new(false, 0, 0, 0, "project root is not configured");

        var now = nowUtc ?? DateTime.UtcNow;
        var docsRoot = Path.Combine(project.RootPath, "docs");
        var controlRoot = Path.Combine(docsRoot, Frame, ".curator");
        var marker = Path.Combine(controlRoot, "retro-pilot-v1.json");
        if (File.Exists(marker)) return new(false, 0, 0, 0, "retro pilot already completed");

        try
        {
            EngineeringWorkstreamFrameSeeder.EnsureFrame(docsRoot,
                WorkstreamFrameLanguageResolver.Resolve(project.Name, isPublicOverride: null));
            Directory.CreateDirectory(controlRoot);

            var evidence = history.Select(ReadEvidence).ToList();
            var matched = Patterns
                .Select(pattern => (pattern, hits: evidence.Where(e => pattern.Needles.Any(n => e.Text.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList()))
                .Where(x => x.hits.Count > 0)
                .ToList();

            foreach (var (pattern, hits) in matched)
                WriteSignal(docsRoot, pattern, hits, now);

            var taskIds = matched.SelectMany(x => x.hits).Select(x => x.TaskId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var knowledge = matched.Count == 0 ? 0 : 1;
            var decisions = matched.Count == 0 ? 0 : 1;
            if (knowledge > 0) WriteKnowledge(docsRoot, taskIds, now);
            if (decisions > 0) WriteDecision(docsRoot, taskIds, now);
            WriteState(docsRoot, project.Name, history, matched.Select(x => x.pattern).ToList(), now);
            WriteLog(docsRoot, history.Count, matched.Count, now);

            var manifest = new RetroManifest(1, project.Name, now, history.Count, matched.Count,
                matched.Select(x => x.pattern.Key).ToArray(), taskIds.Take(100).ToArray());
            File.WriteAllText(marker, JsonSerializer.Serialize(manifest, Json), Encoding.UTF8);
            WriteContext(controlRoot, new CuratorContext(1, now, null, matched.Count + knowledge + decisions + 2, 0, 0, 0));

            _logger.LogInformation(
                "workstream-retro-pilot-completed project={Project} tasks={Tasks} signals={Signals} knowledge={Knowledge} decisions={Decisions}",
                project.Name, history.Count, matched.Count, knowledge, decisions);
            return new(true, matched.Count, knowledge, decisions, "retro pilot completed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workstream-retro-pilot-failed project={Project}", project.Name);
            return new(false, 0, 0, 0, ex.Message);
        }
    }

    public WorkstreamCurationResult Curate(WatchPathEntry project, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(project.RootPath)) return new(0, 0, 0, 0, "project root is not configured");
        var now = nowUtc ?? DateTime.UtcNow;
        var root = Path.Combine(project.RootPath, "docs", Frame);
        var marker = Path.Combine(root, ".curator", "retro-pilot-v1.json");
        if (!File.Exists(marker)) return new(0, 0, 0, 0, "retro pilot has not completed");

        var managed = EnumerateManagedPages(root).ToList();
        var verified = 0;
        var merged = 0;
        var condensed = 0;
        var pruned = 0;

        foreach (var group in managed.GroupBy(p => (p.Area, p.Key), new PageIdentityComparer()))
        {
            // Prefer the canonical collector filename over an arbitrary duplicate.
            // This keeps links stable when a merge removes redundant pages.
            var pages = group
                .OrderByDescending(p => string.Equals(Path.GetFileNameWithoutExtension(p.Path), p.Key, StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var keeper = pages[0];
            foreach (var duplicate in pages.Skip(1))
            {
                MergeEvidence(keeper.Path, duplicate.Path, now);
                File.Delete(duplicate.Path);
                merged++;
            }
        }

        managed = EnumerateManagedPages(root).ToList();
        foreach (var page in managed)
        {
            var text = File.ReadAllText(page.Path, Encoding.UTF8);
            var next = UpsertScalar(text, "last-verified", Stamp(now));
            verified++;
            var rows = EvidenceRows(next).ToList();
            if (rows.Count > MaxEvidenceRows)
            {
                next = ReplaceEvidenceRows(next, rows.Take(MaxEvidenceRows));
                next = UpsertScalar(next, "condensed-evidence", (rows.Count - MaxEvidenceRows).ToString(CultureInfo.InvariantCulture));
                condensed++;
            }
            File.WriteAllText(page.Path, next, Encoding.UTF8);
        }

        foreach (var area in managed.GroupBy(p => p.Area, StringComparer.OrdinalIgnoreCase))
        {
            var overflow = area.OrderByDescending(p => p.Confidence).ThenByDescending(p => p.LastVerified).Skip(MaxManagedPagesPerArea);
            foreach (var page in overflow.Where(p => p.Confidence < 0.5m && EvidenceRows(File.ReadAllText(p.Path)).Count() == 0))
            {
                File.Delete(page.Path);
                pruned++;
            }
        }

        var controlRoot = Path.Combine(root, ".curator");
        var previous = ReadContext(controlRoot);
        WriteContext(controlRoot, new CuratorContext(1, previous?.CreatedAt ?? now, now, verified, merged, condensed, pruned));
        _logger.LogInformation(
            "workstream-curation-completed project={Project} verified={Verified} merged={Merged} condensed={Condensed} pruned={Pruned}",
            project.Name, verified, merged, condensed, pruned);
        return new(verified, merged, condensed, pruned, "curation completed");
    }

    private static TaskEvidence ReadEvidence(TaskInfo task)
    {
        var sb = new StringBuilder().Append(task.Id).Append(' ').Append(task.Title).Append(' ')
            .Append(task.State).Append(' ').Append(task.OutcomeIssue?.Kind).Append(' ')
            .Append(task.OutcomeIssue?.Summary);
        var log = AgentStudio.Tasks.TaskPaths.CliOutputLog(task.FolderPath);
        if (File.Exists(log))
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 96_000) stream.Seek(-96_000, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: false);
            sb.Append(' ').Append(reader.ReadToEnd());
        }
        return new(task.Id, task.Title, sb.ToString());
    }

    private static void WriteSignal(string docsRoot, WorkstreamPattern pattern, IReadOnlyList<TaskEvidence> hits, DateTime now)
    {
        var body = Header(pattern.Key, pattern.Title, "signal", now, 0.8m) +
            $"# {pattern.Title}\n\n**Status.** Active signal found by the EW-3 history pilot.\n\n" +
            "## Evidence\n\n| Task | Observation |\n|---|---|\n" +
            string.Join("\n", hits.Take(MaxEvidenceRows).Select(h => $"| `{Cell(h.TaskId)}` | {Cell(h.Title)} |")) + "\n\n" +
            $"## Reading\n\n{pattern.Severity} recurring behavior. Verify against new runs before promoting a root cause.\n";
        WriteManaged(docsRoot, "20-development-signals", pattern.Key, body);
    }

    private static void WriteKnowledge(string docsRoot, IReadOnlyList<string> tasks, DateTime now)
    {
        const string key = "recovery-and-evidence-contracts";
        var body = Header(key, "Recovery and evidence contracts", "knowledge", now, 0.75m) +
            "# Recovery and evidence contracts\n\nA restart, resume, or reissue is a continuity boundary. Task identity, results, logs, and the latest durable outcome must survive that boundary. Recovery may rebuild execution context, but it must not erase evidence.\n\n" +
            EvidenceTable(tasks) + "\n";
        WriteManaged(docsRoot, "30-system-knowledge", key, body);
    }

    private static void WriteDecision(string docsRoot, IReadOnlyList<string> tasks, DateTime now)
    {
        const string key = "preserve-evidence-before-reissue";
        var body = Header(key, "Preserve evidence before reissue", "decision", now, 0.75m) +
            "# Preserve evidence before reissue\n\n**Decision.** Reissue and recovery flows preserve existing results and diagnostic logs. Cleanup may replace disposable execution state only after durable evidence has been retained.\n\n" +
            "**Trigger.** Historical runs showed that recovery without an explicit preservation boundary can turn an infrastructure failure into apparent missing delivery.\n\n" + EvidenceTable(tasks) + "\n";
        WriteManaged(docsRoot, "40-decision-log", key, body);
    }

    private static void WriteState(string docsRoot, string project, IReadOnlyList<TaskInfo> history, IReadOnlyList<WorkstreamPattern> patterns, DateTime now)
    {
        const string key = "retro-pilot-baseline";
        var body = Header(key, "Retro pilot baseline", "state", now, 0.9m) +
            $"# Retro pilot baseline\n\nProject `{Cell(project)}` has an EW-3 baseline derived from {history.Count} historical tasks. " +
            $"Active taxonomy signals: {(patterns.Count == 0 ? "none" : string.Join(", ", patterns.Select(p => $"`{p.Key}`")))}.\n\n" +
            "This page is refreshed by the curator and is not a replacement for live task state.\n";
        WriteManaged(docsRoot, "10-current-development-state", key, body);
    }

    private static void WriteLog(string docsRoot, int tasks, int signals, DateTime now)
    {
        const string key = "retro-pilot";
        var body = Header(key, "EW-3 retro pilot", "log", now, 1m) +
            $"# EW-3 retro pilot\n\n- {Stamp(now)}: scanned {tasks} historical tasks and established {signals} signal pages plus initial knowledge, decision, and state pages.\n";
        WriteManaged(docsRoot, "50-workstream-log", key, body);
    }

    private static string Header(string key, string title, string kind, DateTime now, decimal confidence) =>
        $"---\nmanaged-by: {ManagedBy}\ncanonical-key: {key}\ntitle: \"{Cell(title)}\"\nkind: {kind}\nconfidence: {confidence.ToString("0.00", CultureInfo.InvariantCulture)}\ncreated-at: {Stamp(now)}\nlast-verified: {Stamp(now)}\n---\n\n";

    private static string EvidenceTable(IEnumerable<string> tasks) =>
        "## Evidence\n\n| Task | Observation |\n|---|---|\n" +
        string.Join("\n", tasks.Take(MaxEvidenceRows).Select(t => $"| `{Cell(t)}` | Historical taxonomy match |")) + "\n";

    private static void WriteManaged(string docsRoot, string area, string key, string body)
    {
        var dir = Path.Combine(docsRoot, Frame, area, "generated");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, key + ".md"), body, Encoding.UTF8);
    }

    private static IEnumerable<ManagedPage> EnumerateManagedPages(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (!string.Equals(Scalar(text, "managed-by"), ManagedBy, StringComparison.Ordinal)) continue;
            var key = Scalar(text, "canonical-key");
            if (string.IsNullOrWhiteSpace(key)) continue;
            var rel = Path.GetRelativePath(root, path);
            var area = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            _ = decimal.TryParse(Scalar(text, "confidence"), NumberStyles.Number, CultureInfo.InvariantCulture, out var confidence);
            _ = DateTime.TryParse(Scalar(text, "last-verified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var verified);
            yield return new(path, area, key, confidence, verified);
        }
    }

    private static void MergeEvidence(string keeper, string duplicate, DateTime now)
    {
        var target = File.ReadAllText(keeper, Encoding.UTF8);
        var rows = EvidenceRows(target).Concat(EvidenceRows(File.ReadAllText(duplicate, Encoding.UTF8)))
            .Distinct(StringComparer.Ordinal).Take(MaxEvidenceRows).ToList();
        target = ReplaceEvidenceRows(target, rows);
        target = UpsertScalar(target, "last-verified", Stamp(now));
        File.WriteAllText(keeper, target, Encoding.UTF8);
    }

    private static IEnumerable<string> EvidenceRows(string text) => Regex.Matches(text, @"(?m)^\|\s*`[^\r\n]+$").Select(m => m.Value);

    private static string ReplaceEvidenceRows(string text, IEnumerable<string> rows)
    {
        var matches = Regex.Matches(text, @"(?m)^\|\s*`[^\r\n]+$");
        if (matches.Count == 0) return text;
        var start = matches[0].Index;
        var end = matches[^1].Index + matches[^1].Length;
        return text[..start] + string.Join("\n", rows) + text[end..];
    }

    private static string? Scalar(string text, string key)
    {
        var match = Regex.Match(text, $@"(?m)^{Regex.Escape(key)}:\s*(?<value>[^\r\n]+)$");
        return match.Success ? match.Groups["value"].Value.Trim().Trim('"') : null;
    }

    private static string UpsertScalar(string text, string key, string value)
    {
        var regex = new Regex($@"(?m)^{Regex.Escape(key)}:\s*[^\r\n]*$");
        if (regex.IsMatch(text)) return regex.Replace(text, $"{key}: {value}", 1);
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        return end < 0 ? text : text.Insert(end, $"\n{key}: {value}");
    }

    private static CuratorContext? ReadContext(string root)
    {
        var path = Path.Combine(root, "context.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<CuratorContext>(File.ReadAllText(path), Json) : null; }
        catch { return null; }
    }

    private static void WriteContext(string root, CuratorContext value)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "context.json"), JsonSerializer.Serialize(value, Json), Encoding.UTF8);
    }

    private static string Stamp(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    private static string Cell(string? value) => (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();

    private sealed record WorkstreamPattern(string Key, string Title, string Severity, string[] Needles);
    private sealed record TaskEvidence(string TaskId, string Title, string Text);
    private sealed record ManagedPage(string Path, string Area, string Key, decimal Confidence, DateTime LastVerified);
    private sealed record RetroManifest(int Version, string Project, DateTime CompletedAt, int TasksScanned, int SignalsCreated, string[] Taxonomy, string[] EvidenceTasks);
    private sealed record CuratorContext(int Version, DateTime CreatedAt, DateTime? LastRunAt, int Verified, int Merged, int Condensed, int Pruned);

    private sealed class PageIdentityComparer : IEqualityComparer<(string Area, string Key)>
    {
        public bool Equals((string Area, string Key) x, (string Area, string Key) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Area, y.Area) && StringComparer.OrdinalIgnoreCase.Equals(x.Key, y.Key);
        public int GetHashCode((string Area, string Key) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Area), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));
    }
}

/// <summary>
/// Periodic EW-3 driver. Its persisted <c>.curator/context.json</c> is deliberately
/// separate from task and review-orchestrator sessions. Disabled by default and
/// enabled with <c>WorkstreamCurator:Enabled</c> after EW-2 collection is active.
/// </summary>
public sealed class WorkstreamCuratorHostedService : BackgroundService
{
    private readonly WorkstreamCurationService _curator;
    private readonly TaskScannerService _scanner;
    private readonly IConfiguration _config;
    private readonly ILogger<WorkstreamCuratorHostedService> _logger;

    public WorkstreamCuratorHostedService(WorkstreamCurationService curator, TaskScannerService scanner,
        IConfiguration config, ILogger<WorkstreamCuratorHostedService> logger)
    {
        _curator = curator;
        _scanner = scanner;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalHours = Math.Max(1, _config.GetValue("WorkstreamCurator:IntervalHours", 24));
            try
            {
                if (_config.GetValue("WorkstreamCurator:Enabled", false)) RunOnce();
                await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "workstream-curator-cycle-failed");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    public void RunOnce()
    {
        var history = _scanner.ScanAllJobs();
        foreach (var project in _scanner.GetWatchPaths())
        {
            var projectHistory = history.Where(t => string.Equals(t.ProjectName, project.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            _curator.RunRetroPilot(project, projectHistory);
            _curator.Curate(project);
        }
    }
}
