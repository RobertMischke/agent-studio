using System.Collections.Concurrent;

namespace AgentStudio.Publishing;

/// <summary>
/// PUB-1 - derives a project's publish targets and their pending deltas from pure
/// repository facts (workflows, tags, manifests), never from an operator setting.
/// The operator intent is "dead simple sehen, dass etwas Publizierbares da ist -
/// im Prinzip nach jedem Task": a Project Hub badge like
/// "NuGet 0.3.1 -&gt; 4 tasks pending", and a per-task "publishable: npm, website"
/// chip.
///
/// <para><b>Target derivation.</b> A release-triggered workflow (tag push or a
/// published release) plus an npm/NuGet publish step (or a located manifest)
/// yields a package target; a Pages/deploy-website workflow yields a website
/// target. The current package version is the last <c>v*</c> tag.</para>
///
/// <para><b>Pending delta.</b> Merged mainline (first-parent) commits on the
/// integration branch since the reference point (last tag / last deploy) that
/// touch the target's path scope - package source paths vs the website folder.
/// Zero pending = no badge (quiet). A package with no tag at all surfaces the
/// "first publish pending" special state instead of a count.</para>
///
/// <para>Read-only: it forks a handful of cheap plumbing commands and reads a few
/// files; it never mutates the repository. Cached per project for a few seconds
/// so the 5 s snapshot poll and the board fold reuse one computation.</para>
/// </summary>
public sealed class PublishTargetService
{
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<PublishTargetService> _logger;

    /// <summary>Default website source folder when a Pages workflow does not name one.</summary>
    public const string DefaultWebsiteRoot = "website";

