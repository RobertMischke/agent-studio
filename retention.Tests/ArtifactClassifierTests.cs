using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class ArtifactClassifierTests
{
    public static TheoryData<string, ArtifactClass> Families => new()
    {
        { "projects/P/tasks/7-archive/T/task.json", ArtifactClass.Authority },
        { "projects/P/tasks/7-archive/T/events.jsonl", ArtifactClass.Authority },
        { "projects/P/tasks/7-archive/T/logs/timeline.jsonl", ArtifactClass.Authority },
        { "projects/P/tasks/7-archive/T/leases/current.json", ArtifactClass.Authority },
        { "projects/P/tasks/7-archive/T/fences/run.json", ArtifactClass.Authority },
        { "projects/P/tasks/7-archive/T/review-attempts.json", ArtifactClass.Authority },
        { "projects/P/tasks/7-archive/T/audit.jsonl", ArtifactClass.Authority },
        { "projects/P/.orchestrator/orchestrator-chat.jsonl", ArtifactClass.Authority },
        { "projects/P/tasks/7-archive/T/status.md", ArtifactClass.Evidence },
        { "projects/P/tasks/7-archive/T/prompt.md", ArtifactClass.Evidence },
        { "projects/P/tasks/7-archive/T/prompt-history/1.md", ArtifactClass.Evidence },
        { "projects/P/tasks/7-archive/T/review-grades.json", ArtifactClass.Evidence },
        { "projects/P/tasks/7-archive/T/review-report.md", ArtifactClass.Evidence },
        { "projects/P/tasks/7-archive/T/integration-records.json", ArtifactClass.Evidence },
        { "projects/P/tasks/7-archive/T/commits.json", ArtifactClass.Evidence },
        { "projects/P/tasks/7-archive/T/logs/session-events.jsonl", ArtifactClass.Evidence },
        { "projects/P/tasks/7-archive/T/enrichment.md", ArtifactClass.Evidence },
        { "projects/P/tasks/7-archive/T/post-step-result.json", ArtifactClass.Evidence },
        { "projects/P/tasks/7-archive/T/logs/cli-output.log", ArtifactClass.HeavyWorkingData },
        { "projects/P/tasks/7-archive/T/review/review-stdout.log", ArtifactClass.HeavyWorkingData },
        { "projects/P/tasks/7-archive/T/results/trace.zip", ArtifactClass.HeavyWorkingData },
        { "projects/P/tasks/7-archive/T/attachments/input.png", ArtifactClass.HeavyWorkingData },
        { ".metadata/attempt-authority-2026-01-01.json", ArtifactClass.Runtime },
        { "logs/bus/2026-01-01.jsonl", ArtifactClass.Runtime },
        { "projects/P/tasks/3-progress/T/.runtime/lease", ArtifactClass.Runtime },
        { "projects/P/tasks/3-progress/T/cache/tool.bin", ArtifactClass.Runtime },
        { "projects/P/tasks/3-progress/T/output.tmp", ArtifactClass.Runtime },
        { "projects/P/tasks/3-progress/T/logs/cli-output.log.1", ArtifactClass.Runtime },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void Classifies_every_dossier_path_family(string path, ArtifactClass expected) =>
        Assert.Equal(expected, ArtifactClassifier.Classify(path));

    [Fact]
    public void Refuses_only_class_c_above_50_mib()
    {
        Assert.False(ArtifactClassifier.IsCommitRefused("results/trace.zip", 50L * 1024 * 1024));
        Assert.True(ArtifactClassifier.IsCommitRefused("results/trace.zip", 50L * 1024 * 1024 + 1));
        Assert.False(ArtifactClassifier.IsCommitRefused("status.md", 80L * 1024 * 1024));
    }
}
