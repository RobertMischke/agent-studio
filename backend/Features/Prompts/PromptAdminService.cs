using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Drift;

namespace AgentStudio.Prompts;

/// <summary>
/// Admin surface over the runtime prompt templates: lists every template
/// with a human description of what it steers, exposes the shipped default
/// vs the application-wide override, and lets the operator edit (override),
/// reset (back to default), or re-baseline an override against a new default.
///
/// Default/Override layering lives in <see cref="RuntimePromptService"/>:
/// defaults ship in the install/bin tree (replaced on update), overrides
/// live in a user-data directory (survive updates). This service adds the
/// catalog metadata, the SHA-based "default changed since you overrode"
/// detection, and the small JSON sidecar that records which default version
/// an override was created against.
/// </summary>
public sealed class PromptAdminService
{
    private readonly RuntimePromptService _prompts;
    private readonly ILogger<PromptAdminService> _logger;
    private readonly ProjectSettingsService? _projectSettings;

    public PromptAdminService(
        RuntimePromptService prompts,
        ILogger<PromptAdminService> logger,
        ProjectSettingsService? projectSettings = null)
    {
        _prompts = prompts;
        _logger = logger;
        _projectSettings = projectSettings;
    }

    public PromptCatalogResponse GetCatalog()
    {
        var projectOverrides = ReadProjectOverrides();
        var items = new List<PromptCatalogItem>();
        foreach (var name in _prompts.EnumerateTemplateNames())
        {
            var meta = PromptDescriptionCatalog.Describe(name);
            var hasGlobalOverride = _prompts.HasOverride(name);
            var defaultContent = _prompts.TryReadDefault(name);
            var effective = _prompts.TryReadOverride(name) ?? defaultContent;
            var matchingProjectOverrides = projectOverrides
                .Where(item => string.Equals(
                    item.PromptName,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            var globalDefaultChanged = hasGlobalOverride
                && DefaultChanged(name, defaultContent);
            items.Add(new PromptCatalogItem
            {
                Name = name,
                Title = meta.Title,
                Description = meta.Description,
                Group = meta.Group,
                HasGlobalOverride = hasGlobalOverride,
                HasOverride = hasGlobalOverride || matchingProjectOverrides.Count > 0,
                HasDefault = defaultContent != null,
                GlobalDefaultChangedSinceOverride = globalDefaultChanged,
                DefaultChangedSinceOverride = globalDefaultChanged
                    || matchingProjectOverrides.Any(item => item.DefaultChangedSinceOverride),
                Slots = RuntimePromptService.ExtractSlots(effective).ToList(),
                UsageCount = PromptUsageCatalog.For(name).Count,
                ProjectOverrides = matchingProjectOverrides,
            });
        }
        return new PromptCatalogResponse
        {
            Items = items
                .OrderBy(i => PromptDescriptionCatalog.GroupOrder(i.Group))
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            OverrideDirectory = _prompts.OverrideDirectory,
        };
    }

    /// <summary>Full detail for one template, or null when the name is unknown (no default, no override).</summary>
    public PromptDetail? GetDetail(string name)
    {
        if (!IsKnown(name)) return null;

        var defaultContent = _prompts.TryReadDefault(name);
        var overrideContent = _prompts.TryReadOverride(name);
        var hasOverride = overrideContent != null;
        var meta = PromptDescriptionCatalog.Describe(name);
        var sidecar = ReadSidecar(name);
        var defaultSha = defaultContent == null ? null : Sha(defaultContent);

        var effective = overrideContent ?? defaultContent ?? string.Empty;
        return new PromptDetail
        {
            Name = name,
            Title = meta.Title,
            Description = meta.Description,
            Group = meta.Group,
            HasDefault = defaultContent != null,
            HasOverride = hasOverride,
            DefaultContent = defaultContent,
            OverrideContent = overrideContent,
            BaseDefaultContent = hasOverride ? sidecar?.BaseDefaultContent : null,
            EffectiveContent = effective,
            DefaultSha = defaultSha,
            BaseDefaultSha = hasOverride ? sidecar?.BaseDefaultSha : null,
            DefaultChangedSinceOverride =
                hasOverride && sidecar?.BaseDefaultSha != null && defaultSha != null
                && !string.Equals(sidecar.BaseDefaultSha, defaultSha, StringComparison.OrdinalIgnoreCase),
            OverrideUpdatedAt = hasOverride ? sidecar?.UpdatedAt : null,
            Slots = RuntimePromptService.ExtractSlots(effective).ToList(),
            Usages = PromptUsageCatalog.For(name).ToList(),
            ProjectOverrides = ReadProjectOverrides()
                .Where(item => string.Equals(
                    item.PromptName,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                .ToList(),
        };
    }

    /// <summary>
    /// Renders the effective template (override -&gt; default), or an explicit
    /// draft when <paramref name="content"/> is supplied, against the given slot
    /// values. This is the registry's "Probelauf": it shows exactly what the
    /// renderer would emit, including which declared slots were filled vs left
    /// empty, without persisting anything. Null when the name is unknown and no
    /// draft content was supplied.
    /// </summary>
    public PromptPreviewResult? Preview(string name, IReadOnlyDictionary<string, string?>? values, string? content = null)
    {
        string? template = content;
        if (template == null)
        {
            if (!IsKnown(name)) return null;
            template = _prompts.TryReadOverride(name) ?? _prompts.TryReadDefault(name);
        }
        if (template == null) return null;

        var slots = RuntimePromptService.ExtractSlots(template);
        var supplied = values ?? new Dictionary<string, string?>();
        bool Filled(string slot) => supplied.TryGetValue(slot, out var v) && !string.IsNullOrEmpty(v);

        return new PromptPreviewResult
        {
            Name = name,
            Rendered = RuntimePromptService.RenderContent(template, supplied),
            Slots = slots.ToList(),
            FilledSlots = slots.Where(Filled).ToList(),
            MissingSlots = slots.Where(s => !Filled(s)).ToList(),
        };
    }

    /// <summary>
    /// The coverage surface: which prompt-source sites are template-backed
    /// (covered) versus still assembling an inline instruction block (pending).
    /// The "covered" rows document the core runner/review/drift files the
    /// inline-migration (T3a) cleared - positive evidence a literal scan can't
    /// reproduce. The "pending" rows are produced live by the prompt-coverage
    /// guard (<see cref="PromptCoverageScanner"/>, T3b): every multi-line
    /// instruction block still pasted into a <c>.cs</c> file shows up here, the
    /// same findings that break the build. On the post-T3a tree this list is
    /// empty, so the section reads as fully covered.
    /// </summary>
    public PromptCoverageResponse GetCoverage()
    {
        var items = new List<PromptCoverageItem>
        {
            new() { Component = "backend/Features/Runner/ProjectRunner.cs", Status = "covered",
                Detail = "Conflict-resolution, project-boot and orchestrator-decision prompts moved to runtime templates." },
            new() { Component = "backend/Features/Runner/ReviewDecisionOrchestrator.cs", Status = "covered",
                Detail = "Review-decision, no-completion-signal, fallback and reissue-follow-up prompts are template-backed." },
            new() { Component = "backend/Features/Runner/GlobalOrchestratorBootstrap.cs", Status = "covered",
                Detail = "Global boot prompt, self-modification note and task-snapshot block moved to runtime templates." },
            new() { Component = "backend/Features/Drift/CodePatternDriftAnalysisService.cs", Status = "covered",
                Detail = "Code-pattern drift review prompt and canonical-sites block moved to runtime templates." },
        };

        foreach (var finding in ScanInlineFindings())
        {
            items.Add(new PromptCoverageItem
            {
                Component = $"{finding.File}:{finding.Line}",
                Status = "pending",
                Detail = $"Inline instruction block ('{finding.Signal}' …) not template-backed - move it to a runtime template: {finding.Snippet}",
            });
        }

        return new PromptCoverageResponse
        {
            Items = items,
            TotalSites = items.Count,
            CoveredSites = items.Count(i => string.Equals(i.Status, "covered", StringComparison.OrdinalIgnoreCase)),
            PendingSites = items.Count(i => string.Equals(i.Status, "pending", StringComparison.OrdinalIgnoreCase)),
        };
    }

    /// <summary>
    /// Runs the build-breaking inline-prompt guard over the product source tree
    /// so the coverage section shows the same findings the arch-test fails on.
    /// Degrades to an empty result (no pending rows) when the source tree is not
    /// reachable, e.g. a bin-only deployment.
    /// </summary>
    private IReadOnlyList<InlinePromptFinding> ScanInlineFindings()
    {
        try
        {
            return PromptCoverageScanner.ScanProductSource(DriftRepoRootLocator.Resolve());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "prompt-coverage-scan-failed");
            return Array.Empty<InlinePromptFinding>();
        }
    }

    /// <summary>Creates / replaces the override and records the default SHA it was based on.</summary>
    public PromptDetail? SaveOverride(string name, string content)
    {
        if (!IsKnown(name)) return null;
        _prompts.WriteOverride(name, content);
        var defaultContent = _prompts.TryReadDefault(name);
        WriteSidecar(name, new PromptOverrideSidecar
        {
            BaseDefaultSha = defaultContent == null ? null : Sha(defaultContent),
            BaseDefaultContent = defaultContent,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return GetDetail(name);
    }

    /// <summary>Removes the override (reset to shipped default) and its sidecar.</summary>
    public PromptDetail? ResetToDefault(string name)
    {
        if (!IsKnown(name)) return null;
        _prompts.DeleteOverride(name);
        DeleteSidecar(name);
        return GetDetail(name);
    }

    /// <summary>
    /// Keeps the current override content but re-points its recorded base
    /// SHA at the current default, clearing the "default changed" banner.
    /// This is the "behalten" (keep mine) resolution after a default update.
    /// </summary>
    public PromptDetail? RebaselineOverride(string name)
    {
        if (!IsKnown(name)) return null;
        if (!_prompts.HasOverride(name)) return GetDetail(name);
        var defaultContent = _prompts.TryReadDefault(name);
        WriteSidecar(name, new PromptOverrideSidecar
        {
            BaseDefaultSha = defaultContent == null ? null : Sha(defaultContent),
            BaseDefaultContent = defaultContent,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _logger.LogInformation("prompt-override-rebaselined template={Template}", name);
        return GetDetail(name);
    }

    private bool IsKnown(string name) =>
        IsSafeName(name)
        && (_prompts.TryReadDefault(name) != null || _prompts.HasOverride(name));

    private bool DefaultChanged(string name, string? defaultContent)
    {
        var sidecar = ReadSidecar(name);
        if (sidecar?.BaseDefaultSha == null || defaultContent == null) return false;
        return !string.Equals(sidecar.BaseDefaultSha, Sha(defaultContent), StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<PromptProjectOverride> ReadProjectOverrides()
    {
        if (_projectSettings is null) return [];

        var result = new List<PromptProjectOverride>();
        foreach (var (projectName, settings) in _projectSettings.GetAll())
        {
            if (settings.PipelineSteps is null) continue;
            foreach (var (stepId, setting) in settings.PipelineSteps)
            {
                if (string.IsNullOrWhiteSpace(setting.Prompt)) continue;

                var promptName = PromptPipelineBindings.ForStep(stepId);
                var defaultContent = promptName is null
                    ? null
                    : _prompts.TryReadDefault(promptName);
                var currentDefaultSha = defaultContent is null
                    ? null
                    : Sha(defaultContent);
                var (added, removed) = DiffCounts(defaultContent, setting.Prompt);
                result.Add(new PromptProjectOverride
                {
                    ProjectName = projectName,
                    StepId = stepId,
                    PromptName = promptName,
                    Content = setting.Prompt,
                    Orphaned = promptName is null || defaultContent is null,
                    MatchesDefault = defaultContent is not null
                        && Normalize(defaultContent) == Normalize(setting.Prompt),
                    AddedLines = added,
                    RemovedLines = removed,
                    BaseDefaultSha = setting.PromptBaseDefaultSha,
                    DefaultChangedSinceOverride =
                        setting.PromptBaseDefaultSha is not null
                        && currentDefaultSha is not null
                        && !string.Equals(
                            setting.PromptBaseDefaultSha,
                            currentDefaultSha,
                            StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        return result
            .OrderBy(item => item.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.StepId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (int Added, int Removed) DiffCounts(
        string? defaultContent,
        string overrideContent)
    {
        if (defaultContent is null)
            return (Normalize(overrideContent).Split('\n').Length, 0);

        var leftCounts = Normalize(defaultContent)
            .Split('\n')
            .GroupBy(line => line)
            .ToDictionary(group => group.Key, group => group.Count());
        var rightCounts = Normalize(overrideContent)
            .Split('\n')
            .GroupBy(line => line)
            .ToDictionary(group => group.Key, group => group.Count());
        var added = 0;
        var removed = 0;
        foreach (var line in leftCounts.Keys.Concat(rightCounts.Keys).Distinct())
        {
            leftCounts.TryGetValue(line, out var before);
            rightCounts.TryGetValue(line, out var after);
            if (after > before) added += after - before;
            if (before > after) removed += before - after;
        }
        return (added, removed);
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").Trim();

    // --- sidecar IO (records the default version an override was made against) ---

    private string SidecarPath(string name) =>
        Path.Combine(_prompts.OverrideDirectory, name + ".meta.json");

    private PromptOverrideSidecar? ReadSidecar(string name)
    {
        var path = SidecarPath(name);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PromptOverrideSidecar>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private void WriteSidecar(string name, PromptOverrideSidecar sidecar)
    {
        var path = SidecarPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(sidecar,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private void DeleteSidecar(string name)
    {
        var path = SidecarPath(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private static bool IsSafeName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(new[] { '/', '\\' }) < 0
        && !name.Contains("..", StringComparison.Ordinal);

    private static string Sha(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n")));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed class PromptCatalogResponse
{
    public List<PromptCatalogItem> Items { get; set; } = new();
    public string OverrideDirectory { get; set; } = "";
}

public sealed class PromptCatalogItem
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Group { get; set; } = "";
    public bool HasDefault { get; set; }
    public bool HasGlobalOverride { get; set; }
    public bool HasOverride { get; set; }
    public bool GlobalDefaultChangedSinceOverride { get; set; }
    public bool DefaultChangedSinceOverride { get; set; }
    public List<string> Slots { get; set; } = new();
    public int UsageCount { get; set; }
    public List<PromptProjectOverride> ProjectOverrides { get; set; } = new();
}

public sealed class PromptDetail
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Group { get; set; } = "";
    public bool HasDefault { get; set; }
    public bool HasOverride { get; set; }
    public string? DefaultContent { get; set; }
    public string? OverrideContent { get; set; }
    public string? BaseDefaultContent { get; set; }
    public string EffectiveContent { get; set; } = "";
    public string? DefaultSha { get; set; }
    public string? BaseDefaultSha { get; set; }
    public bool DefaultChangedSinceOverride { get; set; }
    public DateTimeOffset? OverrideUpdatedAt { get; set; }
    public List<string> Slots { get; set; } = new();
    public List<PromptUsageRef> Usages { get; set; } = new();
    public List<PromptProjectOverride> ProjectOverrides { get; set; } = new();
}

public sealed class PromptProjectOverride
{
    public string ProjectName { get; set; } = "";
    public string StepId { get; set; } = "";
    public string? PromptName { get; set; }
    public string Content { get; set; } = "";
    public bool Orphaned { get; set; }
    public bool MatchesDefault { get; set; }
    public int AddedLines { get; set; }
    public int RemovedLines { get; set; }
    public string? BaseDefaultSha { get; set; }
    public bool DefaultChangedSinceOverride { get; set; }
}

/// <summary>
/// Result of a non-persisting "Probelauf" render: the rendered output plus
/// which declared slots were filled vs left empty for the supplied values.
/// </summary>
public sealed class PromptPreviewResult
{
    public string Name { get; set; } = "";
    public string Rendered { get; set; } = "";
    public List<string> Slots { get; set; } = new();
    public List<string> FilledSlots { get; set; } = new();
    public List<string> MissingSlots { get; set; } = new();
}

/// <summary>
/// Coverage roll-up: which prompt-source sites are template-backed (covered)
/// vs still assembling instruction text inline (pending), with totals.
/// </summary>
public sealed class PromptCoverageResponse
{
    public List<PromptCoverageItem> Items { get; set; } = new();
    public int TotalSites { get; set; }
    public int CoveredSites { get; set; }
    public int PendingSites { get; set; }
}

public sealed class PromptCoverageItem
{
    public string Component { get; set; } = "";
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
}

internal sealed class PromptOverrideSidecar
{
    public string? BaseDefaultSha { get; set; }
    public string? BaseDefaultContent { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Static catalog of what each runtime prompt template steers. Templates not
/// listed here still appear in the admin surface with a generic description,
/// so a newly added template is never hidden.
/// </summary>
internal static class PromptDescriptionCatalog
{
    internal sealed record Meta(string Title, string Description, string Group);

    private static readonly string[] GroupRank =
        { "Runner", "Review", "Orchestrator", "Drift & Analysis", "Supervisor", "Utility", "Other" };

    public static int GroupOrder(string group)
    {
        var idx = Array.IndexOf(GroupRank, group);
        return idx < 0 ? GroupRank.Length : idx;
    }

    private static readonly Dictionary<string, Meta> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["runner-fresh-start.md"] = new("Runner: fresh start",
            "Bootstrap prompt handed to the CLI agent when a task starts from scratch.", "Runner"),
        ["runner-resume-interrupted.md"] = new("Runner: resume interrupted",
            "Prompt used to resume a run that was interrupted mid-flight, in the same session.", "Runner"),
        ["runner-resume-restart.md"] = new("Runner: resume by restart",
            "Prompt used to resume a task by restarting the CLI session from disk state.", "Runner"),
        ["runner-recovery-continuation.md"] = new("Runner: recovery continuation",
            "Prompt used to continue a task after a recovery/crash-recovery boundary.", "Runner"),
        ["epic-decomposition.md"] = new("Epic decomposition",
            "Decomposes an epic-sized task into smaller child tasks.", "Runner"),
        ["mode-framing-readonly.md"] = new("Mode framing: read-only",
            "Framing block injected for read-only modes (planning / research) to forbid mutations.", "Runner"),
        ["mode-framing-research.md"] = new("Mode framing: research",
            "Research delivery contract for one primary HTML report with linked supporting material.", "Runner"),
        ["mode-framing-web.md"] = new("Mode framing: web access",
            "Framing block injected when a run is allowed to access the web.", "Runner"),
        ["commit-message.md"] = new("Commit message",
            "Generates the git commit message for the agent-produced change set.", "Runner"),
        ["summary-protocol.md"] = new("Summary protocol",
            "Generates the run summary / review protocol surfaced as status.md.", "Runner"),

        ["code-review-step.md"] = new("Code-review step",
            "Automated code-review pass over the run's diff in the post bracket.", "Review"),
        ["code-review-grade.md"] = new("Code-review quality grade",
            "Automatic post-CORE pass that grades the task change set A/B/C/D (quality grade on every task).", "Review"),
        ["review-aspect-code-quality.md"] = new("Aspect: code quality",
            "Review aspect that grades code quality of the change.", "Review"),
        ["review-aspect-requirement-fit.md"] = new("Aspect: requirement fit",
            "Review aspect that checks the change against the task's requirements.", "Review"),
        ["review-aspect-tests-and-evidence.md"] = new("Aspect: tests & evidence",
            "Review aspect that checks for adequate tests and verification evidence.", "Review"),
        ["review-aspect-documentation-impact.md"] = new("Aspect: documentation impact",
            "Review aspect that checks whether docs need updating for the change.", "Review"),
        ["post-abort-review.md"] = new("Post-abort review",
            "Verdict step run after a non-clean run end (rerun / stronger-reissue / human-review / accept).", "Review"),

        ["orchestrator-review-decision.md"] = new("Orchestrator review decision",
            "Final orchestrator verdict for the auto-review lane (accept / reissue / escalate).", "Orchestrator"),

        ["adr-code-drift.md"] = new("Drift: ADR vs code",
            "Reports drift between architecture-decision records and the code.", "Drift & Analysis"),
        ["docs-marketing-drift.md"] = new("Drift: docs vs behavior",
            "Reports drift between documentation / marketing copy and shipped behavior.", "Drift & Analysis"),
        ["software-architecture-drift.md"] = new("Drift: software architecture",
            "Reports drift in the described software architecture vs the code.", "Drift & Analysis"),
        ["spec-task-job-drift.md"] = new("Drift: spec vs tasks",
            "Reports drift between spec/task definitions and the actual task folders.", "Drift & Analysis"),
        ["steering-docs-summary-and-drift.md"] = new("Steering docs summary & drift",
            "Summarizes the steering docs and reports drift against them.", "Drift & Analysis"),
        ["roadmap-alignment-review.md"] = new("Roadmap alignment review",
            "Compares the task queue against the roadmap and reports misalignment.", "Drift & Analysis"),
        ["recurring-output-pattern-review.md"] = new("Recurring output-pattern review",
            "Scans recent agent outputs for recurring failure / output patterns.", "Drift & Analysis"),

        ["supervisor-soft-reasoning.md"] = new("Supervisor soft-reasoning",
            "Layer-2 soft-reasoning second-opinion pass over runner state.", "Supervisor"),

        ["title-generate.md"] = new("Title generation",
            "Generates a concise task title from the task prompt.", "Utility"),
        ["prompt-enhance.md"] = new("Prompt enhancement",
            "Expands / enhances a raw task prompt before it is queued.", "Utility"),
        ["wiki-search-expand.md"] = new("Wiki search expansion",
            "Expands a wiki search query with German/English synonyms for the semantic search layer.", "Utility"),
    };

    public static Meta Describe(string name)
    {
        if (Map.TryGetValue(name, out var meta)) return meta;
        var title = Path.GetFileNameWithoutExtension(name)
            .Replace('-', ' ');
        if (title.Length > 0) title = char.ToUpperInvariant(title[0]) + title[1..];
        return new Meta(title, "Runtime prompt template (no catalog description yet).", "Other");
    }
}
