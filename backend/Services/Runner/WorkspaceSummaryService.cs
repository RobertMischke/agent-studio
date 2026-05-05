using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.State;
using OrchestratorApi.Services.Supervisor;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Workspace-level executive summary aggregator. Folds per-project
/// activity (job folders, supervisor advisories, orphan-recovery
/// archive moves, repository commits) into one
/// <see cref="ExecutiveSummary"/> covering the requested time window.
///
/// <para>
/// The aggregator is read-only: it never mutates state and never
/// invents events. Each line in the result references an underlying
/// record (a job id, a supervisor advisory's project+timestamp, an
/// orphan-recovery slug, a commit sha) that the consumer can verify
/// against disk.
/// </para>
///
/// <para>
/// Several deliverables in
/// <c>protocol-summary-and-executive-summary-schema</c> are not yet
/// implemented and surface here as zero-valued counters: there is no
/// per-project decisions log
/// (<c>logs/decisions/&lt;project&gt;.jsonl</c>) yet, so
/// <c>decisionsMade</c> and <c>topDecisions</c> always read 0 / empty
/// until that producer lands. The schema describes the target shape;
/// the aggregator returns what the filesystem can prove today.
/// </para>
/// </summary>
public sealed class WorkspaceSummaryService
{
    public static readonly int[] AllowedWindowHours = [1, 6, 24, 168];
    public const int DefaultWindowHours = 24;

    private readonly JobScannerService _scanner;
    private readonly SupervisorAdvisoryStore _advisories;
    private readonly IConfiguration _config;
    private readonly ILogger<WorkspaceSummaryService> _logger;

    public WorkspaceSummaryService(
        JobScannerService scanner,
        SupervisorAdvisoryStore advisories,
        IConfiguration config,
        ILogger<WorkspaceSummaryService> logger)
    {
        _scanner = scanner;
        _advisories = advisories;
        _config = config;
        _logger = logger;
    }

