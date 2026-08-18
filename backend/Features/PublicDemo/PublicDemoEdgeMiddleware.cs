using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace AgentStudio.PublicDemo;

/// <summary>
/// The public read-only demo edge (dossier AGT-W34 slice S4). It runs ahead of
/// every other boundary so a denied request never reaches authentication,
/// routing, or a handler.
///
/// Flow order follows the backend guide: boundary validation (TLS, origin,
/// method, body, allowlist), then coordination (viewer session, request budget),
/// then the bounded side effects (security headers, typed denial or pass-through).
/// The admission decision itself is pure and lives in
/// <see cref="PublicDemoEdgePolicy"/>.
/// </summary>
public sealed class PublicDemoEdgeMiddleware(
    RequestDelegate next,
    IOptions<PublicDemoOptions> options,
    PublicDemoViewerSessions viewers,
    PublicDemoRequestBudget budget)
{
    public const string ViewerIdItem = "PublicDemo.ViewerId";

    private readonly PublicDemoOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = PublicDemoRoutes.Normalize(context.Request.Path.Value);
        ApplyBrowserBoundary(context, path);

        var verdict = PublicDemoEdgePolicy.Evaluate(
            new PublicDemoRequest(
                context.Request.Method,
                path,
                context.Request.IsHttps,
                PublicDemoBrowserBoundary.IsSameOrigin(
                    context.Request.Headers.Origin.FirstOrDefault(),
                    context.Request.Scheme,
                    context.Request.Host.Value),
                context.Request.ContentLength),
            _options.ToLimits());

        if (verdict.Denied)
        {
            await Deny(context, verdict);
            return;
        }

        if (PublicDemoRoutes.IsHealth(path))
        {
            await next(context);
            return;
        }

        var viewerId = viewers.Resolve(context.Request.Cookies[PublicDemoViewerSessions.CookieName], out var issued);
        if (issued) IssueViewerCookie(context, viewerId);
        context.Items[ViewerIdItem] = viewerId;

        if (!budget.TryConsume(viewerId))
        {
            await Deny(context, PublicDemoVerdict.RateLimited);
            return;
        }

        // The declared Content-Length was already checked by the policy. This
        // closes the chunked case, where there is no declared length to check.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = _options.MaxRequestBodyBytes;

        await next(context);
    }

    private static void ApplyBrowserBoundary(HttpContext context, string path)
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] = PublicDemoBrowserBoundary.ContentSecurityPolicyFor(path);
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] =
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), microphone=(), payment=(), usb=()";
    }

    private void IssueViewerCookie(HttpContext context, string viewerId)
        => context.Response.Cookies.Append(PublicDemoViewerSessions.CookieName, viewerId, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/",
        });

    private static async Task Deny(HttpContext context, PublicDemoVerdict verdict)
    {
        context.Response.StatusCode = verdict.Status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new PublicDemoDenial(
            verdict.Error,
            verdict.Message,
            SecurityProfiles.PublicDemo,
            true));
    }
}

/// <summary>
/// The typed denial every rejected public-demo request receives. The shape stays
/// compatible with the studio's existing <c>{ error, message }</c> envelope and
/// adds the two facts a visitor and an external probe both need: which profile
/// answered, and that the instance is read-only.
/// </summary>
public sealed record PublicDemoDenial(string Error, string Message, string Profile, bool ReadOnly);

public static class PublicDemoEdgeExtensions
{
    /// <summary>
    /// Registers the edge's bounded in-memory stores. Safe to call in every
    /// profile; the middleware itself is only inserted for the public demo.
    /// </summary>
    public static IServiceCollection AddPublicDemoEdge(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PublicDemoOptions>(configuration.GetSection(PublicDemoOptions.SectionName));
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PublicDemoOptions>>().Value;
            return new PublicDemoViewerSessions(
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                TimeSpan.FromMinutes(Math.Max(1, options.ViewerSessionMinutes)),
                Math.Max(1, options.MaxViewerSessions));
        });
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PublicDemoOptions>>().Value;
            return new PublicDemoRequestBudget(
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                Math.Max(1, options.RequestsPerMinute),
                Math.Max(1, options.MaxViewerSessions));
        });
        return services;
    }

    public static IApplicationBuilder UsePublicDemoEdge(this IApplicationBuilder app)
        => app.UseMiddleware<PublicDemoEdgeMiddleware>();
}
