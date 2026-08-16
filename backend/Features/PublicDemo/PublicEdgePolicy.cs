namespace AgentStudio.PublicDemo;

/// <summary>
/// The pure public-demo edge decision. It takes normalized request facts and the
/// startup contract and returns either allow or one typed denial. No HTTP
/// context, no clock, no storage, so the whole visitor boundary is a direct
/// matrix test.
///
/// <para>
/// Order is deliberate and is itself part of the contract: transport first,
/// then origin, then method, then the endpoint allowlist, then the body
/// ceiling, then project scope. A raw unsafe request is refused before the
/// allowlist is consulted, so probing for route names through method errors
/// tells an attacker nothing.
/// </para>
/// </summary>
public static class PublicEdgePolicy
{
    /// <summary>Paths the edge never inspects: liveness probes must answer before any policy.</summary>
    private static readonly string[] HealthPaths = ["/healthz", "/healthz/drain"];

    /// <summary>The SignalR path. Its own authorization lives in the hub; the edge only fixes transport and origin.</summary>
    public const string HubPath = "/hubs/jobs";

    public static PublicEdgeDecision Decide(in PublicEdgeRequest request, PublicEdgeContract contract)
    {
        if (HealthPaths.Contains(request.Path, StringComparer.OrdinalIgnoreCase))
            return PublicEdgeDecision.Allow();

        if (!request.IsHttps)
            return PublicEdgeDecision.Deny(PublicEdgeDenial.HttpsRequired);

        // Same-origin only. A browser sends Origin on cross-origin reads and on
        // every WebSocket handshake; a mismatch means the request was issued by
        // some other site, so it is refused before anything else is evaluated.
        if (!IsSameOrigin(request.Origin, request.Host))
            return PublicEdgeDecision.Deny(PublicEdgeDenial.OriginDenied);

        // The hub is read transport, not a mutation surface: SignalR negotiates
        // with POST and long polling ends a connection with DELETE, so the
        // method rule below cannot apply to it. The hub exposes exactly two
        // callable methods, both of which only join or leave a read group, and
        // the per-project filter is enforced inside the hub where the
        // connection's groups are decided.
        if (IsHubPath(request.Path))
            return IsHubTransportMethod(request.Method)
                ? PublicEdgeDecision.Allow()
                : PublicEdgeDecision.Deny(PublicEdgeDenial.MethodDenied);

        if (!IsSafeMethod(request.Method))
            return PublicEdgeDecision.Deny(PublicEdgeDenial.MethodDenied);

        var route = Match(request.Method, request.Path, contract.Routes);
        if (route is null)
            return PublicEdgeDecision.Deny(PublicEdgeDenial.RouteDenied);

        if (request.ContentLength > contract.MaxRequestBodyBytes)
            return PublicEdgeDecision.Deny(PublicEdgeDenial.BodyTooLarge);

        if (request.ProjectAllowed == false)
            return PublicEdgeDecision.Deny(PublicEdgeDenial.ProjectDenied);

        return PublicEdgeDecision.Allow();
    }

    /// <summary>
    /// Safe methods only. HEAD and OPTIONS are answered because a browser needs
    /// them; every state-changing verb is denied at the edge even though the
    /// execution lock would deny it again behind us.
    /// </summary>
    public static bool IsSafeMethod(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    public static bool IsHubPath(string path)
        => path.Equals(HubPath, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(HubPath + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The methods SignalR's transports need: GET for the WebSocket upgrade and
    /// server-sent events, POST for negotiate and long-poll sends, DELETE to end
    /// a long-poll connection. Everything else is refused.
    /// </summary>
    public static bool IsHubTransportMethod(string method)
        => IsSafeMethod(method) || HttpMethods.IsPost(method) || HttpMethods.IsDelete(method);

    /// <summary>
    /// Absent Origin is treated as same-origin: non-browser clients and ordinary
    /// top-level navigations do not send it, and refusing them would break the
    /// demo without adding a boundary a browser respects.
    /// </summary>
    public static bool IsSameOrigin(string? origin, string? host)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (origin.Equals("null", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(host)) return false;
        return Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
               && string.Equals(parsed.Authority, host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve a concrete request path against the allowlist templates. Returns
    /// the matched route so the caller can apply sandboxing to seeded HTML.
    /// </summary>
    public static PublicEdgeRoute? Match(string method, string path, IReadOnlyList<PublicEdgeRoute> routes)
    {
        // HEAD and OPTIONS are answered from the GET inventory: they expose no
        // payload the corresponding GET would not already expose.
        var lookup = HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) ? HttpMethods.Get : method;
        var segments = Split(path);
        foreach (var route in routes)
        {
            if (!string.Equals(route.Method, lookup, StringComparison.OrdinalIgnoreCase)) continue;
            if (Matches(Split(route.Template), segments)) return route;
        }

        return null;
    }

    private static string[] Split(string value)
        => value.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool Matches(string[] template, string[] path)
    {
        for (var i = 0; i < template.Length; i++)
        {
            var segment = template[i];
            // A catch-all ({**rest}) consumes the remainder and must match at
            // least one segment, mirroring how the route table binds it.
            if (segment.StartsWith("{**", StringComparison.Ordinal))
                return path.Length > i;
            if (i >= path.Length) return false;
            if (segment.StartsWith('{') && segment.EndsWith('}')) continue;
            if (!segment.Equals(path[i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return template.Length == path.Length;
    }
}
