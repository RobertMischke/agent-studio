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
    public async Task UnparseableReply_DefaultsToConcerns_WithUnparseableTag()
    {
        // The user's hard rule: a job that lands in 4-auto-review must
        // not pass through with no opinion. An unparseable model reply
        // still produces a Concerns verdict so the user sees a chip.
        // F1 (2026-05-21): the tag changed from `{namespace}:concerns`
        // to `review:unparseable` so the operator can distinguish
        // "model has a real concern" from "format violation".
        var runner = BuildRunner(_ => "I have no opinion.");

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "code-quality" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Concerns, report.Overall);
        Assert.Single(report.ConcernTagIds);
        Assert.Equal("review:unparseable", report.ConcernTagIds[0]);

        var content = File.ReadAllText(Path.Combine(_jobFolder, "aspect-code-quality.md"));
        Assert.Equal(AspectStatus.Concerns, AspectVerdictParsing.ReadStatusFromReport(content));
    }

    [Theory]
    [InlineData("Looks fine.\n\nStatus: pass", AspectStatus.Pass)]
    [InlineData("**Status:** concerns\n\nNeeds review.", AspectStatus.Concerns)]
    [InlineData("Verdict says block.\nstatus = blocked\n", AspectStatus.Block)]
    public void ParseVerdict_FallsBack_When_Sentinel_Missing_But_StatusLineFound(string reply, AspectStatus expected)
    {
        // F1 tolerant fallback: when the model drops the [[ASPECT_VERDICT]]
        // sentinel but still says "Status: pass|concerns|block" on a
        // line, the parser now recovers that signal instead of bouncing
        // straight to the no-parseable-verdict fallback. Reduces false-
        // positive "concerns" tags from format violations.
        var parsed = AspectVerdictParsing.ParseVerdict(reply);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Value.Status);
    }

    [Theory]
    [InlineData("```\n[[ASPECT_VERDICT: status=pass; summary=fenced reply]]\n```")]
    [InlineData("```text\n[[ASPECT_VERDICT: status=pass; summary=fenced text]]\n```")]
    public void ParseVerdict_Recognises_Sentinel_Inside_Code_Fence(string reply)
    {
        // F1: models sometimes wrap the sentinel in triple-backtick
        // fences for visual emphasis. Strip the fence wrapper before
        // matching so this looks like a real verdict, not unparseable.
        var parsed = AspectVerdictParsing.ParseVerdict(reply);
        Assert.NotNull(parsed);
        Assert.Equal(AspectStatus.Pass, parsed!.Value.Status);
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
    public async Task RequirementFitPrompt_UsesFallbackEvidence_WhenTaskBodyIsEmpty()
    {
        string? capturedPrompt = null;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        runner.CliRunner = (_, _, _, prompt, _, _) =>
        {
            capturedPrompt = prompt;
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=fallback evidence is assessable.]]\n[[TASK_DONE]]");
        };

        var inputs = new AspectRunInputs(
            Project: "demo",
            JobId: "empty-prompt-job",
            JobTitle: "Convert legacy token aggregators to bus-backed shims",
            JobFolderPath: _jobFolder,
            TaskBody: "",
            RecentLog: "Agent reported the token aggregator shim conversion complete.",
            DiffSummary: "AdHocUsageService.cs, TokenSummary.cs, WorkspaceTokensTimelineService.cs changed.",
            StatusSummary: "Converted legacy token aggregator services to bus-backed shims.");

        var report = await runner.RunAsync(inputs,
            new[] { "requirement-fit" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Pass, report.Overall);
        Assert.NotNull(capturedPrompt);
        Assert.Contains("If `prompt.md` is empty", capturedPrompt!);
        Assert.Contains("Do not flag an empty `prompt.md`", capturedPrompt);
        Assert.Contains("Convert legacy token aggregators to bus-backed shims", capturedPrompt);
        Assert.DoesNotContain("If the task body is empty or unclear, prefer `concerns` over `block`", capturedPrompt);
    }

    [Fact]
    public async Task PerAspectModel_RoutesEachAspectsCliCallToItsConfiguredModel()
    {
        // The load-bearing per-step-model-selection acceptance: when the
        // orchestrator hands RunAsync a modelForAspect resolver, each
        // aspect's CLI call must use the model that resolver returns, and
        // the recorded run-wide model must stay the fallback for the rest.
        var captured = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        runner.CliRunner = (aspectId, _, model, _, _, _) =>
        {
            captured[aspectId] = model;
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");
        };

        // code-quality pinned to Haiku; every other aspect uses the
        // run-wide default the resolver echoes back unchanged.
        const string runWide = "claude-opus-4-1";
        Func<string, string> modelForAspect = id =>
            id == "code-quality" ? "claude-haiku-4-5" : runWide;

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", runWide, TimeSpan.FromSeconds(5), CancellationToken.None, modelForAspect);

        Assert.Equal(AspectStatus.Pass, report.Overall);
        Assert.Equal("claude-haiku-4-5", captured["code-quality"]);
        Assert.Equal(runWide, captured["requirement-fit"]);
        Assert.Equal(runWide, captured["documentation-impact"]);
        Assert.Equal(runWide, captured["tests-and-evidence"]);
    }

    [Fact]
    public async Task NullModelForAspect_KeepsRunWideModelForEveryAspect()
    {
        var captured = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        runner.CliRunner = (aspectId, _, model, _, _, _) =>
        {
            captured[aspectId] = model;
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");
        };

        await runner.RunAsync(BuildInputs(),
            new[] { "code-quality", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None, modelForAspect: null);

        Assert.Equal("claude-haiku-4-5", captured["code-quality"]);
        Assert.Equal("claude-haiku-4-5", captured["tests-and-evidence"]);
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

    [Fact]
    public void SerializeFindings_EmitsCamelCaseTokenisedArray_ForTheFrontend()
    {
        var verdicts = new[]
        {
            new AspectVerdict("requirement-fit", AspectStatus.Concerns, "missing edge-case test", "body", "fit:concerns"),
            new AspectVerdict("code-quality", AspectStatus.Block, "helper duplicated", "body", "quality:block"),
        };

        var json = AspectVerdictParsing.SerializeFindings(verdicts);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        // camelCase property names + normalised status token (not the enum name).
        Assert.Equal("requirement-fit", items[0].GetProperty("aspect").GetString());
        Assert.Equal("concerns", items[0].GetProperty("verdict").GetString());
        Assert.Equal("missing edge-case test", items[0].GetProperty("reason").GetString());
        Assert.Equal("code-quality", items[1].GetProperty("aspect").GetString());
        Assert.Equal("block", items[1].GetProperty("verdict").GetString());
    }

    [Fact]
    public void SerializeFindings_EmptyInput_YieldsEmptyArray()
    {
        Assert.Equal("[]", AspectVerdictParsing.SerializeFindings(Array.Empty<AspectVerdict>()));
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
