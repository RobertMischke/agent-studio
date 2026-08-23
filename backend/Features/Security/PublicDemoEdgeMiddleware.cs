using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Security;

/// <summary>
/// The public-demo browser edge (W34 dossier §8 S4, depends on the §8 S2
/// route inventory). No-ops entirely outside the <c>public-demo-readonly</c>
/// profile. Runs after <see cref="PublicDemoExecutionLockMiddleware"/>, so a
/// route S2 already denies by identity keeps its specific
/// <c>execution-disabled</c> code; this middleware is the broader net behind
/// it - every remaining unsafe method is denied outright, and every safe
/// method must match an explicit read allowlist. It also carries the edge's
/// other visitor-facing duties: same-origin TLS, hardening response headers,
/// the ephemeral viewer cookie, and an Origin check for cross-site callers.
/// </summary>
public sealed class PublicDemoEdgeMiddleware(
    RequestDelegate next,
    StartupExecutionAdmission admission,
    PublicDemoViewerSessionStore viewerSessions)
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-src 'self'; " +
        "frame-ancestors 'none'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!admission.IsPublicDemo)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        if (IsHealthCheck(path))
        {
            await next(context);
            return;
        }

        context.Response.OnStarting(() =>
        {
            ApplySecurityHeaders(context.Response);
            return Task.CompletedTask;
        });

        if (!context.Request.IsHttps)
        {
            await Deny(context, StatusCodes.Status426UpgradeRequired, "https-required", "The public demo requires HTTPS.");
            return;
        }

        if (!OriginMatchesHost(context.Request))
        {
            await Deny(
                context,
                StatusCodes.Status403Forbidden,
                PublicDemoEdgePolicy.ReadOnlyDeniedCode,
                "Cross-origin requests are not accepted by the public demo edge.");
            return;
        }

        EnsureViewerSession(context);

        var isHub = path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase);
        if (isHub)
        {
            // SignalR's own handshake needs POST (negotiate) alongside GET
            // (the WebSocket upgrade); the connection-level boundary lives in
            // TaskHub.OnConnectedAsync (viewer-session + project-group scope),
            // not in a blanket method check here.
            if (!PublicDemoReadAllowlist.Allows(path))
            {
                await Deny(
                    context,
                    StatusCodes.Status403Forbidden,
                    PublicDemoEdgePolicy.ReadOnlyDeniedCode,
                    "This hub route is not on the public demo allowlist.");
                return;
            }
            await next(context);
            return;
        }

        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            var decision = PublicDemoEdgePolicy.Decide(
                admission.StartupProfile,
                context.Request.Method,
                PublicDemoReadAllowlist.Allows(path));
            if (!decision.Allowed)
            {
                await Deny(context, StatusCodes.Status403Forbidden, decision.Code, decision.Message);
                return;
            }
        }

        await next(context);
    }

    private void EnsureViewerSession(HttpContext context)
    {
        var existing = context.Request.Cookies[PublicDemoViewerSessionStore.CookieName];
        if (viewerSessions.Touch(existing)) return;

        var issued = viewerSessions.Issue();
        context.Response.Cookies.Append(PublicDemoViewerSessionStore.CookieName, issued, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            MaxAge = viewerSessions.SessionLifetime,
            Path = "/",
        });
    }

    private static void ApplySecurityHeaders(HttpResponse response)
    {
        var headers = response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=(), payment=()";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
    }

    private static bool OriginMatchesHost(HttpRequest request)
    {
        var origin = request.Headers.Origin.FirstOrDefault();
        // No Origin header: a same-site top-level navigation or a simple GET
        // that browsers do not attach one to. Nothing to verify - the safe
        // methods it can carry stay bounded by the read allowlist below.
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
               && string.Equals(originUri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHealthCheck(string path)
        => path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/healthz/drain", StringComparison.OrdinalIgnoreCase);

    private static async Task Deny(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new
        {
            error = code,
            message,
            profile = ExecutionAdmissionPolicy.PublicDemoProfile,
        });
    }
}

public static class PublicDemoEdgeMiddlewareExtensions
{
    public static IApplicationBuilder UsePublicDemoReadOnlyEdge(this IApplicationBuilder app)
        => app.UseMiddleware<PublicDemoEdgeMiddleware>();
}
