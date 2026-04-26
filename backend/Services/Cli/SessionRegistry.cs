using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Reads each CLI's native on-disk session store and presents a unified view
/// for the right-hand usage side-sheet.
/// <list type="bullet">
///   <item>Copilot: tracked via <c>~/.copilot/history/&lt;name&gt;.jsonl</c> when present, plus the
///     job-table's persisted <c>SessionName</c> field. Falls back to the per-job <c>LastUsage</c>.</item>
///   <item>Claude: <c>~/.claude/projects/&lt;encoded-cwd&gt;/&lt;uuid&gt;.jsonl</c>.</item>
///   <item>Codex: <c>~/.codex/session_index.jsonl</c> + per-session
///     <c>~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl</c>.</item>
/// </list>
/// All disk reads are best-effort — a missing or unreadable file just means
/// "no sessions to show" rather than an error.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ILogger<SessionRegistry> _logger;
    private readonly JobScannerService _scanner;

    public SessionRegistry(ILogger<SessionRegistry> logger, JobScannerService scanner)
    {
        _logger = logger;
        _scanner = scanner;
    }

    public CliUsageReport BuildReport(CliRouter router)
    {
        var sections = new List<CliUsageSection>();
        foreach (var cli in router.All)
        {
            var section = BuildSection(cli);
            sections.Add(section);
        }
        return new CliUsageReport { Sections = sections };
    }

    private CliUsageSection BuildSection(ICliExecutionService cli)
    {
        var (available, version, _) = cli.TestCliPath();
        var section = new CliUsageSection
        {
            CliType = cli.CliType,
            Available = available,
            Version = version
        };

        try
        {
            section = section with { Projects = cli.CliType switch
            {
                CliTypes.Copilot => BuildCopilotProjects(),
                CliTypes.Claude  => BuildClaudeProjects(),
                CliTypes.Codex   => BuildCodexProjects(),
                _ => []
            }};
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate sessions for {Cli}", cli.CliType);
            section = section with { Error = ex.Message };
        }
        return section;
    }

    // ── Copilot ─────────────────────────────────────────────────────────
    // We don't (yet) read the Copilot CLI's own history files; the persisted
    // SessionName + LastUsage on the job objects is enough for the side-sheet.

    private List<CliUsageProjectGroup> BuildCopilotProjects()
    {
        var byProject = _scanner.ScanAllJobs()
            .Where(j => CliTypes.Normalize(j.CliType) == CliTypes.Copilot
                        && !string.IsNullOrWhiteSpace(j.SessionName))
            .GroupBy(j => (j.ProjectName, j.WatchPath));

        var groups = new List<CliUsageProjectGroup>();
        foreach (var grp in byProject)
        {
            var sessions = grp
                .OrderByDescending(j => j.LastActivity)
                .Select(j => new CliSessionInfo
                {
                    Id = j.SessionName!,
                    Label = j.Title,
                    UpdatedAt = j.LastActivity,
                    Cwd = j.WatchPath,
                    LastUsage = j.LastUsage
                })
                .ToList();

            groups.Add(new CliUsageProjectGroup
            {
                ProjectName = grp.Key.ProjectName,
                RootPath = grp.Key.WatchPath,
                Sessions = sessions
            });
        }
        return groups;
    }

    // ── Claude Code ─────────────────────────────────────────────────────

    private List<CliUsageProjectGroup> BuildClaudeProjects()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
        if (!Directory.Exists(root)) return [];

        var groups = new List<CliUsageProjectGroup>();
        foreach (var projectDir in Directory.EnumerateDirectories(root))
        {
            var encoded = Path.GetFileName(projectDir);
            // Claude encodes the cwd by replacing path separators with '-' and dropping the leading slash.
            var displayCwd = encoded.Replace('-', Path.DirectorySeparatorChar);

            var sessions = new List<CliSessionInfo>();
            foreach (var jsonl in Directory.EnumerateFiles(projectDir, "*.jsonl"))
            {
                var id = Path.GetFileNameWithoutExtension(jsonl);
                var fi = new FileInfo(jsonl);
                sessions.Add(new CliSessionInfo
                {
                    Id = id,
                    Label = id[..Math.Min(8, id.Length)],
                    UpdatedAt = fi.LastWriteTimeUtc,
                    Cwd = displayCwd
                });
            }

            if (sessions.Count == 0) continue;
            groups.Add(new CliUsageProjectGroup
            {
                ProjectName = displayCwd,
                RootPath = displayCwd,
                Sessions = sessions.OrderByDescending(s => s.UpdatedAt).ToList()
            });
        }
        return groups;
    }

    // ── Codex ───────────────────────────────────────────────────────────

    private record CodexIndexEntry(string Id, string? ThreadName, DateTime? UpdatedAt, string? Cwd);

    private List<CliUsageProjectGroup> BuildCodexProjects()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var indexPath = Path.Combine(home, "session_index.jsonl");
        if (!File.Exists(indexPath)) return [];

        var entries = new List<CodexIndexEntry>();
        foreach (var raw in File.ReadAllLines(indexPath))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var name = root.TryGetProperty("thread_name", out var nm) ? nm.GetString() : null;
                DateTime? updated = root.TryGetProperty("updated_at", out var uEl) && uEl.TryGetDateTime(out var dt) ? dt : null;
                var cwd = root.TryGetProperty("cwd", out var cEl) ? cEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id))
                    entries.Add(new CodexIndexEntry(id, name, updated, cwd));
            }
            catch { /* skip malformed line */ }
        }

        var grouped = entries
            .GroupBy(e => e.Cwd ?? "(unknown)")
            .Select(grp => new CliUsageProjectGroup
            {
                ProjectName = Path.GetFileName(grp.Key.TrimEnd('/', '\\')) is { Length: > 0 } leaf ? leaf : grp.Key,
                RootPath = grp.Key,
                Sessions = grp
                    .OrderByDescending(e => e.UpdatedAt)
                    .Take(20)
                    .Select(e => new CliSessionInfo
                    {
                        Id = e.Id,
                        Label = e.ThreadName,
                        UpdatedAt = e.UpdatedAt,
                        Cwd = e.Cwd
                    })
                    .ToList()
            })
            .ToList();

        return grouped;
    }
}