    /// <summary>Repo-root directories that are never package source ("Package-Quellpfade").</summary>
    private static readonly string[] PackageMetaExcludes = [".github", "docs", ".orchestrator"];

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, (DateTime At, ProjectPublishComputation Value)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public PublishTargetService(
        GitService git,
        ProjectSettingsService settings,
        ILogger<PublishTargetService> logger)
    {
        _git = git;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Wire-facing publish status for the Project Hub badge: derived targets with
    /// their pending deltas, minus the internal per-commit SHA sets. Never throws;
    /// a non-repo / unknown project yields <see cref="ProjectPublishStatus.IsRepo"/>
    /// false with an <see cref="ProjectPublishStatus.Error"/>.
    /// </summary>
    public ProjectPublishStatus GetProjectPublishStatus(string projectName)
    {
        var computation = GetComputation(projectName);
        return new ProjectPublishStatus
        {
            Project = projectName,
            IsRepo = computation.IsRepo,
            Error = computation.Error,
            Targets = computation.Targets.Select(t => t.Target).ToList(),
        };
    }

    /// <summary>
    /// The internal computation (targets + pending SHA sets) used by the board
    /// fold to answer per-task publishability by set-membership. Cached per project.
    /// </summary>
    internal ProjectPublishComputation GetComputation(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return ProjectPublishComputation.Empty(projectName ?? "", "projectName is required");

        if (_cache.TryGetValue(projectName, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.Value;

        using var _t = GitProcessTelemetry.BeginRequest("publish/derive", _logger);
        var fresh = Compute(projectName);
        _cache[projectName] = (DateTime.UtcNow, fresh);
        return fresh;
    }

    /// <summary>Drops the cached computations. Tests use this after mutating a fixture repo.</summary>
    internal void InvalidateCache() => _cache.Clear();

    private ProjectPublishComputation Compute(string projectName)
    {
        var root = _git.ResolveProjectRepoRoot(projectName);
        if (string.IsNullOrWhiteSpace(root))
            return ProjectPublishComputation.Empty(projectName, "Project has no configured git repository.");
        if (!_git.IsGitRepo(root))
            return ProjectPublishComputation.Empty(projectName, $"Not a git repository: {root}");

        var workflows = ReadWorkflows(root!);
        var integrationBranch = ResolveIntegrationBranch(projectName, root!);
        var latestTag = _git.GetLatestVersionTag(root!);

        var websiteRoots = ResolveWebsiteRoots(workflows);
        var targets = new List<PublishTargetComputation>();

        targets.AddRange(DerivePackageTargets(root!, integrationBranch, workflows, websiteRoots, latestTag));

        var websiteTarget = DeriveWebsiteTarget(root!, integrationBranch, workflows, websiteRoots, latestTag);
        if (websiteTarget != null) targets.Add(websiteTarget);

        return new ProjectPublishComputation(projectName, true, null, targets);
    }

    // ----- package -----

    private IEnumerable<PublishTargetComputation> DerivePackageTargets(
        string root, string integrationBranch, IReadOnlyList<WorkflowFacts> workflows,
        IReadOnlyList<string> websiteRoots, string? latestTag)
    {
        var hasReleaseTrigger = workflows.Any(w => w.HasReleaseTrigger);
        if (!hasReleaseTrigger) yield break;

        // Which ecosystems does a workflow actually publish? Fall back to manifest
        // presence when a release workflow exists but names no publish verb.
        var wantsNpm = workflows.Any(w => w.PublishesNpm);
        var wantsNuGet = workflows.Any(w => w.PublishesNuGet);

        ManifestInfo? npm = null, nuget = null;
        if (wantsNpm) npm = PublishManifestLocator.LocateNpm(root, websiteRoots);
        if (wantsNuGet) nuget = PublishManifestLocator.LocateNuGet(root, websiteRoots);

        if (!wantsNpm && !wantsNuGet)
        {
            // Release trigger but no explicit publish step - infer from a manifest.
            npm = PublishManifestLocator.LocateNpm(root, websiteRoots);
            if (npm == null) nuget = PublishManifestLocator.LocateNuGet(root, websiteRoots);
        }

        if (npm != null || wantsNpm)
            yield return BuildPackageTarget(root, integrationBranch, PublishEcosystems.Npm, "npm", npm, websiteRoots, latestTag);
        if (nuget != null || (wantsNuGet && npm == null))
            yield return BuildPackageTarget(root, integrationBranch, PublishEcosystems.NuGet, "NuGet", nuget, websiteRoots, latestTag);
    }

    private PublishTargetComputation BuildPackageTarget(
        string root, string integrationBranch, string ecosystem, string label,
        ManifestInfo? manifest, IReadOnlyList<string> websiteRoots, string? latestTag)
    {
        var sourceRoot = manifest?.SourceRootRelDir ?? string.Empty;
        var include = string.IsNullOrEmpty(sourceRoot) ? Array.Empty<string>() : [sourceRoot];
        var exclude = BuildPackageExcludes(sourceRoot, websiteRoots);

        var currentVersion = StripV(latestTag);
        var firstPublishPending = latestTag == null;

        // Since the last release tag, or (first publish) over the whole branch so
        // the per-task chip still resolves; the count is only asserted when a tag
        // anchors it.
        var commits = _git.GetMainlineCommitsForScope(
            root, integrationBranch, include, exclude, sinceRef: latestTag);
        var shas = commits.Select(c => c.Sha).ToList();

        var target = new PublishTarget
        {
            Id = $"package:{ecosystem}",
            Kind = PublishTargetKind.Package,
            Ecosystem = ecosystem,
            Label = label,
            PackageName = manifest?.PackageName,
            CurrentVersion = currentVersion,
            FirstPublishPending = firstPublishPending,
            PendingCount = firstPublishPending ? null : commits.Count,
            ReferenceKind = firstPublishPending ? PublishReferenceKinds.None : PublishReferenceKinds.Tag,
            Reference = firstPublishPending ? null : latestTag,
        };
        return new PublishTargetComputation(target, shas);
    }

    private static string[] BuildPackageExcludes(string sourceRoot, IReadOnlyList<string> websiteRoots)
    {
        var excludes = new List<string>();
        // Website changes belong to the website target, never the package.
        excludes.AddRange(websiteRoots);
        // Repo-root meta folders are never package source. When the package sits
        // in an explicit sub-directory these are harmless no-ops.
        if (string.IsNullOrEmpty(sourceRoot))
            excludes.AddRange(PackageMetaExcludes);
        return excludes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ----- website -----

    private PublishTargetComputation? DeriveWebsiteTarget(
        string root, string integrationBranch, IReadOnlyList<WorkflowFacts> workflows,
        IReadOnlyList<string> websiteRoots, string? latestTag)
    {
        if (!workflows.Any(w => w.DeploysWebsite)) return null;

        var include = websiteRoots.ToArray();

        // Resolve the "last deploy" baseline honestly from git facts:
        //  1. a gh-pages deploy branch tip date (the classic Pages flow), else
        //  2. the last release tag (websites usually ship with a release), else
        //  3. no baseline (modern actions/deploy-pages leaves no git marker) -
        //     stay quiet rather than invent a count.
        string referenceKind;
        string? reference;
        string? sinceRef = null;
        string? sinceDateIso = null;

        var deployDate = _git.GetTipCommitDateUtc(root, "gh-pages")
                         ?? _git.GetTipCommitDateUtc(root, "origin/gh-pages");
        if (deployDate != null)
        {
            referenceKind = PublishReferenceKinds.PagesBranch;
            reference = deployDate.Value.ToString("yyyy-MM-dd");
            sinceDateIso = deployDate.Value.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }
        else if (latestTag != null)
        {
            referenceKind = PublishReferenceKinds.ReleaseTag;
            reference = latestTag;
            sinceRef = latestTag;
        }
        else
        {
            referenceKind = PublishReferenceKinds.None;
            reference = null;
        }

        var commits = _git.GetMainlineCommitsForScope(
            root, integrationBranch, include, Array.Empty<string>(),
            sinceRef: sinceRef, sinceDateIso: sinceDateIso);
        var shas = commits.Select(c => c.Sha).ToList();

        var target = new PublishTarget
        {
            Id = "website",
            Kind = PublishTargetKind.Website,
            Ecosystem = null,
            Label = "Website",
            PackageName = null,
            CurrentVersion = null,
            FirstPublishPending = false,
            PendingCount = referenceKind == PublishReferenceKinds.None ? null : commits.Count,
            ReferenceKind = referenceKind,
            Reference = reference,
        };
        return new PublishTargetComputation(target, shas);
    }

    // ----- helpers -----

    private static IReadOnlyList<string> ResolveWebsiteRoots(IReadOnlyList<WorkflowFacts> workflows)
    {
        var custom = workflows
            .Select(w => w.PagesArtifactPath)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        var root = string.IsNullOrWhiteSpace(custom) ? DefaultWebsiteRoot : custom!.Replace('\\', '/').Trim('/');
        return [root];
    }

    private List<WorkflowFacts> ReadWorkflows(string root)
    {
        var dir = Path.Combine(root, ".github", "workflows");
        if (!Directory.Exists(dir)) return [];
        var facts = new List<WorkflowFacts>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.yml").Concat(Directory.EnumerateFiles(dir, "*.yaml"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return facts;
        }
        foreach (var file in files)
        {
            string content;
            try { content = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            facts.Add(PublishWorkflowParser.Parse(Path.GetFileName(file), content));
        }
        return facts;
    }

    private string ResolveIntegrationBranch(string projectName, string root)
    {
        var configured = _settings.Get(projectName).IntegrationBranch;
        return _git.ResolveIntegrationBranch(root, configured);
    }

    private static string? StripV(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim();
        return t.StartsWith('v') || t.StartsWith('V') ? t[1..] : t;
    }
}

/// <summary>
/// Internal cached computation: derived targets (each with its pending SHA set)
/// plus the repo/error state. The wire projection
/// (<see cref="PublishTargetService.GetProjectPublishStatus"/>) drops the SHA sets.
/// </summary>
internal sealed record ProjectPublishComputation(
    string Project,
    bool IsRepo,
    string? Error,
    List<PublishTargetComputation> Targets)
{
    public static ProjectPublishComputation Empty(string project, string error)
        => new(project, false, error, []);
}
