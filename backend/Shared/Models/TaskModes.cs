namespace AgentStudio.Shared;

/// <summary>
/// Task execution mode - orthogonal to <see cref="TaskKinds"/> (task|epic). It
/// describes how a leaf task's run behaves:
/// <list type="bullet">
/// <item><c>coding</c> (default) - mutates source, runs the full git pipeline.</item>
/// <item><c>planning</c> - read-only; analyses the codebase and proposes the next
///   concrete piece of work (promotable to a coding task).</item>
/// <item><c>research</c> - read-only; broader fact-finding, web access on by
///   default; the deliverable is a report.</item>
/// </list>
/// Read-only modes skip the git pre/post pipeline steps (no worktree, no commit,
/// no merge) and are always parallel-ok. Persisted as the <c>"mode"</c> field in
/// <c>job.json</c>; keep values stable. See
/// docs/research/planning-research-task-kinds-2026-05.md.
/// </summary>
public static class TaskModes
{
    public const string Coding = "coding";
    public const string Planning = "planning";
    public const string Research = "research";

    public static readonly string[] All = [Coding, Planning, Research];

    /// <summary>Coerce a free-form value to a known mode; unknown / empty -> Coding.</summary>
    public static string Normalize(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v switch
        {
            Planning => Planning,
            Research => Research,
            _ => Coding,
        };
    }

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && System.Array.IndexOf(All, value!.Trim().ToLowerInvariant()) >= 0;

    /// <summary>
    /// Read-only modes (planning / research) produce a report and skip the git
    /// pre/post steps; coding mutates the tree.
    /// </summary>
    public static bool IsReadOnly(string? value) => Normalize(value) is Planning or Research;
}
