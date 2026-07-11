using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// EW-2 live collector probe. Unlike <see cref="WorkstreamCollectorPostStepTests"/>
/// (which unit-tests the static <c>Apply</c> writer in isolation), this exercises the
/// whole <see cref="WorkstreamCollectorPostStepRunner.RunAsync"/> path end to end:
/// frame seeding, prompt render from the shipped <c>workstream-collector.md</c> template,
/// model-reply parse, the mandatory Workstream-Log guarantee, and the bounded write.
///
/// The only substitution is the paid/non-deterministic model call, replaced by a
/// fixed reply via <see cref="WorkstreamCollectorPostStepRunner.OneShotOverride"/>.
/// The collector's sole filesystem authority is <c>ctx.Project.RootPath</c>, so pointing
/// that at a throwaway root proves the collector runs live without any chance of
/// mutating real project documentation.
///
/// Set <c>WORKSTREAM_PROBE_OUT</c> to a directory to persist the generated tree plus a
/// PROBE-REPORT.md there as run evidence; otherwise the probe uses a temp root and cleans
/// up. This mirrors the frontend's <c>PROJECT_WIKI_RESULTS_DIR</c> evidence convention.
/// </summary>
public sealed class WorkstreamCollectorLiveProbeTests : IDisposable
{
    private readonly bool _evidenceMode;
    private readonly string _root;

