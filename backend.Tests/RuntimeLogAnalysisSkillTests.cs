using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the structure of the <c>runtime-log-analysis</c> portable skill at
/// <c>.agents/skills/runtime-log-analysis/</c>: the SKILL.md frontmatter,
/// the stable section list in the per-report contract, the helper script's
/// output shape against a fixture, and the round-trip of the sample report
/// through <see cref="AnalysisReportStore"/>.
///
/// These are file-structure tests, not behavioural tests of a service: the
/// skill is a Markdown contract that an agent reads, plus one helper script.
/// We test what we can verify on disk so a future refactor cannot silently
/// break the report contract or the report's place in the analysis-report
/// surface.
/// </summary>
public class RuntimeLogAnalysisSkillTests : IDisposable
{
    private readonly string _workspace;

    public RuntimeLogAnalysisSkillTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(),
            "runtime-log-analysis-skill-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string FindRepoRoot([CallerFilePath] string sourceFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (dir != null)
        {
            // The repo root is the directory that contains both AGENTS.md and the .agents/ skill folder.
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, ".agents")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Repo root with .agents/ + AGENTS.md not found by walking up from {sourceFile}.");
    }

    private static string SkillRoot()
        => Path.Combine(FindRepoRoot(), ".agents", "skills", "runtime-log-analysis");

    [Fact]
    public void SkillFile_HasFrontmatterSentinelAndRequiredSections()
    {
        var path = Path.Combine(SkillRoot(), "SKILL.md");
        Assert.True(File.Exists(path), $"Missing SKILL.md at {path}");
        var text = File.ReadAllText(path);

        // YAML frontmatter must be the first thing in the file.
        var fm = Regex.Match(text, @"\A---\r?\n(?<body>.*?)\r?\n---\r?\n",
            RegexOptions.Singleline);
        Assert.True(fm.Success, "SKILL.md is missing the YAML frontmatter block");

        var body = fm.Groups["body"].Value;
        Assert.Matches(@"(?m)^name:\s*runtime-log-analysis\s*$", body);
        Assert.Matches(@"(?m)^description:\s*\S.+$", body);
        Assert.Matches(@"(?m)^trigger:\s*user\s*$", body);
        Assert.Matches(@"(?m)^mutates_code:\s*false\s*$", body);
        Assert.Matches(@"(?m)^mutates_queue:\s*false\s*$", body);

        var sentinel = Regex.Match(body,
            @"(?m)^sentinel:\s*(?<value>TASKBOARD-SKILL-RUNTIME-LOG-ANALYSIS)\s*$");
        Assert.True(sentinel.Success,
            "SKILL.md is missing 'sentinel: TASKBOARD-SKILL-RUNTIME-LOG-ANALYSIS' in its frontmatter");

        // Sentinel must also appear in the body so a CLI that ignores YAML can find it.
        Assert.Contains(sentinel.Groups["value"].Value, text);

        // Required sections (regex-stable so downstream tooling can extract them).
        string[] requiredSkillSections =
        [
            "^## When to invoke$",
            "^## Hard constraints$",
            "^## Inputs$",
            "^## Findings$",
            "^## Output$",
            "^## Process$",
            "^## Parse-failure behaviour$",
            "^## Helper script$",
            "^## Anti-patterns$",
        ];
        foreach (var pattern in requiredSkillSections)
        {
            Assert.True(Regex.IsMatch(text, pattern, RegexOptions.Multiline),
                $"SKILL.md is missing required section heading: {pattern}");
        }

        // Findings table must list the six required categories.
        string[] requiredFindingTopics =
        [
            "repeated-error",
            "slow-operation",
            "noisy-event",
            "missing-correlation-id",
            "suspicious-sequence",
            "tests-passed-with-runtime-errors",
        ];
        foreach (var topic in requiredFindingTopics)
        {
            Assert.Contains(topic, text);
        }
    }

    [Fact]
    public void ReportContract_HasStableSectionHeadings()
    {
        var path = Path.Combine(SkillRoot(), "references", "report-contract.md");
        Assert.True(File.Exists(path), $"Missing report-contract.md at {path}");
        var text = File.ReadAllText(path);

        // The section regexes named by the contract must all appear verbatim in
        // the contract document itself; if anyone edits them they must update
        // this list deliberately.
        string[] expectedHeadings =
        [
            "^## Repeated errors$",
            "^## Slow operations$",
            "^## Noisy events$",
            "^## Missing correlation ids$",
            "^## Suspicious sequences$",
            "^## Tests-passed-with-runtime-errors$",
            "^## Notes$",
            "^## Evidence$",
            "^## Follow-up suggestions$",
        ];
        foreach (var heading in expectedHeadings)
        {
            // The contract document quotes the regex pattern on a code-line,
            // so we accept either a literal heading occurrence or the exact
            // regex string.
            var literal = heading.TrimStart('^').TrimEnd('$');
            Assert.True(
                Regex.IsMatch(text, heading, RegexOptions.Multiline) ||
                text.Contains(literal),
                $"report-contract.md is missing stable heading marker: {heading}");
        }
    }

    [Fact]
    public void HelperScriptAndFixtures_ExistOnDisk()
    {
        var root = SkillRoot();
        Assert.True(File.Exists(Path.Combine(root, "scripts", "aggregate-runtime-events.mjs")));
        Assert.True(File.Exists(Path.Combine(root, "scripts", "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "tests", "fixtures", "sample-runtime.jsonl")));
        Assert.True(File.Exists(Path.Combine(root, "tests", "fixtures", "mixed-runtime.jsonl")));
        Assert.True(File.Exists(Path.Combine(root, "references", "fixtures", "sample-report.md")));
        Assert.True(File.Exists(Path.Combine(root, "references", "fixtures", "sample-report.json")));
    }

    [Xunit.SkippableFact]
    public void Aggregator_OnSampleJsonl_ProducesExpectedFindingShape()
    {
        Skip.IfNot(NodeAvailable(out var node), "node is not on PATH; skipping aggregator script test.");

        var script = Path.Combine(SkillRoot(), "scripts", "aggregate-runtime-events.mjs");
        var fixture = Path.Combine(SkillRoot(), "tests", "fixtures", "sample-runtime.jsonl");
        var (exit, stdout, stderr) = RunNode(node!, script, fixture);
        Assert.True(exit == 0, $"aggregator exited {exit}; stderr=\n{stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        // Top-level shape locked by the report-contract.md document.
        foreach (var key in new[]
        {
            "schemaVersion",
            "inputCount",
            "fileCount",
            "window",
            "repeatedErrors",
            "slowOperations",
            "noisyEvents",
            "missingCorrelationIds",
            "suspiciousSequences",
            "parseWarnings",
        })
        {
            Assert.True(root.TryGetProperty(key, out _), $"aggregate is missing top-level key '{key}'");
        }

        // The fixture has three http.request.failed events grouped together.
        var repeated = root.GetProperty("repeatedErrors");
        Assert.True(repeated.GetArrayLength() >= 1);
        var firstGroup = repeated[0];
        Assert.Equal("http.request.failed", firstGroup.GetProperty("event").GetString());
        Assert.Equal(3, firstGroup.GetProperty("count").GetInt32());

        // queue.tick must be flagged as noisy (9/17 = 53%).
        var noisy = root.GetProperty("noisyEvents");
        Assert.Contains(noisy.EnumerateArray(),
            e => e.GetProperty("event").GetString() == "queue.tick");

        // The order.shipped without order.placed pair triggers a suspicious-sequence violation.
        var seq = root.GetProperty("suspiciousSequences");
        var violations = seq.GetProperty("violations");
        Assert.True(violations.GetArrayLength() >= 1);
        Assert.Equal("order.placed -> order.shipped",
            violations[0].GetProperty("invariant").GetString());
    }

    [Xunit.SkippableFact]
    public void Aggregator_OnMixedJsonl_PreservesParseWarnings()
    {
        Skip.IfNot(NodeAvailable(out var node), "node is not on PATH; skipping aggregator script test.");

        var script = Path.Combine(SkillRoot(), "scripts", "aggregate-runtime-events.mjs");
        var fixture = Path.Combine(SkillRoot(), "tests", "fixtures", "mixed-runtime.jsonl");
        var (exit, stdout, stderr) = RunNode(node!, script, fixture);
        Assert.True(exit == 0, $"aggregator exited {exit}; stderr=\n{stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var warnings = doc.RootElement.GetProperty("parseWarnings");
        // The mixed fixture has two malformed lines ("not really json" and "{nope").
        Assert.True(warnings.GetArrayLength() >= 2,
            $"expected at least 2 parse warnings; got {warnings.GetArrayLength()}");

        // Good lines still parsed: three valid runtime events.
        Assert.Equal(3, doc.RootElement.GetProperty("inputCount").GetInt32());
    }

    [Fact]
    public async Task SampleReport_ValidatesAndRoundTripsThroughAnalysisReportStore()
    {
        // The sample-report.json fixture in the skill describes a typical
        // Structured runtime-log-analysis report. We deserialise it into the
        // backend's AnalysisReport record, store it via AnalysisReportStore,
        // and read it back from a fresh store. This locks two things at once:
        //   1) The fixture stays valid against the analysis-report contract.
        //   2) The runtime-log-analysis report shape is not subtly diverging
        //      from the generic AnalysisReport schema.
        var path = Path.Combine(SkillRoot(), "references", "fixtures", "sample-report.json");
        var json = await File.ReadAllTextAsync(path);
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var report = JsonSerializer.Deserialize<AnalysisReport>(json, opts);
        Assert.NotNull(report);

        // Topic and trigger are pinned by the skill's report contract.
        Assert.Equal("runtime-observability", report!.Topic);
        Assert.Equal(AnalysisReportTrigger.Manual, report.Trigger);
        Assert.Equal(AnalysisReportProducerKind.Manual, report.Producer.Kind);
        Assert.Equal(AnalysisReportParseStatus.Structured, report.ParseStatus);

        // Required finding categories must all appear in a Structured sample report.
        var topics = report.Findings?.Select(f => f.Topic).ToHashSet() ?? new HashSet<string>();
        Assert.Contains("repeated-error", topics);
        Assert.Contains("slow-operation", topics);
        Assert.Contains("noisy-event", topics);
        Assert.Contains("suspicious-sequence", topics);
        Assert.Contains("tests-passed-with-runtime-errors", topics);

        // Round-trip through the canonical analysis-report store.
        var store = new AnalysisReportStore();
        var markdownPath = Path.Combine(SkillRoot(), "references", "fixtures", "sample-report.md");
        var markdown = await File.ReadAllTextAsync(markdownPath);
        var version = await store.AppendAsync(_workspace, "agent-taskboard", report, markdown);
        Assert.Equal(1, version);

        var fresh = new AnalysisReportStore();
        var loaded = fresh.Snapshot(_workspace, "agent-taskboard");
        Assert.Single(loaded);
        Assert.Equal(report.ReportId, loaded[0].ReportId);
        Assert.Equal("runtime-observability", loaded[0].Topic);
        Assert.Equal(AnalysisReportSeverity.Critical, loaded[0].Severity);
    }

    private static bool NodeAvailable(out string? node)
    {
        // Resolve `node` (or `node.exe` on Windows) on PATH. Returning the
        // absolute path keeps the launched process unambiguous on Windows
        // where the shim resolution differs across PowerShell and Git Bash.
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        var exe = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate))
            {
                node = candidate;
                return true;
            }
        }
        node = null;
        return false;
    }

    private static (int exit, string stdout, string stderr) RunNode(string node, string script, string arg)
    {
        var psi = new ProcessStartInfo
        {
            FileName = node,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(15000);
        return (proc.ExitCode, stdout, stderr);
    }
}
