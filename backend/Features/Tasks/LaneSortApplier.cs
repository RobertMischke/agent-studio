using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// F35: applies a per-project lane sort strategy to a flat list of jobs that
/// may span multiple projects. The kanban grouped endpoint mixes projects
/// inside one lane, so we group by project, sort each project's jobs using
/// the strategy resolved from that project's <see cref="ProjectSettings"/>,
/// and concatenate the groups in alphabetical project order for a stable
/// global result. Pure function; called from <c>/api/tasks/grouped</c>.
/// </summary>
public static class LaneSortApplier
{
    public static IEnumerable<TaskInfo> Sort(
        IEnumerable<TaskInfo> jobs,
        string lane,
        Func<string, ProjectSettings> settingsResolver)
    {
        var byProject = jobs.GroupBy(j => j.ProjectName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var result = new List<TaskInfo>();
        foreach (var group in byProject)
        {
            var settings = settingsResolver(group.Key);
            var strategy = LaneSortStrategies.Resolve(settings, lane);
            var comparer = LaneSortStrategies.GetComparer(strategy);
            var sorted = group.ToList();
            sorted.Sort(comparer);
            result.AddRange(sorted);
        }
        return result;
    }
}
