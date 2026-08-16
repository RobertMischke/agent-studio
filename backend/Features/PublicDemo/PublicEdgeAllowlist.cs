using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.PublicDemo;

/// <summary>
/// The committed public-demo read inventory. This list is the whole visitor
/// surface: anything absent is denied by default, so a route added tomorrow is
/// unreachable from the public demo until someone puts it here in a reviewed
/// change.
///
/// <para>
/// Entries are route templates exactly as registered by
/// <c>EndpointMapping.MapAllEndpoints</c>. <see cref="PublicEdgeInventoryGuardTests"/>
/// resolves every entry against the live endpoint table, so a renamed or
/// removed route fails the build instead of silently shrinking the demo.
/// </para>
///
/// <para>
/// Deliberately absent, even though the routes exist: authentication and client
/// registration, management, diagnostics, devtools, filesystem layer, CLI and
/// quota probes, prompts and admin config, Git history and repository file
/// reads, drift and analysis prompt builders, orchestrator and project chat,
/// supervisor, publish, and test runs. Those are either execution-adjacent,
/// operator-only, or they read a real repository.
/// </para>
/// </summary>
public static class PublicEdgeAllowlist
{
    private const bool Sandboxed = true;

    /// <summary>
    /// Product surface, board, evidence, Wiki, Dossiers, and search. Reads only.
    /// </summary>
    public static readonly IReadOnlyList<PublicEdgeRoute> Routes =
    [
        // The edge contract itself. The Angular shell reads it to enter read-only
        // mode; it is public by construction and carries no secret.
        new("GET", "/api/public-demo/edge"),

        // Product identity and the bootstrap flags the Angular shell reads
        // before it renders, including the read-only projection of this contract.
        new("GET", "/api/environment"),
        // Names the active profile so the shell knows the visitor surface is
        // anonymous and skips the sign-in gate. It exposes no user and no
        // credential: the rest of /api/auth stays denied.
        new("GET", "/api/auth/status"),
        new("GET", "/api/system/about"),
        new("GET", "/api/system/version"),

        // Projects and board.
        new("GET", "/api/projects"),
        new("GET", "/api/projects/{projectName}/snapshot"),
        new("GET", "/api/projects/{projectName}/graph"),
        new("GET", "/api/projects/{projectName}/throughput"),
        new("GET", "/api/projects/{projectName}/visual-evidence"),
        new("GET", "/api/projects/{projectName}/deployment/summary"),
        new("GET", "/api/projects/{projectName}/token-usage/summary"),
        new("GET", "/api/tasks"),
        new("GET", "/api/tasks/grouped"),
        new("GET", "/api/tasks/archive"),
        new("GET", "/api/tags/"),

        // One card: metadata, run history, and the recorded execution story.
        new("GET", "/api/tasks/{jobId}"),
        new("GET", "/api/tasks/{jobId}/timeline"),
        new("GET", "/api/tasks/{jobId}/pipeline"),
        new("GET", "/api/tasks/{jobId}/plan"),
        new("GET", "/api/tasks/{jobId}/provenance"),
        new("GET", "/api/tasks/{jobId}/runs"),
        new("GET", "/api/tasks/{jobId}/output"),
        new("GET", "/api/tasks/{jobId}/session-events"),
        new("GET", "/api/tasks/{jobId}/agent-work-summary"),
        new("GET", "/api/tasks/{jobId}/conversation"),

        // Review evidence. Result documents and artifacts may carry seeded HTML,
        // so they are served under the sandboxing policy.
        new("GET", "/api/tasks/{jobId}/artifacts"),
        new("GET", "/api/tasks/{jobId}/screenshots"),
        new("GET", "/api/tasks/{jobId}/screenshot"),
        new("GET", "/api/tasks/{jobId}/thumbnail"),
        new("GET", "/api/tasks/{jobId}/results/{**path}", Sandboxed),

        // Wiki.
        new("GET", "/api/projects/{projectName}/wiki"),
        new("GET", "/api/projects/{projectName}/wiki/home"),
        new("GET", "/api/projects/{projectName}/wiki/tree"),
        new("GET", "/api/projects/{projectName}/wiki/recent"),
        new("GET", "/api/projects/{projectName}/wiki/search"),
        new("GET", "/api/projects/{projectName}/wiki/folder/{**relPath}"),
        new("GET", "/api/projects/{projectName}/wiki/files/{**relPath}", Sandboxed),
        new("GET", "/api/projects/{projectName}/wiki/assets/{**relPath}", Sandboxed),

        // Dossier gallery.
        new("GET", "/api/workbenches"),
        new("GET", "/api/projects/{projectName}/workbenches"),
        new("GET", "/api/projects/{projectName}/workbenches/{id}"),
        new("GET", "/api/projects/{projectName}/workbenches/{key}/references"),

        // Cross-project search over the seeded scene.
        new("GET", "/api/search"),
    ];

    /// <summary>
    /// Stable content digest of the inventory. It goes into the edge contract and
    /// the release manifest so a changed visitor surface is a visible artifact
    /// change, not an invisible deployment drift.
    /// </summary>
    public static string Digest(IReadOnlyList<PublicEdgeRoute> routes)
    {
        var canonical = string.Join(
            '\n',
            routes.Select(route => $"{route.Method.ToUpperInvariant()} {route.Template}").Order(StringComparer.Ordinal));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
