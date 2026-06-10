namespace AgentStudio.Runner;

/// <summary>
/// ADR-0052 "always-worktree" invariant, as pure logic: a CODING run mutates the
/// source tree and therefore must execute in its own isolated task worktree -
/// EVERY time, regardless of slot count (sequential <c>max==1</c> as well as
/// parallel <c>max&gt;1</c>) and regardless of whether it is a fresh pickup, a
/// reissue, a resume, or a crash-recovery continuation. The shared main checkout
/// is read-only reference + the integration target; an agent coding run must
/// never run with its working directory pointed at it (that is the
/// cross-contamination bug ASS-1732). Read-only modes (planning / research) and
/// epic planning runs write nothing, so they may run in-place without a worktree.
///
/// <para>
/// This is intentionally separate from <see cref="ParallelSlotPolicy"/>: that
/// policy answers "may a second slot be admitted?"; this one answers "must this
/// run be isolated, and is the chosen working directory legal?". Keeping it pure
/// lets the decision be unit-tested without spinning a runner.
/// </para>
/// </summary>
public static class WorktreeRunPolicy
{
    /// <summary>
    /// True when this run must execute inside an isolated task worktree. A coding
    /// run always does; read-only modes (planning / research) and epic planning
    /// runs do not (they mutate nothing). The caller still gates on the project
    /// root actually being a git repository - a non-git workspace has no worktree
    /// machinery and falls back to running in-place.
    /// </summary>
    public static bool RequiresWorktree(string? mode, bool isEpicPlanningRun)
        => !isEpicPlanningRun && !TaskModes.IsReadOnly(mode);

    /// <summary>
    /// True when a run that <paramref name="requiresWorktree"/> would illegally
    /// execute in the shared main checkout (<paramref name="runWorkingDir"/> ==
    /// <paramref name="mainCheckoutRoot"/>). This is the guard condition: a coding
    /// run resolving to the main checkout means worktree preparation silently fell
    /// through, and the run must be refused + escalated rather than allowed to
    /// dirty the shared tree. Read-only / planning runs (requiresWorktree == false)
    /// legitimately run in the main checkout, so they never trip the guard.
    /// </summary>
    public static bool IsMainCheckoutViolation(bool requiresWorktree, string? runWorkingDir, string? mainCheckoutRoot)
    {
        if (!requiresWorktree) return false;
        if (string.IsNullOrWhiteSpace(runWorkingDir) || string.IsNullOrWhiteSpace(mainCheckoutRoot)) return false;
        return PathsEqual(runWorkingDir!, mainCheckoutRoot!);
    }

    /// <summary>
    /// A recorded CLI session may be resumed only when it was born in the SAME
    /// working directory this run will use (the CLI keys <c>--resume</c> by cwd; a
    /// session born elsewhere hangs). Non-worktree runs always run in the same
    /// place, so their sessions resume. Worktree runs resume only when the
    /// worktree was RE-USED (<paramref name="worktreeReused"/>) - a freshly cut
    /// worktree means any prior session lived in a different directory (the old
    /// main checkout) and must start fresh.
    /// </summary>
    public static bool CanResumeSession(bool isWorktreeRun, bool worktreeReused)
        => !isWorktreeRun || worktreeReused;

    /// <summary>Path equality after full-path normalization, case-insensitive on Windows.</summary>
    public static bool PathsEqual(string a, string b)
        => string.Equals(
            Normalize(a),
            Normalize(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
