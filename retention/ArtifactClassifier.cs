namespace AgentStudio.Retention;

public static class ArtifactClassifier
{
    public const long RefuseAboveBytes = 50L * 1024L * 1024L;

    public static ArtifactClass Classify(string path)
    {
        var value = Normalize(path);
        var name = value.Split('/').LastOrDefault() ?? value;

        if (value.StartsWith(".metadata/attempt-authority", StringComparison.Ordinal)
            || value.StartsWith("logs/bus/", StringComparison.Ordinal)
            || value.Contains("/.runtime/", StringComparison.Ordinal)
            || value.StartsWith(".runtime/", StringComparison.Ordinal)
            || value.Contains("/cache/", StringComparison.Ordinal)
            || value.StartsWith("cache/", StringComparison.Ordinal)
            || value.Contains("/caches/", StringComparison.Ordinal)
            || value.StartsWith("caches/", StringComparison.Ordinal)
            || name.EndsWith(".tmp", StringComparison.Ordinal)
            || name.EndsWith(".cache", StringComparison.Ordinal)
            || name.EndsWith(".log.1", StringComparison.Ordinal))
            return ArtifactClass.Runtime;

        if (name.Equals("task.json", StringComparison.Ordinal)
            || name.Equals("timeline.jsonl", StringComparison.Ordinal)
            || (name.EndsWith("events.jsonl", StringComparison.Ordinal)
                && !name.Equals("session-events.jsonl", StringComparison.Ordinal))
            || value.Contains("/leases/", StringComparison.Ordinal)
            || value.StartsWith("leases/", StringComparison.Ordinal)
            || value.Contains("/fences/", StringComparison.Ordinal)
            || value.StartsWith("fences/", StringComparison.Ordinal)
            || value.Contains("review-attempt", StringComparison.Ordinal)
            || value.Contains("/audit", StringComparison.Ordinal)
            || value.StartsWith("audit", StringComparison.Ordinal)
            || value.Contains("orchestrator-chat", StringComparison.Ordinal)
            || value.Contains("orchestrator-turn", StringComparison.Ordinal))
            return ArtifactClass.Authority;

        if (value.Contains("/results/", StringComparison.Ordinal)
            || value.StartsWith("results/", StringComparison.Ordinal)
            || value.Contains("/attachments/", StringComparison.Ordinal)
            || value.StartsWith("attachments/", StringComparison.Ordinal)
            || name.Equals("cli-output.log", StringComparison.Ordinal)
            || IsReviewStdout(value))
            return ArtifactClass.HeavyWorkingData;

        if (name.Equals("status.md", StringComparison.Ordinal)
            || name.Equals("prompt.md", StringComparison.Ordinal)
            || value.Contains("prompt-history", StringComparison.Ordinal)
            || value.Contains("review-grade", StringComparison.Ordinal)
            || value.Contains("review-report", StringComparison.Ordinal)
            || value.Contains("integration", StringComparison.Ordinal)
            || value.Contains("commit", StringComparison.Ordinal)
            || name.Equals("session-events.jsonl", StringComparison.Ordinal)
            || value.Contains("enrichment", StringComparison.Ordinal)
            || value.Contains("post-step", StringComparison.Ordinal)
            || value.Contains(".retention-excerpts/", StringComparison.Ordinal)
            || name.Equals("archive-manifest.json", StringComparison.Ordinal))
            return ArtifactClass.Evidence;

        return ArtifactClass.Evidence;
    }

    public static bool IsCommitRefused(string path, long size) =>
        Classify(path) == ArtifactClass.HeavyWorkingData && size > RefuseAboveBytes;

    public static string Family(string path)
    {
        var value = Normalize(path);
        if (value.StartsWith(".metadata/attempt-authority", StringComparison.Ordinal))
            return value.Equals(".metadata/attempt-authority.json", StringComparison.Ordinal)
                ? "attempt-authority-live" : "attempt-authority-archive";
        if (value.StartsWith("logs/bus/", StringComparison.Ordinal)) return "bus";
        if (value.EndsWith(".log.1", StringComparison.Ordinal)) return "rotation";
        if (value.Contains("results/", StringComparison.Ordinal)) return "results";
        if (value.Contains("attachments/", StringComparison.Ordinal)) return "attachments";
        if (value.Contains("review", StringComparison.Ordinal) && IsReviewStdout(value)) return "review-stdout";
        if (value.EndsWith("cli-output.log", StringComparison.Ordinal)) return "cli-output";
        return Classify(path).ToString().ToLowerInvariant();
    }

    public static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private static bool IsReviewStdout(string value) =>
        value.Contains("review", StringComparison.Ordinal)
        && (value.EndsWith("stdout.log", StringComparison.Ordinal)
            || value.EndsWith("review.log", StringComparison.Ordinal)
            || value.Contains("review-stdout", StringComparison.Ordinal));
}
