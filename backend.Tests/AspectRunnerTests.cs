using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Direct tests against <see cref="AspectRunnerService"/>. The CLI is
/// stubbed: each aspect's reply is supplied per-aspect-id so a
/// fixture can drive a deterministic mix of pass / concerns / block
/// responses without spinning up a model.
///
/// ADR-0025: an unparseable model reply must default to a Concerns
/// verdict (no silent durchwinken). The aspect runner writes one
/// <c>aspect-{id}.md</c> file per aspect into the job folder; the
/// frontmatter status token is the load-bearing field downstream
/// readers rely on.
/// </summary>
public class AspectRunnerTests : IDisposable
{
    private readonly string _jobFolder;

    public AspectRunnerTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "aspect-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task AllAspectsPass_OverallIsPass_NoConcernTags_AllMdsWritten()
    {
        var runner = BuildRunner(_ => "[[ASPECT_VERDICT: status=pass; summary=Looks fine.]]\n[[TASK_DONE]]");

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Pass, report.Overall);
        Assert.Empty(report.ConcernTagIds);
        Assert.Equal(4, report.Verdicts.Count);

        foreach (var aspect in new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" })
        {
            var path = Path.Combine(_jobFolder, $"aspect-{aspect}.md");
            Assert.True(File.Exists(path), $"missing aspect MD for {aspect}");
            var content = File.ReadAllText(path);
            Assert.Equal(AspectStatus.Pass, AspectVerdictParsing.ReadStatusFromReport(content));
            Assert.Contains("Looks fine.", content);
        }
    }

    [Fact]
    public async Task OneConcernsThreePasses_OverallIsConcerns_ConcernTagAddedForThatNamespace()
    {
        var runner = BuildRunner(aspect => aspect switch
        {
            "code-quality" => "[[ASPECT_VERDICT: status=concerns; summary=Dead helper left behind.]]\n[[TASK_DONE]]",
            _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"
        });

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Concerns, report.Overall);
        Assert.Single(report.ConcernTagIds);
        Assert.Contains("quality:concerns", report.ConcernTagIds);

        var qualityMd = File.ReadAllText(Path.Combine(_jobFolder, "aspect-code-quality.md"));
        Assert.Equal(AspectStatus.Concerns, AspectVerdictParsing.ReadStatusFromReport(qualityMd));
        Assert.Contains("quality:concerns", qualityMd);

