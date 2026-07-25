namespace AgentStudio.TestRuns;

public static class TestRunTaskProjection
{
    public static IEnumerable<TaskInfo> WithTestRunEvidence(
        this IEnumerable<TaskInfo> jobs,
        IReadOnlyDictionary<string, TaskTestRunEvidence> lookup)
    {
        foreach (var job in jobs)
            yield return lookup.TryGetValue(job.TaskKey, out var evidence)
                ? job with { TestEvidence = evidence }
                : job;
    }
}
