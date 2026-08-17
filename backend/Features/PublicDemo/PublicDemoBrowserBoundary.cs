namespace AgentStudio.PublicDemo;

/// <summary>
/// The browser-facing half of the S4 boundary: same-origin admission and the two
/// content-security policies. Both are pure so the matrix is testable without a
/// host, a socket, or a browser.
/// </summary>
public static class PublicDemoBrowserBoundary
{
    /// <summary>
    /// Policy for the application shell and its JSON API. Self-only everywhere,
    /// no framing, no form posts, no base-tag rewrite, no remote embeds.
    /// </summary>
    public const string ApplicationCsp =
        "default-src 'self'; "
        + "base-uri 'none'; "
        + "object-src 'none'; "
        + "frame-src 'none'; "
        + "frame-ancestors 'none'; "
        + "form-action 'none'; "
        + "script-src 'self'; "
        + "style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data: blob:; "
        + "font-src 'self'; "
        + "connect-src 'self'; "
        + "media-src 'self'; "
        + "worker-src 'self' blob:; "
        + "manifest-src 'self'";

    /// <summary>
    /// Policy for seeded document content (Wiki pages, Dossiers, evidence files,
    /// screenshots). The scrub gate in S5 is the primary defense; this is the
    /// second one. <c>sandbox</c> with no allow-token drops scripts, forms,
    /// popups, plugins, and same-origin authority for that response, so a script
    /// fragment that survives derivation still cannot read the demo's cookies or
    /// call the API.
    /// </summary>
    public const string SeededDocumentCsp =
        "sandbox; "
        + "default-src 'none'; "
        + "base-uri 'none'; "
        + "object-src 'none'; "
        + "frame-ancestors 'none'; "
        + "form-action 'none'; "
        + "img-src 'self' data:; "
        + "style-src 'unsafe-inline'";

    public static string ContentSecurityPolicyFor(string normalizedPath)
        => PublicDemoRoutes.IsSeededDocument(normalizedPath) ? SeededDocumentCsp : ApplicationCsp;

    /// <summary>
    /// Same-origin admission for both HTTP and the WebSocket upgrade. A request
    /// without an <c>Origin</c> header is a direct navigation or a non-browser
    /// client and is judged by the rest of the policy; a request that carries one
    /// must match the host the demo is served from, scheme included.
    /// </summary>
    public static bool IsSameOrigin(string? origin, string? requestScheme, string? requestHost)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (origin.Equals("null", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(requestHost)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed)) return false;

        var expected = $"{requestScheme}://{requestHost}";
        return string.Equals(
            parsed.GetLeftPart(UriPartial.Authority),
            expected,
            StringComparison.OrdinalIgnoreCase);
    }
}
