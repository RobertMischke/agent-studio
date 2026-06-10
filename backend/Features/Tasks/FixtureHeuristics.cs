
namespace AgentStudio.Tasks;

/// <summary>
/// Pure-function classifier for "is this job folder almost certainly a
/// Playwright / E2E fixture?". Used by <see cref="FixtureMigrationService"/>
/// to retrofit the <c>fixture: true</c> marker onto folders that pre-date
/// the marker. The bar is intentionally conservative: a false positive
/// hides real user work, which is much worse than missing a fixture.
/// </summary>
public static class FixtureHeuristics
{
    /// <summary>
    /// True when the id or title looks like a Playwright fixture and the
    /// job is not already marked. Matches the patterns the existing e2e
    /// helpers and historical fixture creators use:
    /// <list type="bullet">
    ///   <item><description>id starts with <c>e2e-</c> or <c>e2e_</c> (the
    ///     <c>jobs.ts</c> helper convention)</description></item>
    ///   <item><description>title starts with the standalone token
    ///     <c>e2e</c> (case-insensitive), separated from the next word by
    ///     space, hyphen, or end of string</description></item>
    ///   <item><description>id ends with an ISO timestamp suffix the
    ///     fixture builders bake into ids
    ///     (<c>...-2026-05-04t16-45-28-893z</c>) and starts with
    ///     <c>e2e</c></description></item>
    /// </list>
    /// We deliberately do NOT match on title containing "test" or "spec"
    /// alone; those words appear in legitimate user tasks.
    /// </summary>
    public static bool IsLikelyFixture(string id, string title)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        var idLower = id.ToLowerInvariant();
        if (idLower.StartsWith("e2e-") || idLower.StartsWith("e2e_")) return true;

        var titleTrim = (title ?? string.Empty).TrimStart();
        if (titleTrim.Length >= 3
            && titleTrim[..3].Equals("e2e", StringComparison.OrdinalIgnoreCase)
            && (titleTrim.Length == 3
                || titleTrim[3] == ' '
                || titleTrim[3] == '-'
                || titleTrim[3] == ':'))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Convenience overload that pulls id + title off a <see cref="TaskInfo"/>.
    /// </summary>
    public static bool IsLikelyFixture(TaskInfo info) =>
        IsLikelyFixture(info.Id, info.Title);
}
