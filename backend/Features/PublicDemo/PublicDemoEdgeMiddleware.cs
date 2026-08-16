using System.Security.Cryptography;

using Microsoft.AspNetCore.Http.Features;

namespace AgentStudio.PublicDemo;

/// <summary>
/// The public read-only edge (W34 S4). Ordered as boundary validation, pure
/// decision, then bounded side effects: it normalizes the request, asks
/// <see cref="PublicEdgePolicy"/>, and only then touches the response with
/// headers, the ephemeral viewer cookie, and the typed denial body.
///
/// <para>
/// It runs before <see cref="AgentStudio.Security.AccessSecurityMiddleware"/> so
/// an unauthenticated visitor is judged by the read allowlist rather than by the
/// operator credential model. It is not the execution boundary: the hard server
/// lock refuses claims, starts, continuations, chat, previews, and post-steps
/// independently, and would still refuse them if this layer were removed.
/// </para>
/// </summary>
public sealed class PublicDemoEdgeMiddleware(RequestDelegate next)
{
    /// <summary>Ephemeral viewer identity. Public by design; it authorizes nothing and carries no secret.</summary>
    public const string ViewerCookieName = "__Host-demo-viewer";

    /// <summary>Request item carrying the resolved viewer identity for downstream code.</summary>
    public const string ViewerIdItem = "PublicDemo.ViewerId";

    public async Task InvokeAsync(
        HttpContext context,
        IConfiguration configuration,
        PublicEdgeContract contract,
        PublicEdgeRateLimiter limiter,
        PublicDemoProjectScope scope,
        TaskScannerService? scanner = null)
    {
        if (!PublicDemoProfile.IsActive(configuration))
        {
            await next(context);
            return;
        }

        var path = Normalize(context.Request.Path.Value);
        var route = PublicEdgePolicy.Match(context.Request.Method, path, contract.Routes);
        PublicEdgeSecurityHeaders.Apply(context.Response, route?.Sandboxed == true);

        // A declared Content-Length is refused by the policy below, but a chunked
        // body declares nothing. Clamp the server-side ceiling too, so an
        // unbounded upload to a read route cannot spend more than the contract
        // allows before Kestrel aborts it.
        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false }) bodySizeFeature.MaxRequestBodySize = contract.MaxRequestBodyBytes;

        // Resolving a project handle needs the registry, so it happens here and
        // the pure decision receives a settled answer.
        var addressed = PublicDemoProjectScope.AddressedProject(
            path,
            context.Request.Query["project"].FirstOrDefault(),
            taskId => scanner?.FindJob(taskId, context.Request.Query["watchPath"].FirstOrDefault())?.ProjectName);

        var request = new PublicEdgeRequest(
            context.Request.Method,
            path,
            context.Request.IsHttps,
            context.Request.Headers.Origin.FirstOrDefault(),
            context.Request.Host.Value,
            context.Request.ContentLength,
            addressed is null ? null : scope.Allows(addressed));

        var decision = PublicEdgePolicy.Decide(request, contract);
        if (decision.Denial is { } denial)
        {
            await Deny(context, denial);
            return;
        }

        var viewerId = ResolveViewer(context, contract);
        context.Items[ViewerIdItem] = viewerId;
        if (!limiter.Admit(viewerId))
        {
            context.Response.Headers.RetryAfter = ((int)contract.Window.TotalSeconds).ToString();
            await Deny(context, PublicEdgeDenial.RateLimited);
            return;
        }

        await next(context);
    }

    /// <summary>
    /// Read the visitor's ephemeral session or mint one. The value is opaque
    /// random data with a short lifetime; it exists to bound cost per browser,
    /// not to prove anything about the caller.
    /// </summary>
    private static string ResolveViewer(HttpContext context, PublicEdgeContract contract)
    {
        var existing = context.Request.Cookies[ViewerCookieName];
        if (!string.IsNullOrWhiteSpace(existing) && existing.Length is > 8 and <= 64) return existing;

        var minted = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        context.Response.Cookies.Append(ViewerCookieName, minted, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = contract.ViewerSessionLifetime,
        });
        return minted;
    }

    /// <summary>
    /// Write the typed denial. The body carries a stable code and one neutral
    /// sentence: no route hint, no upstream message, no stack, nothing that
    /// helps a caller map the private surface.
    /// </summary>
    private static async Task Deny(HttpContext context, PublicEdgeDenial denial)
    {
        context.Response.StatusCode = denial.Status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new
        {
            error = denial.Code,
            message = denial.Message,
            profile = PublicDemoProfile.ProfileName,
        });
    }

    private static string Normalize(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/" : value;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }
}

/// <summary>
/// Transport and browser hardening for the public demo. Seeded Wiki, Dossier,
/// and evidence HTML is served under a stricter sandboxing policy so a document
/// in the datastore cannot execute script or reach a remote origin.
/// </summary>
public static class PublicEdgeSecurityHeaders
{
    public const string BasePolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; media-src 'self'; "
        + "object-src 'none'; frame-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'none'";

    public const string SandboxPolicy =
        "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; "
        + "object-src 'none'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'; sandbox";

    public static void Apply(HttpResponse response, bool sandboxed)
    {
        response.Headers["Content-Security-Policy"] = sandboxed ? SandboxPolicy : BasePolicy;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = sandboxed ? "SAMEORIGIN" : "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        response.Headers["Permissions-Policy"] =
            "camera=(), microphone=(), geolocation=(), payment=(), usb=(), interest-cohort=()";
    }
}

public static class PublicDemoEdgeMiddlewareExtensions
{
    public static IApplicationBuilder UsePublicDemoEdge(this IApplicationBuilder app)
        => app.UseMiddleware<PublicDemoEdgeMiddleware>();
}
