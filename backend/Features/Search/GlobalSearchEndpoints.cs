namespace AgentStudio.Search;

public static class GlobalSearchEndpoints
{
    private static readonly HashSet<string> AllowedDomains = new(StringComparer.OrdinalIgnoreCase) { "tasks", "commits", "files" };

    public static void MapGlobalSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search", (string? q, string? domains, int? limit, GlobalSearchService search) =>
        {
            var query = q?.Trim() ?? "";
            var selected = string.IsNullOrWhiteSpace(domains)
                ? new HashSet<string>(AllowedDomains, StringComparer.OrdinalIgnoreCase)
                : domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(AllowedDomains.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (query.Length < 2)
                return Results.Ok(new GlobalSearchResponse(query, [], [], [], new Dictionary<string, string>(), 0));
            return Results.Ok(search.Search(query, selected, limit ?? 20));
        });
    }
}
