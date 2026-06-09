using OrchestratorApi.Models;
using OrchestratorApi.Services.Tasks;

namespace OrchestratorApi.Services;

/// <summary>
/// Project-level Steering Docs surface. Lists the agent-facing instruction
/// files that apply to a watched project (README, AGENTS, ROADMAP, the
/// task contract, the skills lookup, the ADR archive, runtime prompt
/// references, project settings, and project-specific steering notes),
/// reports their existence, last-modified timestamp, and size, and
/// produces a small heuristic warning set the UI can render alongside.
///
/// The service does not summarize or rewrite docs. The "Summarize Steering
/// Docs", "Check Docs Drift", "Analyze Recurring Failures", and
/// "Propose ... Update" actions live in the UI and queue normal
/// 1-preparation tasks; this layer only inventories what is on disk.
/// </summary>
public class ProjectSteeringDocsService
{
    private readonly TaskScannerService _scanner;
    private readonly ILogger<ProjectSteeringDocsService> _logger;

    /// <summary>
    /// Files older than this are flagged as "stale" relative to the most
    /// recent steering edit. The threshold is a heuristic: when one file
    /// in the set has moved recently and another sat untouched for many
    /// times longer, the latter is at higher risk of being out of sync.
    /// </summary>
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(120);

    public ProjectSteeringDocsService(TaskScannerService scanner, ILogger<ProjectSteeringDocsService> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    private WatchPathEntry? FindProject(string projectName) =>
        _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));

    private static string? ResolveBaseDir(WatchPathEntry entry)
    {
        var b = !string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RepositoryPath : entry.RootPath;
        if (string.IsNullOrWhiteSpace(b)) b = entry.Path;
        return string.IsNullOrWhiteSpace(b) ? null : b;
    }

    /// <summary>
    /// Inventory the canonical steering sources for a watched project. The
    /// list is fixed (so the UI knows what to expect) but every entry can
    /// be marked Missing or Stale. <see cref="SteeringDocsOverview.BaseDir"/>
    /// is the resolved repo root the relative paths are evaluated against.
    /// </summary>
    public SteeringDocsOverview? GetOverview(string projectName)
    {
        var entry = FindProject(projectName);
        if (entry == null) return null;
        var baseDir = ResolveBaseDir(entry);
        if (baseDir == null) return null;

        var sources = new List<SteeringDocsSource>();
        foreach (var d in CanonicalSources)
        {
            sources.Add(InspectSource(baseDir, d));
        }

        // Inspect prompts/runtime/ as a directory listing rather than one file.
        sources.Add(InspectRuntimePromptsDir(baseDir));

        var warnings = BuildWarnings(sources);
        var lastUpdated = sources
            .Where(s => s.Exists && s.UpdatedAt.HasValue)
            .Select(s => s.UpdatedAt!.Value)
            .DefaultIfEmpty(default)
            .Max();

        return new SteeringDocsOverview(
            ProjectName: projectName,
            BaseDir: baseDir,
            Sources: sources,
            Warnings: warnings,
            LastUpdated: lastUpdated == default ? null : lastUpdated);
    }

    public SteeringFileContent? ReadFile(string projectName, string relPath)
    {
        if (!IsSafeRelPath(relPath)) return null;
        var entry = FindProject(projectName);
        if (entry == null) return null;
        var baseDir = ResolveBaseDir(entry);
        if (baseDir == null) return null;
        var full = Path.GetFullPath(Path.Combine(baseDir, relPath));
        var root = Path.GetFullPath(baseDir);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(full)) return null;
        // Only allow files that the inventory exposes. Reading arbitrary
        // repository files would turn this into a generic file browser,
        // which is out of scope for the Steering Docs surface.
        var allowed = CanonicalSources.Any(d =>
            string.Equals(NormalizeRel(d.RelPath), NormalizeRel(relPath), StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            // Allow sub-files inside prompts/runtime/ and docs/cli-skills/
            // as well, since they are referenced as a directory.
            var rel = NormalizeRel(relPath);
            if (!rel.StartsWith("prompts/runtime/", StringComparison.OrdinalIgnoreCase) &&
                !rel.StartsWith("docs/cli-skills/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        var content = File.ReadAllText(full);
        return new SteeringFileContent(NormalizeRel(relPath), content);
    }

    // ----------------------------------------------------------------------
    // Inventory implementation
    // ----------------------------------------------------------------------

    private record CanonicalSourceDef(
        string Id,
        string Label,
        string RelPath,
        SteeringDocsSourceKind Kind,
        string Why);

    /// <summary>
    /// The set of files the surface promises to inspect for every project.
    /// Missing entries are not silent failures: the UI shows a "missing
    /// source" tile so the user knows the doc was expected.
    /// </summary>
    private static readonly IReadOnlyList<CanonicalSourceDef> CanonicalSources = new List<CanonicalSourceDef>
    {
        new("readme", "README", "README.md", SteeringDocsSourceKind.ProjectReadme,
            "Product description and on-boarding entry point. The first thing a new contributor reads."),
        new("agents", "AGENTS.md", "AGENTS.md", SteeringDocsSourceKind.AgentInstructions,
            "Single source of truth for agent instructions across CLIs."),
        new("claude-shim", "CLAUDE.md", "CLAUDE.md", SteeringDocsSourceKind.AgentCliShim,
            "Compatibility shim that points Claude Code at AGENTS.md. Should stay tiny."),
        new("copilot-shim", ".github/copilot-instructions.md", ".github/copilot-instructions.md",
            SteeringDocsSourceKind.AgentCliShim,
            "Compatibility shim for the GitHub Copilot coding agent. Should stay tiny."),
        new("frontend-agents", "frontend/AGENTS.md", "frontend/AGENTS.md",
            SteeringDocsSourceKind.AgentInstructions,
            "Frontend-scoped agent instructions; applies to changes under frontend/."),
        new("roadmap", "ROADMAP.md", "ROADMAP.md", SteeringDocsSourceKind.Roadmap,
            "Product thesis, near-term themes, hard boundaries, and decision principles."),
        new("task-contract", "Task contract", "docs/agent-task-contract.md",
            SteeringDocsSourceKind.TaskContract,
            "The boundary the application enforces against CLI agents per task."),
        new("skills-architecture", "Skills architecture", "docs/skills-architecture.md",
            SteeringDocsSourceKind.SkillsLookup,
            "How portable skills are defined, distributed, and discovered."),
        new("cli-skills-readme", "CLI skills lookup", "docs/cli-skills/README.md",
            SteeringDocsSourceKind.SkillsLookup,
            "Per-CLI skill index. Required reading before touching a CLI driver."),
        new("adr", "Architecture decisions", "docs/architecture-decisions.md",
            SteeringDocsSourceKind.AdrIndex,
            "Durable archive of load-bearing architectural decisions."),
        new("design-principles", "Design principles", "docs/design-principles.md",
            SteeringDocsSourceKind.SteeringNote,
            "UX contract + design principles that the agent-facing rules build on."),
        new("commit-doctrine", "Commit / push doctrine", "docs/commit-push-doctrine.md",
            SteeringDocsSourceKind.SteeringNote,
            "Where the application owns the commit and push boundary."),
        new("appsettings", "Project settings", "backend/appsettings.json",
            SteeringDocsSourceKind.ProjectSettings,
            "Default backend settings (watch paths, supervisor toggles, etc.)."),
        new("appsettings-local", "Local settings (gitignored)", "backend/appsettings.Local.json",
            SteeringDocsSourceKind.ProjectSettings,
            "Local-only overrides; gitignored. Existence flips dev-mode markers."),
    };

    private static SteeringDocsSource InspectSource(string baseDir, CanonicalSourceDef def)
    {
        var rel = NormalizeRel(def.RelPath);
        var full = Path.GetFullPath(Path.Combine(baseDir, rel));
        if (!File.Exists(full))
        {
            return new SteeringDocsSource(
                Id: def.Id,
                Label: def.Label,
                RelPath: rel,
                Kind: def.Kind,
                Why: def.Why,
                Exists: false,
                UpdatedAt: null,
                Size: 0,
                Children: null);
        }
        var fi = new FileInfo(full);
        return new SteeringDocsSource(
            Id: def.Id,
            Label: def.Label,
            RelPath: rel,
            Kind: def.Kind,
            Why: def.Why,
            Exists: true,
            UpdatedAt: fi.LastWriteTimeUtc,
            Size: fi.Length,
            Children: null);
    }

    private static SteeringDocsSource InspectRuntimePromptsDir(string baseDir)
    {
        var dirRel = "prompts/runtime";
        var full = Path.GetFullPath(Path.Combine(baseDir, dirRel));
        if (!Directory.Exists(full))
        {
            return new SteeringDocsSource(
                Id: "runtime-prompts",
                Label: "Runtime prompts",
                RelPath: dirRel,
                Kind: SteeringDocsSourceKind.RuntimePrompt,
                Why: "Editable Markdown templates rendered by backend runtime services.",
                Exists: false,
                UpdatedAt: null,
                Size: 0,
                Children: null);
        }
        var children = new List<SteeringDocsSourceChild>();
        long totalSize = 0;
        DateTime? newest = null;
        foreach (var f in Directory.EnumerateFiles(full, "*.md", SearchOption.TopDirectoryOnly).OrderBy(p => p))
        {
            var fi = new FileInfo(f);
            var rel = NormalizeRel(Path.Combine(dirRel, fi.Name));
            children.Add(new SteeringDocsSourceChild(
                Name: fi.Name,
                RelPath: rel,
                UpdatedAt: fi.LastWriteTimeUtc,
                Size: fi.Length));
            totalSize += fi.Length;
            if (newest == null || fi.LastWriteTimeUtc > newest) newest = fi.LastWriteTimeUtc;
        }
        return new SteeringDocsSource(
            Id: "runtime-prompts",
            Label: "Runtime prompts",
            RelPath: dirRel,
            Kind: SteeringDocsSourceKind.RuntimePrompt,
            Why: "Editable Markdown templates rendered by backend runtime services.",
            Exists: children.Count > 0,
            UpdatedAt: newest,
            Size: totalSize,
            Children: children);
    }

    private static List<SteeringDocsWarning> BuildWarnings(IList<SteeringDocsSource> sources)
    {
        var warnings = new List<SteeringDocsWarning>();
        // Critical missing files first; the application requires these.
        var criticalMissing = sources.Where(s =>
            !s.Exists &&
            (s.Kind == SteeringDocsSourceKind.AgentInstructions ||
             s.Kind == SteeringDocsSourceKind.ProjectReadme ||
             s.Kind == SteeringDocsSourceKind.TaskContract));
        foreach (var s in criticalMissing)
        {
            // The frontend AGENTS scope is not load-bearing on every project,
            // demote it to "info" by skipping when there's no frontend at all.
            if (s.Id == "frontend-agents") continue;
            warnings.Add(new SteeringDocsWarning(
                Severity: SteeringDocsWarningSeverity.High,
                Kind: SteeringDocsWarningKind.MissingSource,
                Message: $"Required steering source is missing: {s.RelPath}.",
                SourceId: s.Id,
                EvidenceRefs: new List<string> { s.RelPath }));
        }
        // Optional missing files as warn.
        foreach (var s in sources.Where(s => !s.Exists))
        {
            if (s.Kind == SteeringDocsSourceKind.AgentInstructions ||
                s.Kind == SteeringDocsSourceKind.ProjectReadme ||
                s.Kind == SteeringDocsSourceKind.TaskContract)
            {
                continue; // already emitted as critical above (or skipped)
            }
            warnings.Add(new SteeringDocsWarning(
                Severity: SteeringDocsWarningSeverity.Info,
                Kind: SteeringDocsWarningKind.MissingSource,
                Message: $"No {s.Label} found at {s.RelPath}.",
                SourceId: s.Id,
                EvidenceRefs: new List<string> { s.RelPath }));
        }
        // Stale file detection: if a source exists, but is older than the
        // staleness threshold and at least one other source has been
        // touched recently, surface a "may be out of date" warning.
        var anyRecent = sources.Any(s => s.Exists && s.UpdatedAt is { } u && (DateTime.UtcNow - u) < TimeSpan.FromDays(30));
        if (anyRecent)
        {
            foreach (var s in sources.Where(s => s.Exists && s.UpdatedAt is { } u && (DateTime.UtcNow - u) > StaleThreshold))
            {
                warnings.Add(new SteeringDocsWarning(
                    Severity: SteeringDocsWarningSeverity.Warn,
                    Kind: SteeringDocsWarningKind.Stale,
                    Message: $"{s.Label} hasn't been updated in over {(int)StaleThreshold.TotalDays} days while other steering files have moved recently.",
                    SourceId: s.Id,
                    EvidenceRefs: new List<string> { s.RelPath }));
            }
        }
        // Conflict heuristic: AGENTS shim files (CLAUDE.md, copilot
        // instructions) larger than 1 KB suggest the shim has drifted from
        // its three-line contract.
        foreach (var s in sources.Where(s => s.Kind == SteeringDocsSourceKind.AgentCliShim && s.Exists && s.Size > 1024))
        {
            warnings.Add(new SteeringDocsWarning(
                Severity: SteeringDocsWarningSeverity.Warn,
                Kind: SteeringDocsWarningKind.PossibleConflict,
                Message: $"{s.Label} is {s.Size:N0} bytes; compatibility shims should stay tiny and point at AGENTS.md.",
                SourceId: s.Id,
                EvidenceRefs: new List<string> { s.RelPath }));
        }
        return warnings;
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private static string NormalizeRel(string rel) =>
        (rel ?? "").Replace('\\', '/').TrimStart('/');

    private static bool IsSafeRelPath(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return false;
        if (relPath.Contains("..", StringComparison.Ordinal)) return false;
        if (Path.IsPathRooted(relPath)) return false;
        var ext = Path.GetExtension(relPath);
        return string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase);
    }
}

public enum SteeringDocsSourceKind
{
    ProjectReadme,
    AgentInstructions,
    AgentCliShim,
    Roadmap,
    TaskContract,
    SkillsLookup,
    AdrIndex,
    RuntimePrompt,
    ProjectSettings,
    SteeringNote,
}

public enum SteeringDocsWarningSeverity { Info, Warn, High }

public enum SteeringDocsWarningKind { MissingSource, Stale, PossibleConflict, RecurringFailure }

public record SteeringDocsSourceChild(string Name, string RelPath, DateTime UpdatedAt, long Size);

public record SteeringDocsSource(
    string Id,
    string Label,
    string RelPath,
    SteeringDocsSourceKind Kind,
    string Why,
    bool Exists,
    DateTime? UpdatedAt,
    long Size,
    List<SteeringDocsSourceChild>? Children);

public record SteeringDocsWarning(
    SteeringDocsWarningSeverity Severity,
    SteeringDocsWarningKind Kind,
    string Message,
    string? SourceId,
    List<string> EvidenceRefs);

public record SteeringDocsOverview(
    string ProjectName,
    string BaseDir,
    List<SteeringDocsSource> Sources,
    List<SteeringDocsWarning> Warnings,
    DateTime? LastUpdated);

public record SteeringFileContent(string RelPath, string Content);
