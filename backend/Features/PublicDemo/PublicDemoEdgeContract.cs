namespace AgentStudio.PublicDemo;

/// <summary>
/// W34 S4 (public read-only edge). The public demo runs the real Task Server
/// behind a startup-locked visitor boundary: an explicit read allowlist, an
/// ephemeral viewer session, project-filtered events, transport hardening,
/// and rate/body ceilings.
///
/// <para>
/// This is the <b>second</b> barrier, never the first. The hard execution lock
/// (slice S2) denies claims, starts, continuations, chat, previews, and
/// post-steps inside the server regardless of what reaches it. The edge exists
/// so a raw client that bypasses Angular still sees a typed denial instead of a
/// reachable mutation or diagnostic surface.
/// </para>
/// </summary>
public static class PublicDemoProfile
{
    /// <summary>Value of <c>Security:Profile</c> that arms the public read-only edge.</summary>
    public const string ProfileName = "public-demo";

    /// <summary>
    /// Startup-only profile identity. It is read from configuration, never from a
    /// project setting, a management command, or a browser-controlled header, so
    /// there is no runtime toggle that can widen the visitor surface.
    /// </summary>
    public static bool IsActive(IConfiguration configuration)
        => string.Equals(configuration["Security:Profile"], ProfileName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One allowlisted read route, expressed as the registered ASP.NET route template.</summary>
/// <param name="Method">HTTP method. Only safe methods and declared read-only POSTs are legal.</param>
/// <param name="Template">The route template exactly as registered (for example <c>/api/tasks/{jobId}/timeline</c>).</param>
/// <param name="Sandboxed">
/// The response may carry seeded HTML (Wiki page, Dossier document, task evidence).
/// Those responses receive the sandboxing content-security policy.
/// </param>
public sealed record PublicEdgeRoute(string Method, string Template, bool Sandboxed = false);

/// <summary>
/// The immutable visitor contract. Built once at startup from configuration and
/// the committed allowlist; every field is a ceiling, never a default that a
/// request can raise.
/// </summary>
public sealed record PublicEdgeContract
{
    public required IReadOnlyList<PublicEdgeRoute> Routes { get; init; }

    /// <summary>Project handles the visitor may read. Everything else is out of scene.</summary>
    public required IReadOnlyList<string> Projects { get; init; }

    public required long MaxRequestBodyBytes { get; init; }
    public required int RequestsPerWindow { get; init; }
    public required TimeSpan Window { get; init; }
    public required TimeSpan ViewerSessionLifetime { get; init; }

    /// <summary>Stable digest of the allowlist, surfaced to the release manifest and the UI.</summary>
    public required string AllowlistDigest { get; init; }
}

/// <summary>
/// A typed denial. The public demo answers a refused request with a stable
/// machine-readable code and a short sentence, never with a stack trace, a
/// filesystem path, an upstream message, or a route hint.
/// </summary>
/// <param name="Status">HTTP status to write.</param>
/// <param name="Code">Stable denial code, prefixed <c>public-demo-</c>.</param>
/// <param name="Message">One neutral English sentence for the visitor.</param>
public sealed record PublicEdgeDenial(int Status, string Code, string Message)
{
    public static readonly PublicEdgeDenial HttpsRequired =
        new(426, "public-demo-https-required", "The public demo is served over HTTPS only.");

    public static readonly PublicEdgeDenial OriginDenied =
        new(403, "public-demo-origin-denied", "The public demo accepts same-origin requests only.");

    public static readonly PublicEdgeDenial MethodDenied =
        new(403, "public-demo-read-only", "The public demo is read-only. This method is not available.");

    public static readonly PublicEdgeDenial RouteDenied =
        new(403, "public-demo-route-denied", "This endpoint is not part of the public demo surface.");

    public static readonly PublicEdgeDenial ProjectDenied =
        new(403, "public-demo-project-denied", "Only the seeded demo projects are readable here.");

    public static readonly PublicEdgeDenial BodyTooLarge =
        new(413, "public-demo-body-too-large", "The request body exceeds the public demo limit.");

    public static readonly PublicEdgeDenial RateLimited =
        new(429, "public-demo-rate-limited", "Too many requests. Please slow down.");
}

/// <summary>Outcome of the pure edge decision. Exactly one of the two states is meaningful.</summary>
public readonly record struct PublicEdgeDecision(PublicEdgeDenial? Denial)
{
    public static PublicEdgeDecision Allow() => new((PublicEdgeDenial?)null);
    public static PublicEdgeDecision Deny(PublicEdgeDenial denial) => new(denial);
    public bool IsAllowed => Denial is null;
}

/// <summary>
/// The normalized request facts the edge decision needs. Keeping this a plain
/// value keeps <see cref="PublicEdgePolicy"/> pure and directly matrix-testable
/// without an HTTP context.
/// </summary>
/// <param name="Method">Uppercase HTTP method.</param>
/// <param name="Path">Absolute request path, trailing slash trimmed.</param>
/// <param name="IsHttps">Whether the request reached the server over TLS.</param>
/// <param name="Origin">Value of the <c>Origin</c> header, or null.</param>
/// <param name="Host">Value of the <c>Host</c> header, or null.</param>
/// <param name="ContentLength">Declared body length, or null when absent.</param>
/// <param name="ProjectAllowed">
/// Whether the addressed project is inside the seeded scene, or null when the
/// route is not project-addressed. Resolving a handle needs the project
/// registry, so the caller settles it through
/// <see cref="PublicDemoProjectScope"/> and hands the decision a plain answer.
/// </param>
public readonly record struct PublicEdgeRequest(
    string Method,
    string Path,
    bool IsHttps,
    string? Origin,
    string? Host,
    long? ContentLength,
    bool? ProjectAllowed);
