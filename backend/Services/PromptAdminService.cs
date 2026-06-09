using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrchestratorApi.Services;

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

    public PromptAdminService(RuntimePromptService prompts, ILogger<PromptAdminService> logger)
    {
        _prompts = prompts;
        _logger = logger;
    }

    public PromptCatalogResponse GetCatalog()
    {
        var items = new List<PromptCatalogItem>();
        foreach (var name in _prompts.EnumerateTemplateNames())
        {
            var meta = PromptDescriptionCatalog.Describe(name);
            var hasOverride = _prompts.HasOverride(name);
            var defaultContent = _prompts.TryReadDefault(name);
            items.Add(new PromptCatalogItem
            {
                Name = name,
                Title = meta.Title,
                Description = meta.Description,
                Group = meta.Group,
                HasOverride = hasOverride,
                HasDefault = defaultContent != null,
                DefaultChangedSinceOverride = hasOverride && DefaultChanged(name, defaultContent),
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
            EffectiveContent = overrideContent ?? defaultContent ?? string.Empty,
            DefaultSha = defaultSha,
            BaseDefaultSha = hasOverride ? sidecar?.BaseDefaultSha : null,
            DefaultChangedSinceOverride =
                hasOverride && sidecar?.BaseDefaultSha != null && defaultSha != null
                && !string.Equals(sidecar.BaseDefaultSha, defaultSha, StringComparison.OrdinalIgnoreCase),
            OverrideUpdatedAt = hasOverride ? sidecar?.UpdatedAt : null,
        };
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
    public bool HasOverride { get; set; }
    public bool DefaultChangedSinceOverride { get; set; }
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
        ["mode-framing-web.md"] = new("Mode framing: web access",
            "Framing block injected when a run is allowed to access the web.", "Runner"),
        ["commit-message.md"] = new("Commit message",
            "Generates the git commit message for the agent-produced change set.", "Runner"),
        ["summary-protocol.md"] = new("Summary protocol",
            "Generates the run summary / review protocol surfaced as status.md.", "Runner"),

        ["code-review-step.md"] = new("Code-review step",
            "Automated code-review pass over the run's diff in the post bracket.", "Review"),
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