        var docsMd = File.ReadAllText(Path.Combine(_jobFolder, "aspect-documentation-impact.md"));
        Assert.Equal(AspectStatus.Pass, AspectVerdictParsing.ReadStatusFromReport(docsMd));
    }

    [Fact]
    public async Task QualityAndTestsBothConcerns_DedupesToOneQualityConcernsTag()
    {
        // code-quality and tests-and-evidence share the `quality` namespace
        // for their concern tag. Two concerns in that namespace must
        // collapse to one chip on the card so the user does not see two
        // visually identical "quality:concerns" tags.
        var runner = BuildRunner(aspect => aspect switch
        {
            "code-quality" => "[[ASPECT_VERDICT: status=concerns; summary=Helper is duplicated.]]\n[[TASK_DONE]]",
            "tests-and-evidence" => "[[ASPECT_VERDICT: status=concerns; summary=No regression test for the fix.]]\n[[TASK_DONE]]",
            _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"
        });

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Concerns, report.Overall);
        Assert.Single(report.ConcernTagIds);
        Assert.Equal("quality:concerns", report.ConcernTagIds[0]);
    }

    [Fact]
    public async Task OneBlockBeatsConcerns_OverallIsBlock_FollowUpListsBothFindings()
    {
        var runner = BuildRunner(aspect => aspect switch
        {
            "requirement-fit" => "[[ASPECT_VERDICT: status=block; summary=Acceptance criterion 2 not met.]]\n[[TASK_DONE]]",
            "code-quality" => "[[ASPECT_VERDICT: status=concerns; summary=Helper duplicated.]]\n[[TASK_DONE]]",
            _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"
        });

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Block, report.Overall);
        Assert.Contains("requirement:concerns", report.ConcernTagIds);
        Assert.Contains("quality:concerns", report.ConcernTagIds);
        Assert.Contains("requirement-fit", report.FollowUpSummary);
        Assert.Contains("Acceptance criterion 2 not met.", report.FollowUpSummary);
        Assert.Contains("code-quality", report.FollowUpSummary);
        Assert.Contains("Helper duplicated.", report.FollowUpSummary);
    }

    [Fact]
    public async Task UnparseableReply_DefaultsToConcerns_NoSilentDurchwinken()
    {
        // The user's hard rule: a job that lands in 4-auto-review must
        // not pass through with no opinion. An empty / unparseable
        // model reply maps to Concerns so the user still sees a chip
        // and can drill in.
        var runner = BuildRunner(_ => "I have no opinion.");

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "code-quality" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Concerns, report.Overall);
        Assert.Single(report.ConcernTagIds);
        Assert.Equal("quality:concerns", report.ConcernTagIds[0]);

        var content = File.ReadAllText(Path.Combine(_jobFolder, "aspect-code-quality.md"));
        Assert.Equal(AspectStatus.Concerns, AspectVerdictParsing.ReadStatusFromReport(content));
    }

    [Fact]
    public async Task UnknownAspectId_IsSkippedWithoutFailingTheRun()
    {
        var runner = BuildRunner(_ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "code-quality", "non-existent-aspect" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Single(report.Verdicts);
        Assert.Equal("code-quality", report.Verdicts[0].Aspect);
    }

    [Fact]
    public void AspectVerdictParsing_RoundTripsStatusToken()
    {
        var v = new AspectVerdict("code-quality", AspectStatus.Concerns, "summary text", "body", "quality:concerns");
        var rendered = AspectVerdictParsing.RenderReport(v, DateTime.UtcNow);
        Assert.Equal(AspectStatus.Concerns, AspectVerdictParsing.ReadStatusFromReport(rendered));
        Assert.Contains("aspect: code-quality", rendered);
        Assert.Contains("tag: quality:concerns", rendered);
    }

    [Theory]
    [InlineData("[[ASPECT_VERDICT: status=pass; summary=ok]]", AspectStatus.Pass, "ok")]
    [InlineData("[[ASPECT_VERDICT:status=concerns;summary=needs work]]", AspectStatus.Concerns, "needs work")]
    [InlineData("[[ASPECT_VERDICT: status=block; summary=must fix]]", AspectStatus.Block, "must fix")]
    [InlineData("[[ASPECT_VERDICT: SUMMARY=order-flip; STATUS=pass]]", AspectStatus.Pass, "order-flip")]
    public void AspectVerdictParsing_AcceptsCommonShapes(string input, AspectStatus expectedStatus, string expectedSummary)
    {
        var parsed = AspectVerdictParsing.ParseVerdict(input);
        Assert.NotNull(parsed);
        Assert.Equal(expectedStatus, parsed!.Value.Status);
        Assert.Equal(expectedSummary, parsed.Value.Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no sentinel here")]
    [InlineData("[[ASPECT_VERDICT: status=mystery; summary=x]]")]
    public void AspectVerdictParsing_RejectsUnparseable(string input)
    {
        Assert.Null(AspectVerdictParsing.ParseVerdict(input));
    }

    private AspectRunInputs BuildInputs() => new(
        Project: "demo",
        JobId: "test-job",
        JobTitle: "Test job",
        JobFolderPath: _jobFolder,
        TaskBody: "# Task\n\nDo the thing.",
        RecentLog: "[12:00:00] [stdout] running\n[12:00:01] [stdout] [[TASK_DONE]]",
        DiffSummary: "Files: src/foo.ts (+10, -2)",
        StatusSummary: "# Status\n\nDone.");

    private AspectRunnerService BuildRunner(Func<string, string> stub)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        runner.CliRunner = (aspectId, _, _, _, _, _) => Task.FromResult(stub(aspectId));
        return runner;
    }
}
