using OrchestratorApi.Models;
using OrchestratorApi.Services.Markdown;

namespace OrchestratorApi.Services.Pipeline;

/// <summary>
/// Read-time enrichment that lifts each aspect step's concern summary out
/// of its <c>aspect-{id}.md</c> frontmatter and attaches it to the matching
/// <see cref="PipelineStepExecution.VerdictSummary"/>. The Overview pipeline
/// renders that text as the tooltip on the CONCERNS pill so the operator
/// reads the concrete concern, not just the badge.
///
/// <para>
/// Pure read: the persisted <c>pipeline-execution.json</c> is never
/// rewritten. Only aspect steps whose recorded verdict is a concern
/// (<c>concerns</c> / <c>block</c>) are touched, so a pass verdict never
/// grows a misleading tooltip and the disk I/O stays bounded to the few
/// aspect reports that actually flagged something.
/// </para>
/// </summary>
public static class AspectConcernReader
{
    private static readonly HashSet<string> ConcernVerdicts =
        new(StringComparer.OrdinalIgnoreCase) { "concerns", "concern", "block", "blocked" };

    /// <summary>
    /// Return <paramref name="record"/> with every concern-flagged aspect
    /// step carrying its <see cref="PipelineStepExecution.VerdictSummary"/>.
    /// Returns the input unchanged when there is nothing to enrich (no
    /// record, no folder, or no aspect step with a concern verdict + a
    /// readable summary).
    /// </summary>
    public static PipelineExecutionRecord? Enrich(PipelineExecutionRecord? record, string? jobFolderPath)
    {
        if (record == null || record.Steps.Count == 0) return record;
        if (string.IsNullOrWhiteSpace(jobFolderPath) || !Directory.Exists(jobFolderPath)) return record;

        List<PipelineStepExecution>? rebuilt = null;
        for (var i = 0; i < record.Steps.Count; i++)
        {
            var step = record.Steps[i];
            var summary = TryReadConcernSummary(step, jobFolderPath!);
            if (summary == null) continue;
            rebuilt ??= new List<PipelineStepExecution>(record.Steps);
            rebuilt[i] = step with { VerdictSummary = summary };
        }
        return rebuilt == null ? record : record with { Steps = rebuilt };
    }

    private static string? TryReadConcernSummary(PipelineStepExecution step, string jobFolderPath)
    {
        if (step.Kind != StepKind.Aspect) return null;
        if (string.IsNullOrWhiteSpace(step.Verdict) || !ConcernVerdicts.Contains(step.Verdict!)) return null;

        // The aspect step id IS the report file stem (e.g. step
        // "aspect-requirement-fit" -> "aspect-requirement-fit.md").
        var path = Path.Combine(jobFolderPath, step.StepId + ".md");
        if (!File.Exists(path)) return null;

        string text;
        try { text = File.ReadAllText(path); }
        catch { return null; }

        var fm = FrontmatterParser.Parse(text);
        if (!fm.Ok) return null;
        if (fm.Fields.TryGetValue("summary", out var summary) && !string.IsNullOrWhiteSpace(summary))
        {
            return summary.Trim();
        }
        return null;
    }
}
