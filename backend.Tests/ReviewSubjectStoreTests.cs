using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewSubjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "review-subject-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidateCurrentAttempt_EmbeddedFlatStorage_UsesFolderKeyWhenTaskJsonIsUnavailable()
    {
        var folder = Path.Combine(_root, ".orchestrator", "jobs", "tasks", "000", "TE-38");
        Directory.CreateDirectory(folder);
        var (authority, subject) = CompletedSubject("TE-38");

        var valid = ReviewSubjectStore.TryValidateCurrentAttempt(
            folder,
            subject,
            authority,
            out var error);

        Assert.True(valid, error);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCurrentAttempt_LegacyStorage_ReadsKeyFieldCaseInsensitively()
    {
        var folder = Path.Combine(_root, "5-human-review", "legacy-task");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), """{"Key":"TE-38"}""");
        var (authority, subject) = CompletedSubject("TE-38");

        var valid = ReviewSubjectStore.TryValidateCurrentAttempt(
            folder,
            subject,
            authority,
            out var error);

        Assert.True(valid, error);
        Assert.Null(error);
    }

    private (AttemptAuthorityService Authority, ReviewSubjectRecord Subject) CompletedSubject(string taskKey)
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
            })
            .Build();
        var authority = new AttemptAuthorityService(
            configuration,
            NullLogger<AttemptAuthorityService>.Instance);
        var run = authority.AcquireRun(
            taskKey,
            "PROJ-TE",
            null,
            "agent-runner-01",
            "host-a",
            60,
            "claim").RunAttempt!;
        var sha = new string('a', 40);
        var settled = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId,
                run.LastFence,
                run.AuthorityEpoch,
                "settle"),
            Outcome = "done",
            ResultSha = sha,
        });
        Assert.True(settled.Accepted);

        return (authority, new ReviewSubjectRecord
        {
            TaskKey = taskKey,
            RunAttemptId = run.AttemptId,
            ResultSha = sha,
            AttemptChainId = run.Lease!.LeaseId,
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
