using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>
/// Rewrites <c>&lt;img src="..."&gt;</c> attributes inside already-rendered
/// HTML so the frontend never sees relative paths it cannot resolve.
///
/// Rules (matches the F22 prompt):
/// <list type="bullet">
/// <item>relative path under <c>attachments/</c> → <c>/api/tasks/{jobId}/attachments/...</c></item>
/// <item>top-level relative path under <c>results/</c> → <c>/api/tasks/{jobId}/results/...</c></item>
/// <item>nested relative path under <c>results/</c> → <c>/api/tasks/{jobId}/screenshot?path=...</c></item>
/// <item>absolute <c>http(s)://</c> URLs are passed through (sanitizer enforces scheme)</item>
/// <item>any <c>..</c> traversal is stripped: the img is replaced by its alt text so
///   a malicious link cannot pull from outside the job tree</item>
/// </list>
///
/// Adds <c>loading="lazy"</c> and a <c>data-lightbox-src</c> attribute so the
/// frontend lightbox hook keeps working without re-parsing the body.
/// </summary>
public static partial class ImageUrlRewriter
{
    public static string Rewrite(string html, ImageContext ctx)
    {
        if (string.IsNullOrEmpty(html)) return html;
        if (string.IsNullOrWhiteSpace(ctx.JobId)) return html;

        return ImgTagRegex().Replace(html, m =>
        {
            var src = m.Groups["src"].Value;
            var alt = m.Groups["alt"].Success ? m.Groups["alt"].Value : "";
            var title = m.Groups["title"].Success ? m.Groups["title"].Value : "";

            // Path traversal: drop the image to its alt text. We must not let
            // a crafted markdown source pull from elsewhere on disk through
            // the api/tasks/{id}/attachments handler.
            if (ContainsTraversal(src))
            {
                return string.IsNullOrEmpty(alt) ? string.Empty : alt;
            }

            if (IsAbsoluteSafeScheme(src))
            {
                return BuildImgTag(src, alt, title, lightboxSrc: null);
            }

            var rewritten = RewriteRelative(src, ctx);
            if (rewritten is null)
            {
                // Unknown relative shape; leave the body text-only so the
                // sanitizer cannot later be tricked into emitting a broken
                // path that still looks like an image.
                return string.IsNullOrEmpty(alt) ? string.Empty : alt;
            }

            return BuildImgTag(rewritten, alt, title, rewritten);
        });
    }

    private static bool ContainsTraversal(string src)
    {
        if (src.Contains("..", StringComparison.Ordinal)) return true;
        // A leading slash for non-absolute URLs would also escape the job
        // tree (it bypasses the api/tasks/{id}/... prefix); the api/ allow
        // list above already covers the legitimate absolute case.
        return false;
    }

    private static bool IsAbsoluteSafeScheme(string src)
    {
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return true;
        if (src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        if (src.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return true;
        if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? RewriteRelative(string src, ImageContext ctx)
    {
        var trimmed = src.TrimStart('/');

        if (trimmed.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed["attachments/".Length..];
            return $"/api/tasks/{Uri.EscapeDataString(ctx.JobId)}/attachments/{EscapePath(rest)}{WatchPathQuery(ctx, '?')}";
        }
        if (trimmed.StartsWith("results/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed["results/".Length..];
            if (!rest.Contains('/'))
            {
                return $"/api/tasks/{Uri.EscapeDataString(ctx.JobId)}/results/{EscapePath(rest)}{WatchPathQuery(ctx, '?')}";
            }
            return $"/api/tasks/{Uri.EscapeDataString(ctx.JobId)}/screenshot?path={Uri.EscapeDataString(rest)}{WatchPathQuery(ctx, '&')}";
        }
        // Bare filename (no folder prefix): assume it belongs to the job
        // attachments folder. That matches how the activity-log parser has
        // emitted screenshot paths historically.
        if (!trimmed.Contains('/') && trimmed.Length > 0)
        {
            return $"/api/tasks/{Uri.EscapeDataString(ctx.JobId)}/attachments/{EscapePath(trimmed)}{WatchPathQuery(ctx, '?')}";
        }

        return null;
    }

    private static string WatchPathQuery(ImageContext ctx, char prefix)
    {
        if (string.IsNullOrWhiteSpace(ctx.WatchPath)) return "";
        return $"{prefix}watchPath={Uri.EscapeDataString(ctx.WatchPath)}";
    }

    private static string EscapePath(string rest)
    {
        // Keep path separators; escape each segment individually so spaces
        // and unicode characters in filenames survive the URL trip.
        return string.Join('/', rest.Split('/').Select(Uri.EscapeDataString));
    }

    private static string BuildImgTag(string src, string alt, string title, string? lightboxSrc)
    {
        var altAttr = $" alt=\"{HtmlAttrEscape(alt)}\"";
        var titleAttr = string.IsNullOrEmpty(title) ? "" : $" title=\"{HtmlAttrEscape(title)}\"";
        var lightboxAttr = lightboxSrc is null
            ? ""
            : $" data-lightbox-src=\"{HtmlAttrEscape(lightboxSrc)}\"";
        return $"<img src=\"{HtmlAttrEscape(src)}\"{altAttr}{titleAttr} loading=\"lazy\"{lightboxAttr} />";
    }

    private static string HtmlAttrEscape(string s)
        => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    // Markdig emits  <img src="..." alt="..." />   (self-closing, double-quoted).
    // We match the common shape and surface src / alt / title for the rewriter.
    [GeneratedRegex(
        @"<img\b(?=[^>]*\bsrc=""(?<src>[^""]*)"")(?:[^>]*\balt=""(?<alt>[^""]*)"")?(?:[^>]*\btitle=""(?<title>[^""]*)"")?[^>]*/?>",
        RegexOptions.IgnoreCase)]
    private static partial Regex ImgTagRegex();
}
