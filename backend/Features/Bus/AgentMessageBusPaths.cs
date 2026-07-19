namespace AgentStudio.Bus;

/// <summary>
/// Canonical layout for Agent Message Bus files. See section 4 of
/// <c>docs/system/architecture/bus/agent-message-bus.md</c>. The layout root is the watched
/// workspace's <c>logs/</c> directory; bus output is workspace evidence and
/// lives next to other workspace logs, not in the app repository.
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <code>
/// {workspace}/logs/bus/participants/&lt;id&gt;.json
/// {workspace}/logs/bus/_workspace/&lt;yyyy-mm-dd&gt;.jsonl
/// {workspace}/logs/bus/&lt;project&gt;/&lt;yyyy-mm-dd&gt;.jsonl
/// </code>
/// </remarks>
public static class AgentMessageBusPaths
{
    public const string WorkspaceScope = "_workspace";

    public static string BusRoot(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, "logs", "bus");
    }

    public static string ParticipantsDir(string workspaceRoot) =>
        Path.Combine(BusRoot(workspaceRoot), "participants");

    public static string ParticipantFile(string workspaceRoot, string participantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
        // Participant ids can carry ':' (e.g. supervisor:my-project) but Windows
        // filenames cannot. Map ':' to '-' for the on-disk filename so the doc
        // example layout (supervisor-my-project.json, agent-claude.json) matches
        // both platforms. The id inside the JSON document is preserved verbatim.
        return Path.Combine(ParticipantsDir(workspaceRoot), participantId.Replace(':', '-') + ".json");
    }

    /// <summary>
    /// Directory holding the per-day JSONL files for one project scope.
    /// Pass <c>null</c> for workspace-wide messages; the directory becomes
    /// <c>_workspace</c>.
    /// </summary>
    public static string ProjectDir(string workspaceRoot, string? project)
    {
        var slug = string.IsNullOrWhiteSpace(project) ? WorkspaceScope : project!;
        return Path.Combine(BusRoot(workspaceRoot), slug);
    }

    public static string DayFile(string workspaceRoot, string? project, DateTime utcDay)
    {
        var name = utcDay.ToString("yyyy-MM-dd") + ".jsonl";
        return Path.Combine(ProjectDir(workspaceRoot, project), name);
    }
}
