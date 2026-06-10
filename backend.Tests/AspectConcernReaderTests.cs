

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Tests for <see cref="AspectConcernReader"/>: the read-time enrichment
/// that lifts the per-aspect concern summary out of <c>aspect-{id}.md</c>
/// onto the matching pipeline step so the Overview pipeline can tooltip the
/// CONCERNS pill.
/// </summary>
public class AspectConcernReaderTests
{
    private static string NewTempJobFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atp-aspect-concern-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteAspect(string folder, string aspect, AspectStatus status, string summary, string? tag)
        => File.WriteAllText(
            Path.Combine(folder, $"aspect-{aspect}.md"),
            AspectVerdictParsing.RenderReport(
                new AspectVerdict(aspect, status, summary, "## Model reply\n\n```\n...\n```\n", tag),
                DateTime.UtcNow));

    [Fact]
    public void Enrich_AttachesConcernSummary_ForConcernAspectStep_NotForPass()
    {
        var folder = NewTempJobFolder();
        try
        {
            WriteAspect(folder, "requirement-fit", AspectStatus.Concerns,
                "Acceptance item 3 (empty-state) has no evidence.", "requirement:concerns");
            WriteAspect(folder, "code-quality", AspectStatus.Pass, "Diff is clean.", null);

            var record = new PipelineExecutionRecord
            {
                Steps =
                {
                    new PipelineStepExecution { StepId = "aspect-requirement-fit", Kind = StepKind.Aspect, Verdict = "concerns" },
                    new PipelineStepExecution { StepId = "aspect-code-quality", Kind = StepKind.Aspect, Verdict = "pass" },
                    new PipelineStepExecution { StepId = "core-agent-run", Kind = StepKind.Core },
                },
            };

            var enriched = AspectConcernReader.Enrich(record, folder)!;

            var rf = enriched.Steps.Single(s => s.StepId == "aspect-requirement-fit");
            Assert.Equal("Acceptance item 3 (empty-state) has no evidence.", rf.VerdictSummary);

            // A pass verdict must never grow a (misleading) tooltip.
            var cq = enriched.Steps.Single(s => s.StepId == "aspect-code-quality");
            Assert.Null(cq.VerdictSummary);

            // Non-aspect steps are untouched.
            var core = enriched.Steps.Single(s => s.StepId == "core-agent-run");
            Assert.Null(core.VerdictSummary);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Enrich_BlockVerdict_AlsoCarriesSummary()
    {
        var folder = NewTempJobFolder();
        try
        {
            WriteAspect(folder, "tests-and-evidence", AspectStatus.Block,
                "No failing-then-passing test for the new branch.", "quality:concerns");

            var record = new PipelineExecutionRecord
            {
                Steps = { new PipelineStepExecution { StepId = "aspect-tests-and-evidence", Kind = StepKind.Aspect, Verdict = "block" } },
            };

            var enriched = AspectConcernReader.Enrich(record, folder)!;
            Assert.Equal("No failing-then-passing test for the new branch.", enriched.Steps[0].VerdictSummary);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Enrich_MissingAspectFile_LeavesSummaryNull()
    {
        var folder = NewTempJobFolder();
        try
        {
            var record = new PipelineExecutionRecord
            {
                Steps = { new PipelineStepExecution { StepId = "aspect-requirement-fit", Kind = StepKind.Aspect, Verdict = "concerns" } },
            };
            var enriched = AspectConcernReader.Enrich(record, folder)!;
            Assert.Null(enriched.Steps[0].VerdictSummary);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Enrich_NullOrEmptyRecord_IsSafe()
    {
        Assert.Null(AspectConcernReader.Enrich(null, "/nonexistent"));
        var empty = new PipelineExecutionRecord();
        Assert.Same(empty, AspectConcernReader.Enrich(empty, null));
    }
}
