
namespace AgentStudio.ProjectChat;

/// <summary>
/// Resolves on-disk locations for the per-project, file-backed chat
/// surface introduced in Slice D of "project-chat-becomes-primary".
/// Markdown files are the source of truth (ADR-0023); the index DB is
/// derived. Chat is project-level, sibling to the lane / task folders
/// (ADR-0024), so for a watch-path entry whose <c>Path</c> resolves to
/// <c>&lt;workspace&gt;/projects/&lt;project&gt;</c> the chat folder lives
/// at <c>&lt;Path&gt;/chat/</c>.
/// </summary>
public static class ProjectChatPaths
{
    public const string ChatFolderName = "chat";
    public const string IndexFileName = ".index.db";

    public static string ChatRoot(WatchPathEntry entry) => ChatRoot(entry.Path);

    public static string ChatRoot(string watchPathFolder) =>
        Path.Combine(watchPathFolder, ChatFolderName);

    public static string IndexDbPath(WatchPathEntry entry) => IndexDbPath(entry.Path);
    public static string IndexDbPath(string watchPathFolder) =>
        Path.Combine(ChatRoot(watchPathFolder), IndexFileName);

    /// <summary>
    /// "yyyy-MM" folder for a turn's UTC timestamp. Per-month buckets
    /// keep folders small enough that filesystem listings stay fast and
    /// git diffs stay tractable on long-running projects.
    /// </summary>
    public static string MonthFolder(string chatRoot, DateTime tsUtc) =>
        Path.Combine(chatRoot, tsUtc.ToUniversalTime().ToString("yyyy-MM"));

    public static string TurnFile(string chatRoot, DateTime tsUtc, string turnId) =>
        Path.Combine(MonthFolder(chatRoot, tsUtc), turnId + ".md");

    /// <summary>
    /// Enumerate every <c>yyyy-MM</c> sub-folder under the chat root in
    /// chronological order. Caller is responsible for deciding whether to
    /// walk newest-first (scroll back) or oldest-first (rebuild).
    /// </summary>
    public static IEnumerable<string> EnumerateMonthFolders(string chatRoot)
    {
        if (!Directory.Exists(chatRoot)) yield break;
        var dirs = Directory.EnumerateDirectories(chatRoot)
            .Where(d => IsMonthFolder(Path.GetFileName(d)))
            .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal);
        foreach (var d in dirs) yield return d;
    }

    public static bool IsMonthFolder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name!.Length != 7) return false;
        if (name[4] != '-') return false;
        for (int i = 0; i < 4; i++) if (!char.IsDigit(name[i])) return false;
        for (int i = 5; i < 7; i++) if (!char.IsDigit(name[i])) return false;
        return true;
    }
}
