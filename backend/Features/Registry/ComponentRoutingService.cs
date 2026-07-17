using System.Text;

namespace AgentStudio.Registry;

/// <summary>
/// Resolves a visible surface/component to its primary implementation project
/// and delivery chain. Navigation scope is deliberately an input/output field,
/// never an ownership fallback when a matching declaration exists.
/// </summary>
public sealed class ComponentRoutingService
{
    public const double ConfidentThreshold = 0.75;
    private readonly ProjectRegistry _projects;

    public ComponentRoutingService(ProjectRegistry projects) => _projects = projects;

    public ComponentRoutingResolution Resolve(ComponentRoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projects = _projects.List();
        var navigation = ResolveProject(projects, request.NavigationProjectId);
        var candidates = projects
            .SelectMany(project => project.OwnershipMappings.Select(mapping => (project, mapping, score: Score(request, mapping))))
            .Where(row => row.score > 0)
            .OrderByDescending(row => row.score)
            .ThenByDescending(row => row.mapping.Confidence)
            .ToList();

        if (candidates.Count == 0)
        {
            if (navigation == null)
            {
                return ComponentRoutingResolution.Question(
                    request, null, "No ownership mapping or valid navigation project matches this surface.");
            }

            if (!string.IsNullOrWhiteSpace(request.Component) || !string.IsNullOrWhiteSpace(request.ObservedSurface))
                return ComponentRoutingResolution.Question(
                    request, navigation, "No ownership mapping matches the affected surface/component.");
            return ComponentRoutingResolution.Local(request, navigation);
        }

        var best = candidates[0];
        var tied = candidates.Where(row => Math.Abs(row.score - best.score) < 0.05).ToList();
        var distinctOwners = tied.Select(row => row.mapping.PrimaryProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinctOwners.Count > 1)
        {
            return ComponentRoutingResolution.Question(
                request,
                navigation,
                "Ownership mappings conflict at the same confidence.",
                tied.Select(row => $"{row.mapping.Component} -> {row.mapping.PrimaryProjectId}").ToList());
        }

        var mapping = best.mapping;
        var owner = projects.FirstOrDefault(project =>
            string.Equals(project.Id, mapping.PrimaryProjectId, StringComparison.OrdinalIgnoreCase));
        if (owner == null || owner.Archived)
        {
            return ComponentRoutingResolution.Question(
                request, navigation, "The mapped primary project is missing or archived.", [mapping.PrimaryProjectId]);
        }

        var confidence = Math.Min(mapping.Confidence, best.score);
        var alternatives = mapping.UnresolvedAlternatives.ToList();
        var requiresQuestion = confidence < ConfidentThreshold || alternatives.Count > 0;
        var consumers = mapping.ConsumerProjectIds.Select(id => projects.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
            .Where(project => project != null)
            .Select(project => new RoutingProjectRef(project!.Id, project.ShortCode, project.DisplayName))
            .ToList();
        return new ComponentRoutingResolution(
            request.ObservedSurface,
            mapping.Component,
            mapping.PackageOrModule,
            navigation == null ? null : new RoutingProjectRef(navigation.Id, navigation.ShortCode, navigation.DisplayName),
            new RoutingProjectRef(owner.Id, owner.ShortCode, owner.DisplayName),
            mapping.Repository,
            consumers,
            mapping.IntegrationHosts,
            mapping.ReleaseArtifact,
            mapping.VersioningMechanism,
            mapping.DeploymentSteps,
            mapping.Environments,
            owner.ShortCode,
            owner.Id,
            mapping.Evidence,
            confidence,
            alternatives,
            requiresQuestion,
            requiresQuestion ? "Ownership confidence is low or alternatives remain unresolved." : null,
            BuildPreview(owner, mapping),
            mapping.Id,
            mapping.Version);
    }

    public static string RenderCompact(ComponentRoutingResolution route)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== RESOLVED COMPONENT ROUTING ===");
        sb.AppendLine($"observedSurface: {Compact(route.ObservedSurface)}");
        sb.AppendLine($"component/package/module: {Compact(route.Component)} / {Compact(route.PackageOrModule)}");
        sb.AppendLine($"primaryProject: {route.PrimaryProject?.Id ?? "unresolved"} / {route.PrimaryProject?.ShortCode ?? "?"}; repository={Compact(route.Repository)}");
        sb.AppendLine($"consumerProjects/integrationHosts: {string.Join(", ", route.ConsumerProjects.Select(p => $"{p.Id}/{p.ShortCode}").Concat(route.IntegrationHosts))}");
        sb.AppendLine($"releaseArtifact/versioning: {Compact(route.ReleaseArtifact)} / {Compact(route.VersioningMechanism)}");
        sb.AppendLine($"deployment/integration: {string.Join("; ", route.DeploymentSteps)}; environments={string.Join(", ", route.Environments)}");
        sb.AppendLine($"allowedTicketPrefix/storageProject: {Compact(route.AllowedTicketPrefix)} / {Compact(route.StorageProjectId)}");
        sb.AppendLine($"source/evidence: {string.Join("; ", route.Evidence)}");
        sb.AppendLine($"routingConfidence: {route.Confidence:0.00}; unresolvedAlternatives={string.Join("; ", route.UnresolvedAlternatives)}");
        sb.AppendLine("Navigation identifies where feedback was observed. Routing identifies where the fix is owned. Never substitute one for the other.");
        sb.AppendLine(route.RequiresQuestion
            ? "Ask a routing question before proposing or creating a task."
            : $"Routing preview: {route.Preview}");
        return sb.ToString();
    }

