namespace AgentStudio.PublicDemo;

/// <summary>Outcome of the public-demo edge decision.</summary>
public enum PublicDemoOutcome
{
    Allow,
    Deny,
}

/// <summary>Everything the edge policy is allowed to look at. No ambient state.</summary>
public readonly record struct PublicDemoRequest(
    string Method,
    string Path,
    bool IsHttps,
    bool SameOrigin,
    long? ContentLength);

/// <summary>Numeric ceilings the policy enforces.</summary>
public readonly record struct PublicDemoLimits(long MaxRequestBodyBytes);

/// <summary>
/// The typed denial the edge returns. <see cref="Error"/> is the stable machine
/// code; <see cref="Message"/> is deliberately generic so a probe learns nothing
/// about the route table beyond "denied".
/// </summary>
public readonly record struct PublicDemoVerdict(
    PublicDemoOutcome Outcome,
    int Status,
    string Error,
    string Message)
{
    public static readonly PublicDemoVerdict Allow = new(PublicDemoOutcome.Allow, 0, string.Empty, string.Empty);

    public bool Denied => Outcome == PublicDemoOutcome.Deny;

    private static PublicDemoVerdict Deny(int status, string error, string message)
        => new(PublicDemoOutcome.Deny, status, error, message);

    public static PublicDemoVerdict HttpsRequired => Deny(
        426, "public-demo-https-required", "The public demo is served over HTTPS only.");

    public static PublicDemoVerdict CrossOrigin => Deny(
        403, "public-demo-cross-origin-denied", "The public demo accepts same-origin requests only.");

    public static PublicDemoVerdict ReadOnly => Deny(
        403, "public-demo-read-only", "The public demo is read-only. This request was not executed.");

    public static PublicDemoVerdict BodyTooLarge => Deny(
        413, "public-demo-body-too-large", "The request body exceeds the public demo limit.");

    public static PublicDemoVerdict EndpointDenied => Deny(
        404, "public-demo-endpoint-denied", "This endpoint is not part of the public demo surface.");

    public static PublicDemoVerdict RateLimited => Deny(
        429, "public-demo-rate-limited", "Too many requests. Slow down and retry shortly.");
}

/// <summary>
/// The pure admission decision for the public read-only demo edge (dossier
/// AGT-W34 slice S4). It is deny-by-default: a request is admitted only when it
/// is same-origin, over TLS, uses a safe method, stays inside the body ceiling,
/// and matches an entry of the explicit allowlist in
/// <see cref="PublicDemoRoutes"/>.
///
/// Rate limiting is deliberately not part of this function: it needs a clock and
/// per-viewer counters, so <see cref="PublicDemoRequestBudget"/> owns it and the
/// middleware applies both in order.
/// </summary>
public static class PublicDemoEdgePolicy
{
    public static PublicDemoVerdict Evaluate(PublicDemoRequest request, PublicDemoLimits limits)
    {
        var path = PublicDemoRoutes.Normalize(request.Path);

        // Health probes answer the load balancer, not the visitor. They carry no
        // demo data and must survive every other rule so an unhealthy edge is
        // still observable.
        if (PublicDemoRoutes.IsHealth(path)) return PublicDemoVerdict.Allow;

        if (!request.IsHttps) return PublicDemoVerdict.HttpsRequired;
        if (!request.SameOrigin) return PublicDemoVerdict.CrossOrigin;

        // The read-only verdict comes before the allowlist on purpose. A raw
        // POST/PUT/PATCH/DELETE against any path - known, unknown, or forged -
        // gets the one denial that names the launch invariant.
        if (!IsAdmissibleMethod(request.Method, path)) return PublicDemoVerdict.ReadOnly;

        if (request.ContentLength is { } length && length > limits.MaxRequestBodyBytes)
            return PublicDemoVerdict.BodyTooLarge;

        return PublicDemoRoutes.IsAllowed(path)
            ? PublicDemoVerdict.Allow
            : PublicDemoVerdict.EndpointDenied;
    }

    private static bool IsAdmissibleMethod(string method, string normalizedPath)
        => HttpMethods.IsGet(method)
           || HttpMethods.IsHead(method)
           || HttpMethods.IsOptions(method)
           // SignalR opens its stream with a POST to /negotiate. It is the one
           // unsafe verb the read-only edge admits, and only on that exact path.
           || (HttpMethods.IsPost(method) && PublicDemoRoutes.IsHubNegotiate(normalizedPath));
}
