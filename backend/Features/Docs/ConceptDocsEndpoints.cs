
namespace AgentStudio.Docs;

/// <summary>
/// Read-only routes for the in-product concept-docs (the markdown
/// committed under <c>docs/concept-docs/</c>). The FE renders the body
/// in the <c>&lt;app-info-button&gt;</c> side-drawer next to surfaces
/// whose behaviour is non-obvious (e.g. the 4-auto-review and
/// 3-progress lane headers).
///
/// The endpoint is intentionally open (no <c>X-Client-Id</c>) - it
/// serves committed documentation, not user data.
/// </summary>
public static class ConceptDocsEndpoints
{
    public static void MapConceptDocsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/concept-docs/{topic}", (string topic, ConceptDocsService docs) =>
        {
            var entry = docs.Get(topic);
            return entry == null
                ? Results.NotFound(new { error = $"Unknown concept-doc topic '{topic}'" })
                : Results.Ok(entry);
        });
    }
}