    private static ProjectRecord? ResolveProject(IEnumerable<ProjectRecord> projects, string? handle)
        => string.IsNullOrWhiteSpace(handle) ? null : projects.FirstOrDefault(project =>
            string.Equals(project.Id, handle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.ShortCode, handle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.DisplayName, handle, StringComparison.OrdinalIgnoreCase));

    private static double Score(ComponentRoutingRequest request, ComponentOwnershipMapping mapping)
    {
        var component = Normalize(request.Component);
        var surface = Normalize(request.ObservedSurface);
        var declaredComponent = Normalize(mapping.Component + " " + mapping.PackageOrModule);
        if (Tokens(component).Contains("cac") && declaredComponent.Contains("coding agent chat")) return 1;
        if (component.Length > 0 && (declaredComponent.Contains(component) || component.Contains(declaredComponent)))
            return 1;
        var componentOverlap = Tokens(component).Intersect(Tokens(declaredComponent)).Count();
        if (component.Length > 0 && componentOverlap > 0)
            return Math.Min(0.8 + (componentOverlap * 0.05), 0.98);
        if (component.Length > 0) return 0;
        var surfaceScore = mapping.ObservedSurfaces.Select(Normalize).Select(candidate =>
            candidate.Length > 0 && surface.Length > 0 && (surface.Contains(candidate) || candidate.Contains(surface)) ? 0.9 : 0);
        var bestSurfaceScore = surfaceScore.DefaultIfEmpty(0).Max();
        if (bestSurfaceScore > 0) return bestSurfaceScore;
        var overlap = mapping.ObservedSurfaces.Select(candidate => Tokens(Normalize(candidate)).Intersect(Tokens(surface)).Count()).DefaultIfEmpty().Max();
        return overlap >= 3 ? 0.8 : overlap >= 2 ? 0.65 : 0;
    }

    private static readonly HashSet<string> RoutingStopWords = new(
        ["agent", "studio", "component", "components", "project", "shared"],
        StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> Tokens(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token.Length > 2 && !RoutingStopWords.Contains(token))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static string Normalize(string? value) => string.Join(' ', (value ?? "").Trim().ToLowerInvariant()
        .Split(new[] { ' ', '/', '-', '_', '.', ',', ':', ';' }, StringSplitOptions.RemoveEmptyEntries));
    private static string Compact(string? value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();
    private static string BuildPreview(ProjectRecord owner, ComponentOwnershipMapping mapping)
    {
        var integration = mapping.IntegrationHosts.Count > 0
            ? $" integrate/deploy in {string.Join(", ", mapping.IntegrationHosts)}"
            : " complete the declared integration/deployment chain";
        return $"Create {owner.ShortCode} ticket;{integration}.";
    }
}

public sealed record ComponentRoutingRequest(
    string? ObservedSurface = null,
    string? Component = null,
    string? NavigationProjectId = null);

public sealed record RoutingProjectRef(string Id, string ShortCode, string DisplayName);

public sealed record ComponentRoutingResolution(
    string? ObservedSurface,
    string? Component,
    string? PackageOrModule,
    RoutingProjectRef? NavigationProject,
    RoutingProjectRef? PrimaryProject,
    string? Repository,
    IReadOnlyList<RoutingProjectRef> ConsumerProjects,
    IReadOnlyList<string> IntegrationHosts,
    string? ReleaseArtifact,
    string? VersioningMechanism,
    IReadOnlyList<string> DeploymentSteps,
    IReadOnlyList<string> Environments,
    string? AllowedTicketPrefix,
    string? StorageProjectId,
    IReadOnlyList<string> Evidence,
    double Confidence,
    IReadOnlyList<string> UnresolvedAlternatives,
    bool RequiresQuestion,
    string? QuestionReason,
    string Preview,
    string? MappingId,
    int? MappingVersion)
{
    public string? PrimaryProjectId => PrimaryProject?.Id;
    public string? ProjectShortCode => PrimaryProject?.ShortCode;
    public double RoutingConfidence => Confidence;

    public static ComponentRoutingResolution Local(ComponentRoutingRequest request, ProjectRecord project) => new(
        request.ObservedSurface, request.Component, null,
        new(project.Id, project.ShortCode, project.DisplayName),
        new(project.Id, project.ShortCode, project.DisplayName),
        project.DisplayName, [], [], null, null, [], [], project.ShortCode, project.Id,
        ["Explicit destination project; no shared-component mapping matched."],
        0.8, [], false, null, $"Create {project.ShortCode} ticket.", null, null);

    public static ComponentRoutingResolution Question(
        ComponentRoutingRequest request, ProjectRecord? navigation, string reason, IReadOnlyList<string>? alternatives = null) => new(
        request.ObservedSurface, request.Component, null,
        navigation == null ? null : new(navigation.Id, navigation.ShortCode, navigation.DisplayName),
        null, null, [], [], null, null, [], [], null, null, [], 0,
        alternatives ?? [], true, reason, "Resolve ownership before creating a ticket.", null, null);
}
