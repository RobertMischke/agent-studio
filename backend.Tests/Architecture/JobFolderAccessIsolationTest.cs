using System.Text.RegularExpressions;
using Xunit;

namespace OrchestratorApi.Tests.Architecture;

/// <summary>
/// Mechanically enforces the rule from ADR-0024 and the queued task
/// <c>task-access-api-layer-extraction</c>: only the small set of
/// services that own on-disk job state may construct lane folder paths
/// or perform structural directory mutations against the job-folder
/// tree (<c>&lt;watchPath&gt;/&lt;lane&gt;/&lt;slug&gt;/</c>).
///
/// <para>
/// Motivation: in 2026-05 a class of bugs (zombie folders, 409 on move,
/// state-field-out-of-lane) was traced back to direct filesystem
/// manipulation of job folders that bypassed <c>JobMutationService</c>
/// and <c>JobStateMachine</c>. Once <c>ITaskAccess</c> implements
/// phases 2-4, the whitelist below shrinks to <c>Services/TaskAccess/</c>
/// plus <c>Services/Jobs/</c>; everything else is forced through the
/// typed API. Today the whitelist is the migration ledger.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// The test scans source files, not compiled IL, on purpose: a textual
/// rule is the cheapest review surface and the diff-time grep stays
/// useful even when the offending code is in a generated partial.
/// </para>
/// <para>
/// Two layers of rule:
/// <list type="number">
/// <item><description>
/// <b>Lane-path construction.</b> <c>Path.Combine(.., JobStates.X, ..)</c>
/// or a hardcoded lane folder literal (e.g. <c>"3-progress"</c>) inside
/// a <c>Path.Combine</c> call means the caller is building a path into
/// the lane-folder structure. That is a job-folder-tree operation by
/// construction and must go through <c>ITaskAccess</c> (or the
/// grandfathered storage services).
/// </description></item>
/// <item><description>
/// <b>Structural directory mutations.</b> <c>Directory.Move</c> and
/// <c>Directory.Delete</c> are the calls that produce zombie folders
/// and 409 conflicts when used against the job-folder tree from
/// anywhere except <c>JobStateMachine</c>. Forbid them outright outside
/// the storage services; non-job-folder uses (writing to
/// <c>workspace/logs/</c>, dropping temp scratch) do not need
/// <c>Directory.Move</c> / <c>Directory.Delete</c> in practice.
/// </description></item>
/// </list>
/// </para>
/// </remarks>
public class JobFolderAccessIsolationTest
{
    /// <summary>
    /// Files that may construct lane folder paths or perform structural
    /// directory mutations against the job-folder tree. Anything not on
    /// this list goes through <c>ITaskAccess</c> (today: through
    /// <c>JobMutationService</c> / <c>JobStateMachine</c>; after phase 4
    /// of <c>task-access-api-layer-extraction</c>, through the typed
    /// API in <c>Services/TaskAccess/</c>).
    ///
    /// <para>
    /// Each entry carries the reason it is on the list. Entries marked
    /// "MIGRATION TARGET" are tolerated only until the corresponding
    /// consumer is rewritten against <c>ITaskAccess</c>; removing the
    /// entry is the green-light gate for the migration.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Whitelist =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Tier 1: the future single owner. Phase 1 ships only the
            // contract; phases 2-3 land the in-memory store and the
            // mutation API; phase 4 migrates every other consumer here.
            ["backend/Services/TaskAccess/ITaskAccess.cs"] =
                "TaskAccess contract surface.",
            ["backend/Services/TaskAccess/ITaskAccessHost.cs"] =
                "TaskAccess lifecycle surface.",
            ["backend/Services/TaskAccess/TaskAccessRecords.cs"] =
                "TaskAccess typed request/result records.",