    public WorkstreamCollectorLiveProbeTests()
    {
        var outDir = Environment.GetEnvironmentVariable("WORKSTREAM_PROBE_OUT");
        _evidenceMode = !string.IsNullOrWhiteSpace(outDir);
        _root = Path.GetFullPath(_evidenceMode
            ? Path.Combine(outDir!.Trim(), "collector-live-probe")
            : Path.Combine(Path.GetTempPath(), "collector-live-probe-" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task RunAsync_ClassifiesSettledEvidence_IntoIsolatedThrowawayRoot()
    {
        var prompts = new RuntimePromptService(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PromptTemplates:RuntimePath"] = LocateShippedPromptsRuntime(),
                })
                .Build(),
            NullLogger<RuntimePromptService>.Instance);

        var runner = new WorkstreamCollectorPostStepRunner(prompts, NullLogger<WorkstreamCollectorPostStepRunner>.Instance);

        // Substitute only the model. The reply is production-shaped: it carries the
        // exact contract the runner parses (marker + fenced JSON) and proposes one item
        // per non-onboarding area, including an actionable development signal.
        string? renderedPrompt = null;
        runner.OneShotOverride = (request, _) =>
        {
            renderedPrompt = request.Prompt;
            return Task.FromResult(SuccessfulReply(ModelReply));
        };

        var ctx = new WorkstreamCollectorContext
        {
            Task = new TaskInfo
            {
                Id = "AGT-2015",
                Key = "AGT-2015",
                Title = "PULSE-2 warnings, in-progress live view, curator integration",
                ProjectName = "agent-studio",
                FolderPath = _root,
            },
            Project = new WatchPathEntry { Name = "agent-studio", RootPath = _root, Path = _root },
            TaskBody = "Surface development signals with a human action, dead internal links, and areas over page budget.",
            StatusSummary = "Result: Success. Added the warnings tile, live in-progress runs, and the collector/curator status.",
            DiffSummary = "backend/Features/Docs/ProjectDocsService.cs | frontend wiki-pulse component | prompts/runtime/workstream-collector.md",
            ReviewSummary = "Reissue completion-gate flagged E2E combined-run flakiness and a missing live collector probe.",
            Model = "claude-sonnet-5",
            FrameLanguage = EngineeringWorkstreamFrameLanguage.English,
        };

        var result = await runner.RunAsync(ctx, CancellationToken.None);

        // The full runner path ran and applied a bounded set of writes.
        Assert.Equal(WorkstreamCollectorVerdict.Updated, result.Verdict);
        Assert.Equal(4, result.Writes);
        Assert.Equal(0, result.Rejected);
        Assert.Equal("claude-sonnet-5", result.Model);

        // The shipped template rendered with every slot filled (no leftover {{...}})
        // and the fixed frame + task identity present.
        Assert.NotNull(renderedPrompt);
        Assert.DoesNotContain("{{", renderedPrompt);
        Assert.Contains("50-workstream-log", renderedPrompt);
        Assert.Contains("AGT-2015", renderedPrompt);

        var frame = Path.Combine(_root, "docs", "engineering-workstream");

        // Development signal carries the PULSE-2 actionable frontmatter (status + human-action).
        var signal = Path.Combine(frame, "20-development-signals", "reissue-evidence-wipe.md");
        Assert.True(File.Exists(signal));
        var signalText = File.ReadAllText(signal);
        Assert.Contains("status: active", signalText);
        Assert.Contains("human-action: \"Confirm results/ is preserved across a reissue before re-dispatching.\"", signalText);

        // System knowledge nests two deep with provenance; decision + chronological log land too.
        Assert.True(File.Exists(Path.Combine(frame, "30-system-knowledge", "pipeline", "collector-write-contract.md")));
        Assert.True(File.Exists(Path.Combine(frame, "40-decision-log", "preserve-evidence-before-cleanup.md")));
        var log = File.ReadAllText(Path.Combine(frame, "50-workstream-log", "workstream-log.md"));
        Assert.Contains("Source: AGT-2015", log);

        // Isolation: the collector only ever writes under ctx.Project.RootPath. Every file
        // it produced lives beneath the throwaway root, so no real documentation was reachable.
        var generated = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(_root, p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.All(
            Directory.EnumerateFiles(Path.Combine(_root, "docs"), "*.md", SearchOption.AllDirectories),
            path => Assert.StartsWith(_root, Path.GetFullPath(path), StringComparison.Ordinal));

        if (_evidenceMode) WriteEvidenceReport(result, generated, signalText);
    }

    /// <summary>Walks up from the test output dir to the repo's shipped runtime prompts.</summary>
    private static string LocateShippedPromptsRuntime()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "prompts", "runtime", "workstream-collector.md");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
        }
        throw new FileNotFoundException(
            $"Could not locate prompts/runtime above {AppContext.BaseDirectory}.");
    }

    private static CliOneShotResult SuccessfulReply(string reply) => new(
        Ok: true,
        ExitCode: 0,
        Stdout: reply,
        Stderr: string.Empty,
        Duration: TimeSpan.FromMilliseconds(1200),
        ParsedText: reply,
        Usage: null,
        RichUsage: null,
        Latency: new AgentMessageLatency(),
        Error: null);

    private const string ModelReply = """
        Here is the settled classification for the completed task.

        <!-- WORKSTREAM_COLLECTOR_JSON -->
        ```json
        {
          "items": [
            {
              "area": "20-development-signals",
              "identity": "reissue-evidence-wipe",
              "title": "Reissue can wipe prior deliverables",
              "content": "A reissue restarted the run; without an explicit preservation boundary the previous results were at risk before evidence was retained.",
              "status": "active",
              "humanAction": "Confirm results/ is preserved across a reissue before re-dispatching.",
              "frequency": "1"
            },
            {
              "area": "30-system-knowledge",
              "identity": "pipeline/collector-write-contract",
              "title": "EW-2 collector write contract",
              "content": "The collector proposes data only. The server owns every path and write and enforces the anti-overgrowth budget across the fixed five-area frame."
            },
            {
              "area": "40-decision-log",
              "identity": "preserve-evidence-before-cleanup",
              "title": "Preserve evidence before cleanup",
              "content": "Cleanup may replace disposable execution state only after durable evidence (results, logs, latest outcome) has been retained."
            },
            {
              "area": "50-workstream-log",
              "identity": "task-outcome",
              "title": "PULSE-2 live collector probe",
              "content": "Exercised the EW-2 collector end to end against an isolated throwaway project root, with only the model reply stubbed."
            }
          ]
        }
        ```
        """;

    private void WriteEvidenceReport(WorkstreamCollectorResult result, IReadOnlyList<string> generated, string signalText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# EW-2 live collector probe — report");
        sb.AppendLine();
        sb.AppendLine("Ran `WorkstreamCollectorPostStepRunner.RunAsync` end to end (frame seeding, template");
        sb.AppendLine("render, model-reply parse, mandatory Workstream-Log guarantee, bounded write). The only");
        sb.AppendLine("substitution is the model call, stubbed via `OneShotOverride`.");
        sb.AppendLine();
        sb.AppendLine("## Isolation");
        sb.AppendLine();
        sb.AppendLine($"- Throwaway project root: `{_root}`");
        sb.AppendLine("- The collector's only filesystem authority is `ctx.Project.RootPath`; pointing it at this");
        sb.AppendLine("  root means no real project documentation can be reached or mutated.");
        sb.AppendLine();
        sb.AppendLine("## Outcome");
        sb.AppendLine();
        sb.AppendLine($"- Verdict: `{result.Verdict}`");
        sb.AppendLine($"- Reason: {result.Reason}");
        sb.AppendLine($"- Writes: {result.Writes}, Rejected: {result.Rejected}, Model: `{result.Model}`");
        sb.AppendLine();
        sb.AppendLine("## Generated tree");
        sb.AppendLine();
        sb.AppendLine("```");
        foreach (var rel in generated) sb.AppendLine(rel.Replace('\\', '/'));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Development-signal frontmatter (actionable, PULSE-2 source)");
        sb.AppendLine();
        sb.AppendLine("```");
        var frontmatterEnd = signalText.IndexOf("\n---", 4, StringComparison.Ordinal);
        sb.AppendLine(frontmatterEnd > 0 ? signalText[..(frontmatterEnd + 4)].Trim() : signalText.Trim());
        sb.AppendLine("```");
        File.WriteAllText(Path.Combine(_root, "PROBE-REPORT.md"), sb.ToString(), new UTF8Encoding(false));
    }

    public void Dispose()
    {
        if (_evidenceMode) return; // keep the evidence tree for inspection
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}