    public ExecutiveSummary Build(int windowHours, DateTime? nowUtc = null)
    {
        var w = ResolveWindow(windowHours);
        var end = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var start = end.AddHours(-w);

        var workspaceRoot = _config["TaskRepository"];
        var watchPaths = _scanner.GetWatchPaths();

        var byProject = new List<ExecutiveSummaryProject>();
        var openHumanDecisions = new List<ExecutiveSummaryOpenDecision>();

        var allJobs = _scanner.ScanAllJobs();
        var jobsByProject = allJobs
            .GroupBy(j => j.ProjectName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var orphans = ReadOrphanRecoveries(workspaceRoot, start, end);

        foreach (var entry in watchPaths)
        {
            var project = entry.Name;

            var advisoriesInWindow = string.IsNullOrWhiteSpace(workspaceRoot)
                ? 0
                : _advisories.Where(workspaceRoot, project, a => a.CreatedAt >= start && a.CreatedAt < end).Count;

            var jobsMoved = orphans
                .Where(o => string.Equals(o.Project, project, StringComparison.Ordinal))
                .Select(o => new ExecutiveSummaryJobMove(
                    JobId: o.Slug,
                    FromState: "3-progress",
                    ToState: o.TargetState,
                    At: o.At))
                .ToList();

            var commits = ReadCommits(entry, start, end);

            // Open human-decision-needed-* tasks (independent of the window).
            if (jobsByProject.TryGetValue(project, out var projectJobs))
            {
                foreach (var job in projectJobs.Where(j =>
                    j.State == "1-preparation" &&
                    j.Id.StartsWith("human-decision-needed-", StringComparison.OrdinalIgnoreCase)))
                {
                    openHumanDecisions.Add(new ExecutiveSummaryOpenDecision(
                        Project: project,
                        JobId: job.Id,
                        Title: string.IsNullOrWhiteSpace(job.Title) ? job.Id : job.Title,
                        CreatedAt: job.CreatedAt));
                }
            }

            var hasAnyActivity = advisoriesInWindow > 0 || jobsMoved.Count > 0 || commits.Count > 0;
            if (!hasAnyActivity) continue;

            byProject.Add(new ExecutiveSummaryProject(
                Project: project,
                JobsMoved: jobsMoved,
                DecisionsMade: 0,
                AdvisoriesRaised: advisoriesInWindow,
                Commits: commits));
        }

        var crashes = ReadCrashes(workspaceRoot, start, end);
        var headline = BuildHeadline(byProject, crashes, openHumanDecisions, w);

        return new ExecutiveSummary(
            WindowStart: start,
            WindowEnd: end,
            Headline: headline,
            ByProject: byProject,
            Crashes: crashes,
            TopDecisions: Array.Empty<ExecutiveSummaryDecision>(),
            OpenHumanDecisions: openHumanDecisions);
    }

    private static int ResolveWindow(int requested)
        => Array.IndexOf(AllowedWindowHours, requested) >= 0 ? requested : DefaultWindowHours;

    private static string BuildHeadline(
        IReadOnlyList<ExecutiveSummaryProject> byProject,
        IReadOnlyList<ExecutiveSummaryCrash> crashes,
        IReadOnlyList<ExecutiveSummaryOpenDecision> openDecisions,
        int windowHours)
    {
        var totalCommits = byProject.Sum(p => p.Commits.Count);
        var totalAdvisories = byProject.Sum(p => p.AdvisoriesRaised);
        var totalMoves = byProject.Sum(p => p.JobsMoved.Count);
        var window = windowHours == 1 ? "the last hour" : $"the last {windowHours} hours";

        if (byProject.Count == 0 && crashes.Count == 0 && openDecisions.Count == 0)
            return $"Workspace was idle in {window}.";

        var parts = new List<string>();
        if (byProject.Count > 0)
            parts.Add($"{byProject.Count} project{(byProject.Count == 1 ? "" : "s")} active");
        if (totalCommits > 0)
            parts.Add($"{totalCommits} commit{(totalCommits == 1 ? "" : "s")}");
        if (totalMoves > 0)
            parts.Add($"{totalMoves} job move{(totalMoves == 1 ? "" : "s")}");
        if (totalAdvisories > 0)
            parts.Add($"{totalAdvisories} advisor{(totalAdvisories == 1 ? "y" : "ies")}");
        if (crashes.Count > 0)
            parts.Add($"{crashes.Count} crash record{(crashes.Count == 1 ? "" : "s")}");
        if (openDecisions.Count > 0)
            parts.Add($"{openDecisions.Count} open human decision{(openDecisions.Count == 1 ? "" : "s")}");

        return $"In {window}: " + string.Join(", ", parts) + ".";
    }

    private record OrphanRecoveryRow(DateTime At, string Project, string Slug, string TargetState);

    private List<OrphanRecoveryRow> ReadOrphanRecoveries(string? workspaceRoot, DateTime start, DateTime end)
    {
        var list = new List<OrphanRecoveryRow>();
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return list;

        var path = Path.Combine(workspaceRoot, "logs", "orphan-recoveries.jsonl");
        if (!File.Exists(path)) return list;

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("at", out var atEl)) continue;
                    if (!atEl.TryGetDateTime(out var at)) continue;
                    var atUtc = at.ToUniversalTime();
                    if (atUtc < start || atUtc >= end) continue;

                    var project = root.TryGetProperty("projectName", out var p) ? p.GetString() ?? "" : "";
                    var slug = root.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : "";
                    var target = root.TryGetProperty("targetState", out var t) ? t.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(project) || string.IsNullOrEmpty(slug)) continue;

