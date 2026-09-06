using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class ArtifactClassifierTests
{
    private readonly ArtifactClassifier _classifier = new();

    [Theory]
    [InlineData("task.json", ArtifactClass.Authority)]
    [InlineData("events.jsonl", ArtifactClass.Authority)]
    [InlineData("logs/timeline.jsonl", ArtifactClass.Authority)]
    [InlineData("leases/current.json", ArtifactClass.Authority)]
    [InlineData("fences.json", ArtifactClass.Authority)]
    [InlineData("review-attempts.json", ArtifactClass.Authority)]
    [InlineData("audit.jsonl", ArtifactClass.Authority)]
    [InlineData("orchestrator-chat-turns.jsonl", ArtifactClass.Authority)]
    [InlineData("status.md", ArtifactClass.Evidence)]
    [InlineData("prompt.md", ArtifactClass.Evidence)]
    [InlineData("prompt-history/1.md", ArtifactClass.Evidence)]
    [InlineData("review-grades.json", ArtifactClass.Evidence)]
    [InlineData("summary-report.md", ArtifactClass.Evidence)]
    [InlineData("integration-records.json", ArtifactClass.Evidence)]
    [InlineData("commit-list.json", ArtifactClass.Evidence)]
    [InlineData("logs/session-events.jsonl", ArtifactClass.Evidence)]
    [InlineData("enrichment.json", ArtifactClass.Evidence)]
    [InlineData("post-step-results.json", ArtifactClass.Evidence)]
    [InlineData("logs/cli-output.log", ArtifactClass.HeavyWorkingData)]
    [InlineData("logs/cli-output.log.1", ArtifactClass.HeavyWorkingData)]
    [InlineData("review-agent-stdout.log", ArtifactClass.HeavyWorkingData)]
    [InlineData("results/trace.zip", ArtifactClass.HeavyWorkingData)]
    [InlineData("results/screenshot.png", ArtifactClass.HeavyWorkingData)]
    [InlineData("attachments/input.bin", ArtifactClass.HeavyWorkingData)]
    [InlineData(".metadata/attempt-authority.json", ArtifactClass.Runtime)]
    [InlineData(".metadata/attempt-authority.archive-2026-01-01.json", ArtifactClass.Runtime)]
    [InlineData("logs/bus/project/2026-01-01.jsonl", ArtifactClass.Runtime)]
    [InlineData(".runtime/process.json", ArtifactClass.Runtime)]
    [InlineData("cache/index.bin", ArtifactClass.Runtime)]
    [InlineData("scratch.tmp", ArtifactClass.Runtime)]
    public void ClassifiesDossierPathFamilies(string path, ArtifactClass expected) =>
        Assert.Equal(expected, _classifier.Classify(path).ArtifactClass);

    [Fact]
    public void RefusesHeavyFileOnlyAboveFiftyMiB()
    {
        Assert.False(_classifier.IsCommitRefused("logs/cli-output.log", 50L * 1024 * 1024));
        Assert.True(_classifier.IsCommitRefused("logs/cli-output.log", 50L * 1024 * 1024 + 1));
        Assert.False(_classifier.IsCommitRefused("status.md", 100L * 1024 * 1024));
    }
}
