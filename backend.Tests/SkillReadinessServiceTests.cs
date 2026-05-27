using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pins the contract for the skill readiness check + fix-task wiring
/// (docs/skills-architecture.md "First Product Step"). The check is
/// naive in v1: stable heading detection + required phrases. These
/// tests are the regression boundary so a future tightening of the
/// parser cannot silently change pass / warn / fail verdicts that the
/// frontend modal renders straight to the user, and they pin the
/// invariant that the fix path queues a normal task in <c>2-ready</c>
/// rather than auto-editing the watched project.
/// </summary>
public class SkillReadinessServiceTests : IDisposable
{
    private readonly string _watchPath;
    private readonly string _projectName = "skill-test";

    public SkillReadinessServiceTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "rdo-skill-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in JobStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    // ---- Section extractor (pure) -------------------------------------------

    [Fact]
    public void ExtractSection_NoHeading_ReturnsNull()
    {
        var (heading, body) = SkillReadinessService.ExtractSection("# README\n\nNo skill section here.\n");
        Assert.Null(heading);
        Assert.Null(body);
    }

    [Fact]
    public void ExtractSection_FindsHeadingAndBody_StopsAtNextSameLevelHeading()
    {
        var md = "# README\n\n" +
                 "## Agent Software Studio Skills\n\n" +
                 "Standard skills: `.agents/skills/foo/SKILL.md`\n\n" +
                 "## Next section\n\nUnrelated.\n";
        var (heading, body) = SkillReadinessService.ExtractSection(md);
        Assert.NotNull(heading);
        Assert.Contains("Agent Software Studio Skills", heading);
        Assert.NotNull(body);
        Assert.Contains(".agents/skills/foo", body);
        Assert.DoesNotContain("Unrelated", body);
    }

    [Fact]
    public void ExtractSection_IsCaseInsensitive_AndAcceptsH3()
    {
        var md = "### Portable Skills\n\nbody\n";
        var (heading, _) = SkillReadinessService.ExtractSection(md);
        Assert.NotNull(heading);
        Assert.Contains("Portable Skills", heading);
    }

    // ---- Verdict matrix -----------------------------------------------------

    [Fact]
    public void CheckProject_UnknownProject_ReturnsNull()
    {
        var svc = BuildService();
        Assert.Null(svc.CheckProject("does-not-exist"));
    }

    [Fact]
    public void CheckProject_NoReadme_Fails()
    {
        var svc = BuildService();
        var report = svc.CheckProject(_projectName);

        Assert.NotNull(report);
        Assert.Equal(SkillReadinessStatus.Fail, report!.Status);
        Assert.Null(report.MatchedFile);
        Assert.True(report.MissingPhrases.Count >= 1);
        // Every checked file is reported, even when absent, so the UI can
        // explain *what* it looked at.
        Assert.Contains(report.CheckedFiles, f => f.RelPath == "README.md");
    }

    [Fact]
    public void CheckProject_ReadmeWithoutHeading_Fails()
    {
        WriteRepoFile("README.md", "# Project\n\nJust a description, no skill section.\n");
        var svc = BuildService();
        var report = svc.CheckProject(_projectName);

        Assert.NotNull(report);
        Assert.Equal(SkillReadinessStatus.Fail, report!.Status);
        Assert.Null(report.MatchedFile);
    }

    [Fact]
    public void CheckProject_HeadingButMissingPhrases_WarnsAndListsMissing()
    {
        // Heading is present but body says nothing concrete - this is
        // exactly the "stale" case the docs call out: someone added the
        // heading once and never filled it in.
        var md = "# Project\n\n## Agent Software Studio Skills\n\nTBD.\n";
        WriteRepoFile("README.md", md);

        var svc = BuildService();
        var report = svc.CheckProject(_projectName);

        Assert.NotNull(report);
        Assert.Equal(SkillReadinessStatus.Warning, report!.Status);
        Assert.Equal("README.md", report.MatchedFile);
        Assert.NotEmpty(report.MissingPhrases);
        // All four labels missing on this empty section.
        Assert.Contains("standardSkills", report.MissingPhrases);
        Assert.Contains("projectSkills", report.MissingPhrases);
        Assert.Contains("skillsPath", report.MissingPhrases);
        Assert.Contains("processorReference", report.MissingPhrases);
    }

    [Fact]
    public void CheckProject_FullSection_Passes()
    {
        var md = "# Project\n\n" +
                 "## Agent Software Studio Skills\n\n" +
                 "This project is managed by Agent Software Studio.\n\n" +
                 "- Standard skills: `<task-processor-root>/.agents/skills/runtime-log-analysis/SKILL.md`\n" +
                 "- Project skills: `<task-processor-root>/.agents/projects/skill-test/skills/foo/SKILL.md`\n";
        WriteRepoFile("README.md", md);

        var svc = BuildService();
        var report = svc.CheckProject(_projectName);

        Assert.NotNull(report);
        Assert.Equal(SkillReadinessStatus.Pass, report!.Status);
        Assert.Equal("README.md", report.MatchedFile);
        Assert.Empty(report.MissingPhrases);
        Assert.Equal(4, report.MatchedPhrases.Count);
    }