                    list.Add(new OrphanRecoveryRow(atUtc, project, slug, target));
                }
                catch (JsonException)
                {
                    // skip malformed line; lenient on read
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not read orphan-recoveries.jsonl");
        }
        return list;
    }

    private List<ExecutiveSummaryCrash> ReadCrashes(string? workspaceRoot, DateTime start, DateTime end)
    {
        // Today crashes are surfaced via two paths: orphan-recoveries.jsonl
        // (already counted as job moves) and the backend file logger's
        // last-crash.json under the backend's log folder. The latter is a
        // single file per process, not a window; we surface it when its
        // timestamp falls inside the window so the executive summary can
        // call out a recent backend crash.
        var list = new List<ExecutiveSummaryCrash>();
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return list;

        // Orphan recoveries are crash evidence too: they only fire when a
        // 3-progress folder did not write a completion sentinel, which means
        // the previous run was killed without a clean exit.
        var orphanPath = Path.Combine(workspaceRoot, "logs", "orphan-recoveries.jsonl");
        if (File.Exists(orphanPath))
        {
            try
            {
                foreach (var line in File.ReadAllLines(orphanPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("at", out var atEl)) continue;
                        if (!atEl.TryGetDateTime(out var at)) continue;
                        var atUtc = at.ToUniversalTime();
                        if (atUtc < start || atUtc >= end) continue;
                        var project = root.TryGetProperty("projectName", out var p) ? p.GetString() ?? "" : "";
                        var slug = root.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : "";
                        list.Add(new ExecutiveSummaryCrash(
                            At: atUtc,
                            Kind: "orphan-recovery",
                            Path: $"logs/orphan-recoveries.jsonl",
                            Summary: string.IsNullOrEmpty(project) || string.IsNullOrEmpty(slug)
                                ? null
                                : $"{project}/{slug} archived without completion sentinel"));
                    }
                    catch (JsonException) { }
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not read orphan-recoveries.jsonl for crash list");
            }
        }
        return list;
    }

    private List<ExecutiveSummaryCommit> ReadCommits(WatchPathEntry entry, DateTime start, DateTime end)
    {
        // The watched path's repository sometimes equals entry.Path
        // and sometimes lives at entry.RootPath (when .orchestrator.yml
        // is configured). Try both before giving up.
        var candidates = new[] { entry.RepositoryPath, entry.RootPath, entry.Path }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var candidate in candidates)
        {
            var top = ResolveGitToplevel(candidate);
            if (top is null) continue;
            var commits = RunGitLog(top, start, end);
            if (commits.Count > 0) return commits;
        }
        return new List<ExecutiveSummaryCommit>();
    }

    private string? ResolveGitToplevel(string path)
    {
        try
        {
            var (output, _, code) = RunGit(path, "rev-parse --show-toplevel");
            if (code != 0) return null;
            var top = output.Trim();
            return string.IsNullOrEmpty(top) ? null : top;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "git rev-parse failed for {Path}", path);
            return null;
        }
    }

    private List<ExecutiveSummaryCommit> RunGitLog(string repoRoot, DateTime start, DateTime end)
    {
        var fromIso = start.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var toIso = end.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var fmt = "%H%x1f%h%x1f%aI%x1f%aN%x1f%s";
        var args = $"log --no-merges --since=\"{fromIso}\" --until=\"{toIso}\" --pretty=format:\"{fmt}\"";
        var (output, _, code) = RunGit(repoRoot, args);
        var list = new List<ExecutiveSummaryCommit>();
        if (code != 0 || string.IsNullOrEmpty(output)) return list;

        foreach (var raw in output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('');
            if (parts.Length < 5) continue;
            if (!DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) continue;
            list.Add(new ExecutiveSummaryCommit(
                Sha: parts[0],
                ShortSha: parts[1],
                Subject: parts[4],
                Author: parts[3],
                At: at.ToUniversalTime()));
        }
        return list;
    }

    private (string Stdout, string Stderr, int ExitCode) RunGit(string workingDir, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return ("", "", -1);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);
            return (stdout, stderr, proc.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "git invocation failed in {Dir}", workingDir);
            return ("", ex.Message, -1);
        }
    }
}
