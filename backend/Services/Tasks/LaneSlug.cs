using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// Single source of truth for the duplicate-slug root-cause fix. A task
/// folder name is the canonical task id, so a slug that is reused in a
/// second lane makes every later cross-lane move (archive, complete)
/// collide on the occupied name — the recurring
/// <c>409 … 'slug' already exists in 7-archive</c>. Both the create flow
/// (<see cref="TaskMutationService.CreateJob"/>) and the collision-safe
/// move (<see cref="TaskStateMachine.MoveJob"/>) reserve a name that is
/// unique across <em>all</em> lanes of the watch path so a later move can
/// never collide. Callers hold the lane mutex around the check plus the
/// folder create/move that follows, so two concurrent writers cannot pick
/// the same suffix.
/// </summary>
internal static class LaneSlug
{
    /// <summary>
    /// True when <paramref name="slug"/> already names a folder in any lane
    /// of <paramref name="watchPath"/> (including <c>7-archive</c>).
    /// </summary>
    public static bool IsTaken(string watchPath, string slug) =>
        TaskStates.All.Any(state => Directory.Exists(Path.Combine(watchPath, state, slug)));

    /// <summary>
    /// Returns <paramref name="baseSlug"/> unchanged when it is free across
    /// every lane, otherwise appends an incrementing <c>-N</c> suffix
    /// (starting at <c>-2</c>) until an unused name is found.
    /// </summary>
    public static string EnsureUnique(string watchPath, string baseSlug)
    {
        if (!IsTaken(watchPath, baseSlug)) return baseSlug;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseSlug}-{n}";
            if (!IsTaken(watchPath, candidate)) return candidate;
        }
    }
}