    [Fact]
    public void CheckProject_FallsBackToAgentsMd_WhenReadmeHasNoHeading()
    {
        WriteRepoFile("README.md", "# Project\n\nNo skills here.\n");
        WriteRepoFile("AGENTS.md", "## Skills lookup\n\n" +
                                   "Standard skills under `.agents/skills/`. Project skills under `.agents/projects/`. " +
                                   "Managed by the task processor.\n");

        var svc = BuildService();
        var report = svc.CheckProject(_projectName);

        Assert.NotNull(report);
        Assert.Equal("AGENTS.md", report!.MatchedFile);
        Assert.Equal(SkillReadinessStatus.Pass, report.Status);
    }

    // ---- Fix-task payload ---------------------------------------------------

    [Fact]
    public void PreviewFixTask_OnFail_ProducesReadyTaskWithExpectedShape()
    {
        var svc = BuildService();
        var preview = svc.PreviewFixTask(_projectName);

        Assert.NotNull(preview);
        Assert.Equal(_watchPath, preview!.WatchPath);
        Assert.Equal(JobStates.Ready, preview.TargetState);
        Assert.Equal(TaskTypes.Chore, preview.TaskType);
        Assert.Contains("Add", preview.Title);
        // Prompt should embed the canonical lookup snippet so the agent
        // knows the exact heading and phrases the validator looks for.
        Assert.Contains("## Agent Software Studio Skills", preview.PromptMarkdown);
        Assert.Contains(".agents/skills/", preview.PromptMarkdown);
        Assert.Contains("[[TASK_DONE]]", preview.PromptMarkdown);
        // Report is embedded so the user can see *why* this task is being proposed.
        Assert.Equal(SkillReadinessStatus.Fail, preview.Report.Status);
    }

    [Fact]
    public void PreviewFixTask_OnWarning_TitleSaysUpdate()
    {
        var md = "# Project\n\n## Agent Software Studio Skills\n\nTBD.\n";
        WriteRepoFile("README.md", md);

        var svc = BuildService();
        var preview = svc.PreviewFixTask(_projectName);

        Assert.NotNull(preview);
        Assert.Contains("Update", preview!.Title);
    }

    [Fact]
    public void CreateFixTask_QueuesNewJobIn2Ready()
    {
        var svc = BuildService();
        var result = svc.CreateFixTask(_projectName, ownerClientId: null);

        Assert.NotNull(result);
        Assert.Equal(JobStates.Ready, result!.TargetState);
        Assert.False(string.IsNullOrEmpty(result.JobId));

        // Folder + prompt landed on disk.
        var jobDir = Path.Combine(_watchPath, JobStates.Ready, result.JobId);
        Assert.True(Directory.Exists(jobDir));
        Assert.True(File.Exists(Path.Combine(jobDir, "job.json")));
        var prompt = File.ReadAllText(Path.Combine(jobDir, "prompt.md"));
        Assert.Contains("Agent Software Studio Skills", prompt);

        // Watched project's README is *not* edited by the fix path - the
        // agent updates it through the queue, exactly like every other
        // task. This is the load-bearing invariant in the prompt.
        Assert.False(File.Exists(Path.Combine(_watchPath, "README.md")));
    }

    // ---- Catalog ------------------------------------------------------------

    [Fact]
    public void GetCatalog_ReturnsStandardAndProjectSpecificSeparately()
    {
        var svc = BuildService();
        var catalog = svc.GetCatalog(_projectName);

        Assert.Equal(_projectName, catalog.ProjectName);
        // We can't assume the .agents/ tree is reachable from the test's
        // BaseDirectory in every CI shape, so we only assert the lists
        // exist and are split. When the tree is reachable, the standard
        // category should at least carry the runtime-log-analysis skill
        // that ships in the repo today.
        Assert.NotNull(catalog.Standard);
        Assert.NotNull(catalog.ProjectSpecific);
        Assert.All(catalog.Standard, s => Assert.Equal(SkillCategory.Standard, s.Category));
        Assert.All(catalog.ProjectSpecific, s => Assert.Equal(SkillCategory.ProjectSpecific, s.Category));
    }

    // ---- helpers ------------------------------------------------------------

    private void WriteRepoFile(string relPath, string content)
    {
        var full = Path.Combine(_watchPath, relPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
    }

    private SkillReadinessService BuildService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = _projectName,
                ["WatchPaths:0:Path"] = _watchPath,
                // RootPath defaults to Path; the service uses
                // RepositoryPath || RootPath || Path so leaving these blank
                // is fine - it falls back to _watchPath.
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var mutations = new JobMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), NullLogger<JobMutationService>.Instance);
        return new SkillReadinessService(scanner, mutations, NullLogger<SkillReadinessService>.Instance);
    }
}
