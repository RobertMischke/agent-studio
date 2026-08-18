namespace AgentStudio.Search;

public static class GlobalSearchEndpoints
{
    private static readonly HashSet<string> AllowedDomains = new(StringComparer.OrdinalIgnoreCase) { "tasks", "commits", "files" };

    public static void MapGlobalSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search", (string? q, string? domains, int? limit, HttpContext context,
            GlobalSearchService search, AgentStudio.Registry.ProjectRegistry projects,
            IConfiguration configuration, Microsoft.Extensions.Options.IOptions<PublicDemoOptions> publicDemo) =>
        {
            var query = q?.Trim() ?? "";
            var selected = string.IsNullOrWhiteSpace(domains)
                ? new HashSet<string>(AllowedDomains, StringComparer.OrdinalIgnoreCase)
                : domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(AllowedDomains.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (query.Length < 2)
                return Results.Ok(new GlobalSearchResponse(query, [], [], [], new Dictionary<string, string>(), 0));
            var response = search.Search(query, selected, limit ?? 20);

            // Public demo: the same project filter /api/projects and the hub
            // apply, so a mis-seeded or drifted store cannot surface a project
            // the demo never announced through search either.
            if (SecurityProfiles.IsPublicDemo(configuration))
            {
                var announced = publicDemo.Value.Projects;
                // A search item's ProjectName may be an id, a display name, or a
                // watch-path folder name depending on the domain. Resolve it to the
                // registry's canonical id first, the same way the hub and
                // /api/projects do, so the comparison against the configured ids
                // is not fooled by a display-name spelling.
                bool DemoAllowed(GlobalSearchItem item) => PublicDemoProjectScope.Allows(
                    announced,
                    projects.FindByIdOrDisplayName(item.ProjectName)?.Id ?? item.ProjectName);
                return Results.Ok(response with
                {
                    Tasks = response.Tasks.Where(DemoAllowed).ToList(),
                    Commits = response.Commits.Where(DemoAllowed).ToList(),
                    Files = response.Files.Where(DemoAllowed).ToList(),
                });
            }

            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human)
                return Results.Ok(response);
            bool Allowed(GlobalSearchItem item) => ProjectAccessAuthorization.Allows(human.User, item.ProjectName, projects);
            return Results.Ok(response with
            {
                Tasks = response.Tasks.Where(Allowed).ToList(),
                Commits = response.Commits.Where(Allowed).ToList(),
                Files = response.Files.Where(Allowed).ToList(),
            });
        });
    }
}
