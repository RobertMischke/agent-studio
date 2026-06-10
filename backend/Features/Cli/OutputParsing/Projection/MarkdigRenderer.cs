using Ganss.Xss;
using Markdig;

namespace AgentStudio.Cli;

/// <summary>
/// Renders markdown to safe HTML. The contract is intentionally narrow so
/// the projector (and tests) treat rendering as a pure function:
/// markdown + image context → sanitized HTML string.
///
/// Pipeline order:
/// <list type="number">
/// <item>Markdig → raw HTML (tables, fenced code, autolinks).</item>
/// <item><see cref="ImageUrlRewriter"/> → relative attachment paths become absolute API URLs.</item>
/// <item><see cref="HtmlSanitizer"/> → tag/attr/scheme whitelist; the only XSS gate.</item>
/// </list>
/// </summary>
public interface IMarkdownRenderer
{
    string ToHtml(string markdown, ImageContext imageCtx);
}

public sealed class MarkdigRenderer : IMarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;
    private readonly HtmlSanitizer _sanitizer;

    public MarkdigRenderer()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions() // tables, footnotes, task lists, etc.
            .UseAutoLinks()
            .Build();
        _sanitizer = BuildSanitizer();
    }

    public string ToHtml(string markdown, ImageContext imageCtx)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;
        var raw = Markdig.Markdown.ToHtml(markdown, _pipeline);
        var rewritten = ImageUrlRewriter.Rewrite(raw, imageCtx);
        return _sanitizer.Sanitize(rewritten);
    }

    private static HtmlSanitizer BuildSanitizer()
    {
        var s = new HtmlSanitizer();

        // Start from a known-narrow set rather than the library defaults so
        // an upstream change to defaults cannot widen our XSS surface.
        s.AllowedTags.Clear();
        foreach (var t in new[]
        {
            "p", "br", "hr", "strong", "em", "u", "s", "code", "pre",
            "ul", "ol", "li", "blockquote",
            "a", "img",
            "h1", "h2", "h3", "h4", "h5", "h6",
            "table", "thead", "tbody", "tr", "td", "th",
            "span", "div"
        }) s.AllowedTags.Add(t);

        s.AllowedAttributes.Clear();
        foreach (var a in new[]
        {
            "href", "title", "alt", "src", "class",
            "data-lightbox-src", "data-language",
            "loading", "colspan", "rowspan", "id"
        }) s.AllowedAttributes.Add(a);

        s.AllowedCssProperties.Clear();
        s.AllowedAtRules.Clear();

        s.AllowedSchemes.Clear();
        s.AllowedSchemes.Add("http");
        s.AllowedSchemes.Add("https");
        s.AllowedSchemes.Add("mailto");

        // Allow internal API paths (used for image rewrites) by treating
        // bare-host-less URLs as allowed; the rewriter already produced
        // absolute /api/... paths.
        s.AllowDataAttributes = true;

        return s;
    }
}
