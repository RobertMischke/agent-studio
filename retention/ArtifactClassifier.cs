namespace AgentStudio.Retention;

public sealed record ArtifactClassification(
    ArtifactClass ArtifactClass,
    string RuleFamily,
    bool MayEnterGit);

public sealed class ArtifactClassifier
{
    public IReadOnlyList<string> IntermediateCommitExcludeGlobs { get; } =
    [
        "*/logs/cli-output.log",
        "*/logs/cli-output.log.1",
        "*/results/*",
        "*/attachments/*",
        "*review*stdout*.log",
    ];

    public ArtifactClassification Classify(string path)
    {
        var value = Normalize(path);
        var file = value.Split('/').LastOrDefault() ?? value;

        if (value.StartsWith(".metadata/attempt-authority", StringComparison.OrdinalIgnoreCase))
            return Runtime("attempt-authority");
        if (value.StartsWith("logs/bus/", StringComparison.OrdinalIgnoreCase)
            || HasSegment(value, ".runtime")
            || HasSegment(value, "cache")
            || HasSegment(value, "caches")
            || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".cache", StringComparison.OrdinalIgnoreCase))
            return Runtime("runtime");

        if (file.Equals("session-events.jsonl", StringComparison.OrdinalIgnoreCase))
            return Evidence("session-events");

        if (file.Equals("task.json", StringComparison.OrdinalIgnoreCase)
            || file.Equals("job.json", StringComparison.OrdinalIgnoreCase)
            || file.Contains("events", StringComparison.OrdinalIgnoreCase)
            || file.Contains("timeline", StringComparison.OrdinalIgnoreCase)
            || HasSegment(value, "leases")
            || HasSegment(value, "fences")
            || file.Contains("lease", StringComparison.OrdinalIgnoreCase)
            || file.Contains("fence", StringComparison.OrdinalIgnoreCase)
            || file.Contains("review-attempt", StringComparison.OrdinalIgnoreCase)
            || file.Contains("audit", StringComparison.OrdinalIgnoreCase)
            || file.Contains("orchestrator-chat", StringComparison.OrdinalIgnoreCase))
            return Authority("authority");

        if (value.Contains("/logs/cli-output.log", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("logs/cli-output.log", StringComparison.OrdinalIgnoreCase))
            return Heavy("cli-output");
        if (HasSegment(value, "results"))
            return Heavy("results");
        if (HasSegment(value, "attachments"))
            return Heavy("attachments");
        if (file.Contains("review", StringComparison.OrdinalIgnoreCase)
            && (file.Contains("stdout", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
            return Heavy("review-stdout");

        if (file.Equals("status.md", StringComparison.OrdinalIgnoreCase)
            || file.Contains("prompt", StringComparison.OrdinalIgnoreCase)
            || file.Contains("review-grade", StringComparison.OrdinalIgnoreCase)
            || file.Contains("report", StringComparison.OrdinalIgnoreCase)
            || file.Contains("integration", StringComparison.OrdinalIgnoreCase)
            || file.Contains("commit", StringComparison.OrdinalIgnoreCase)
            || file.Contains("enrichment", StringComparison.OrdinalIgnoreCase)
            || file.Contains("post-step", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("excerpts/", StringComparison.OrdinalIgnoreCase))
            return Evidence("evidence");

        return Evidence("evidence-other");
    }

    public bool IsCommitRefused(string path, long bytes, RetentionPolicy? policy = null)
    {
        var classification = Classify(path);
        var threshold = (policy ?? RetentionPolicy.Default())
            .WorkspaceDefaults.For(classification.ArtifactClass).RefuseAboveBytes;
        return classification.ArtifactClass == ArtifactClass.HeavyWorkingData
            && threshold is not null
            && bytes > threshold;
    }

    private static string Normalize(string path) => path
        .Replace('\\', '/')
        .TrimStart('/');

    private static bool HasSegment(string value, string segment) =>
        value.Equals(segment, StringComparison.OrdinalIgnoreCase)
        || value.StartsWith(segment + "/", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith("/" + segment, StringComparison.OrdinalIgnoreCase)
        || value.Contains("/" + segment + "/", StringComparison.OrdinalIgnoreCase);

    private static ArtifactClassification Authority(string family) =>
        new(ArtifactClass.Authority, family, true);
    private static ArtifactClassification Evidence(string family) =>
        new(ArtifactClass.Evidence, family, true);
    private static ArtifactClassification Heavy(string family) =>
        new(ArtifactClass.HeavyWorkingData, family, true);
    private static ArtifactClassification Runtime(string family) =>
        new(ArtifactClass.Runtime, family, false);
}