            // Tier 2: current storage authority. JobStateMachine is the
            // single owner of folder moves and deletes; JobMutationService
            // owns folder creates and field edits; JobScannerService owns
            // reads; JobTransitionService combines a move with its side
            // effects (auto-commit, runner-active-state clear).
            ["backend/Services/Jobs/JobStateMachine.cs"] =
                "Single-state-machine authority for folder moves and deletes.",
            ["backend/Services/Jobs/JobMutationService.cs"] =
                "Owns folder creates and per-job field edits.",
            ["backend/Services/Jobs/JobScannerService.cs"] =
                "Owns reads against the job-folder tree.",
            ["backend/Services/Jobs/JobTransitionService.cs"] =
                "Combines folder moves with auto-commit and runner-active-state side effects.",
            ["backend/Services/JobWatcherService.cs"] =
                "FileSystemWatcher feeding the scanner; needs raw lane paths.",

            // Tier 2 (boot): the crash-recovery sweep walks 3-progress
            // before the in-memory store boots. Explicitly carved out in
            // the prompt for this task.
            ["backend/Services/Runner/CrashRecoveryService.cs"] =
                "Boot-time recovery; runs before ITaskAccess could be available.",

            // Tier 3: MIGRATION TARGET. These bypass the API today and
            // need to be rewritten against ITaskAccess. The whitelist
            // entry keeps the test green while the migration is queued;
            // remove the entry when the consumer is migrated.
            ["backend/Services/Runner/ProjectRunner.cs"] =
                "MIGRATION TARGET: pickup loop reads 3-progress and creates 3a-failed-pickup directly; should call ITaskAccess.ListByLane and TransitionLaneAsync after phase 4.",
        };

    /// <summary>
    /// Detects lane-folder path construction. A match means the source
    /// line is building a path into the lane-folder structure (the
    /// signal that motivates this whole rule).
    /// </summary>
    internal static readonly Regex LanePathConstruction = new(
        @"Path\.Combine\([^;]*?(?:" +
        @"JobStates\.(?:Backlog|Preparation|OrchestratorPrep|NeedsHumanReview|Ready|Progress|FailedPickup|AutoReview|HumanReview|Completed|Archive)\b" +
        @"|""(?:0-backlog|1-preparation|1a-orchestrator-prep|1b-needs-human-review|2-ready|3-progress|3a-failed-pickup|4-auto-review|5-human-review|6-completed|7-archive)""" +
        @")",
        RegexOptions.Compiled);

    /// <summary>
    /// Detects structural directory mutations: <c>Directory.Move</c> and
    /// <c>Directory.Delete</c>. These are the two calls that produce
    /// zombie folders and 409s when used against the job-folder tree
    /// from outside <c>JobStateMachine</c>; everywhere else can manage
    /// with <c>Directory.CreateDirectory</c>, file APIs, or
    /// <c>ITaskAccess</c>.
    /// </summary>
    internal static readonly Regex StructuralDirectoryMutation = new(
        @"\bDirectory\.(?:Move|Delete)\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void NoLaneFolderPathConstruction_OutsideWhitelist()
    {
        var repoRoot = ResolveRepoRoot();
        var violations = ScanForViolations(repoRoot, LanePathConstruction);

        Assert.True(
            violations.Count == 0,
            BuildFailureMessage(
                "lane-folder path construction",
                "Use ITaskAccess (or, until phase 4 ships, JobMutationService / JobStateMachine) to address jobs by id and lane name without building raw filesystem paths.",
                violations));
    }

    [Fact]
    public void NoStructuralDirectoryMutation_OutsideWhitelist()
    {
        var repoRoot = ResolveRepoRoot();
        var violations = ScanForViolations(repoRoot, StructuralDirectoryMutation);

        Assert.True(
            violations.Count == 0,
            BuildFailureMessage(
                "Directory.Move / Directory.Delete",
                "Folder structural changes against the job-folder tree must flow through JobStateMachine (and, after phase 4, ITaskAccess.TransitionLaneAsync). Non-job-folder uses of Directory.Move/Delete are rare; whitelist the file with a one-line reason if there is a legitimate case.",
                violations));
    }

    [Fact]
    public void WhitelistEntries_StillExist()
    {
        var repoRoot = ResolveRepoRoot();
        var missing = new List<string>();
        foreach (var entry in Whitelist.Keys)
        {
            var absolute = Path.Combine(repoRoot, entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
            {
                missing.Add(entry);
            }
        }

        Assert.True(
            missing.Count == 0,
            "Whitelist references files that no longer exist - prune the list:\n  " +
            string.Join("\n  ", missing));
    }

    [Fact]
    public void LanePathConstructionRegex_MatchesKnownViolations_AndIgnoresStateComparisons()
    {
        Assert.Matches(LanePathConstruction, "var x = Path.Combine(watchPath, JobStates.Progress);");
        Assert.Matches(LanePathConstruction, "var y = Path.Combine(root, JobStates.HumanReview, slug);");
        Assert.Matches(LanePathConstruction, "var z = Path.Combine(workspace, \"projects\", project, \"3-progress\", slug);");

        Assert.DoesNotMatch(LanePathConstruction, "if (info.State == JobStates.Progress) {}");
        Assert.DoesNotMatch(LanePathConstruction, "return JobStates.Ready;");
        Assert.DoesNotMatch(LanePathConstruction, "var lane = JobStates.Progress;");
    }

    [Fact]
    public void StructuralDirectoryMutationRegex_MatchesMoveAndDelete_AndIgnoresOtherDirectoryCalls()
    {
        Assert.Matches(StructuralDirectoryMutation, "Directory.Move(sourceFolder, targetDir);");
        Assert.Matches(StructuralDirectoryMutation, "Directory.Delete(info.FolderPath, true);");

        Assert.DoesNotMatch(StructuralDirectoryMutation, "Directory.CreateDirectory(dir);");
        Assert.DoesNotMatch(StructuralDirectoryMutation, "Directory.EnumerateDirectories(progressDir)");
        Assert.DoesNotMatch(StructuralDirectoryMutation, "if (Directory.Exists(targetDir))");
    }

    private static List<Violation> ScanForViolations(string repoRoot, Regex pattern)
    {
        var backendDir = Path.Combine(repoRoot, "backend");
        var violations = new List<Violation>();

        foreach (var path in Directory.EnumerateFiles(backendDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsExcludedPath(path)) continue;

            var relative = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
            if (Whitelist.ContainsKey(relative)) continue;

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (IsLineCommentedOut(line)) continue;
                if (!pattern.IsMatch(line)) continue;

                violations.Add(new Violation(relative, i + 1, line.Trim()));
            }
        }

        return violations;
    }

    private static bool IsExcludedPath(string path)
    {
        // Skip generated, build, and IDE folders.
        var normalised = path.Replace('\\', '/');
        return normalised.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalised.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalised.Contains("/Properties/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLineCommentedOut(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("///", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    private static string BuildFailureMessage(string ruleName, string remediation, List<Violation> violations)
    {
        var rendered = string.Join("\n  ", violations.Select(v => $"{v.RelativePath}:{v.Line}: {v.Source}"));
        return
            $"Found {violations.Count} forbidden {ruleName} call(s) outside the JobFolderAccessIsolation whitelist.\n" +
            $"Remediation: {remediation}\n" +
            $"Offending lines:\n  {rendered}";
    }

    private static string ResolveRepoRoot()
    {
        // Walk up from the test binary location to the repo root.
        // The test runner's working directory is the test project's
        // bin/Debug/net10.0/, so the repo root is a few levels above.
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && current is not null; i++)
        {
            var marker = Path.Combine(current, "backend", "OrchestratorApi.csproj");
            if (File.Exists(marker)) return current;
            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException(
            $"Could not locate repo root by walking up from {AppContext.BaseDirectory}.");
    }

    private readonly record struct Violation(string RelativePath, int Line, string Source);
}
