using System.Text;

namespace OrchestratorApi.Services.ProjectChat;

/// <summary>
/// Read/write surface over the per-month markdown files described in
/// Slice D. Each turn lives in its own file under
/// <c>&lt;projectFolder&gt;/chat/&lt;yyyy-MM&gt;/&lt;turnId&gt;.md</c>;
/// the index DB sitting next to the months is a derived artefact and is
/// out of scope here (see <see cref="ProjectChatIndex"/>).
///
/// The store is intentionally stateless and dependency-free: every call
/// reads from disk on demand. Caching/aggregation belongs to the index.
/// </summary>
public sealed class ProjectChatStore
{
    private readonly ILogger<ProjectChatStore> _logger;

    public ProjectChatStore(ILogger<ProjectChatStore> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Persist a turn. Returns the absolute path that was written. The
    /// month folder is created on demand. Re-writing the same turn-id
    /// in the same month overwrites in place — callers that want
    /// optimistic concurrency should detect that case beforehand.
    /// </summary>
    public string Write(string projectFolder, ProjectChatTurn turn)
    {
        var chatRoot = ProjectChatPaths.ChatRoot(projectFolder);
        var monthDir = ProjectChatPaths.MonthFolder(chatRoot, turn.Ts);
        Directory.CreateDirectory(monthDir);
        var file = ProjectChatPaths.TurnFile(chatRoot, turn.Ts, turn.TurnId);
        File.WriteAllText(file, ProjectChatTurnSerializer.Serialize(turn), new UTF8Encoding(false));
        return file;
    }

    public ProjectChatTurn? ReadFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var content = File.ReadAllText(filePath);
            return ProjectChatTurnSerializer.Parse(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse chat turn at {Path}", filePath);
            return null;
        }
    }

    public ProjectChatTurn? FindById(string projectFolder, string turnId)
    {
        var chatRoot = ProjectChatPaths.ChatRoot(projectFolder);
        if (!Directory.Exists(chatRoot)) return null;
        var safeId = turnId;
        if (!IsSafeTurnId(safeId)) return null;
        // Walk newest month first — typical lookups go after recent turns.
        foreach (var month in EnumerateMonthFoldersDescending(chatRoot))
        {
            var candidate = Path.Combine(month, safeId + ".md");
            if (File.Exists(candidate))
            {
                var t = ReadFile(candidate);
                if (t != null) return t;
            }
        }
        return null;
    }

    /// <summary>
    /// Stream every chat turn under a project, oldest first. Used by the
    /// index rebuild + by tests; callers that only want a window should
    /// use <see cref="ReadScroll"/>.
    /// </summary>
    public IEnumerable<(string Path, ProjectChatTurn Turn)> EnumerateAll(string projectFolder)
    {
        var chatRoot = ProjectChatPaths.ChatRoot(projectFolder);
        if (!Directory.Exists(chatRoot)) yield break;
        foreach (var month in ProjectChatPaths.EnumerateMonthFolders(chatRoot))
        {
            var files = Directory.EnumerateFiles(month, "*.md").ToArray();
            // We sort by file content's `ts` later, but the on-disk path
            // sort is good enough as a stable secondary order for the
            // usual case where turn-ids embed nothing about time.
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files)
            {
                var t = ReadFile(f);
                if (t != null) yield return (f, t);
            }
        }
    }

    /// <summary>
    /// Slice D's scroll cursor: returns up to <paramref name="limit"/>
    /// turns whose <c>ts</c> falls strictly before or strictly after the
    /// caller's anchor. <paramref name="before"/> wins when both are set.
    /// Result for <paramref name="before"/> is reverse-chronological
    /// (most-recent first); for <paramref name="after"/> chronological
    /// — matches what the FE virtualiser wants without an extra reverse.
    /// </summary>
    public List<ProjectChatTurn> ReadScroll(string projectFolder, DateTime? before, DateTime? after, int limit)
    {
        if (limit <= 0) return [];
        if (limit > 200) limit = 200;
        var all = EnumerateAll(projectFolder).Select(p => p.Turn).ToList();
        all.Sort(static (a, b) => DateTime.Compare(a.Ts, b.Ts));

        IEnumerable<ProjectChatTurn> filtered;
        bool reverse;

        if (before.HasValue)
        {
            var anchor = before.Value;
            filtered = all.Where(t => t.Ts < anchor).Reverse();
            reverse = true;
        }
        else if (after.HasValue)
        {
            var anchor = after.Value;
            filtered = all.Where(t => t.Ts > anchor);
            reverse = false;
        }
        else
        {
            // No anchor: return the most recent N in reverse order so the
            // FE's initial load lands the bottom of the live list.
            filtered = ((IEnumerable<ProjectChatTurn>)all).Reverse();
            reverse = true;
        }

        var page = filtered.Take(limit).ToList();
        if (reverse) return page; // already reverse-chronological
        return page;
    }

    public int CountAll(string projectFolder)
    {
        var chatRoot = ProjectChatPaths.ChatRoot(projectFolder);
        if (!Directory.Exists(chatRoot)) return 0;
        int n = 0;
        foreach (var m in ProjectChatPaths.EnumerateMonthFolders(chatRoot))
        {
            n += Directory.EnumerateFiles(m, "*.md").Count();
        }
        return n;
    }

    private static IEnumerable<string> EnumerateMonthFoldersDescending(string chatRoot)
    {
        var dirs = Directory.EnumerateDirectories(chatRoot)
            .Where(d => ProjectChatPaths.IsMonthFolder(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal);
        foreach (var d in dirs) yield return d;
    }

    private static bool IsSafeTurnId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.Length > 64) return false;
        foreach (var c in id)
        {
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) return false;
        }
        return true;
    }
}
