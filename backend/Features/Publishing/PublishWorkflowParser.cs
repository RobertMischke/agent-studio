using System.Text.RegularExpressions;

namespace AgentStudio.Publishing;

/// <summary>
/// Facts extracted from a single GitHub Actions workflow file, used to derive
/// publish targets. Deliberately coarse booleans rather than a full YAML model:
/// the derivation only needs "is this a tag/release-triggered package publish?"
/// and "is this a Pages/website deploy?", which are robustly detectable from the
/// trigger block and the step verbs without a YAML dependency.
/// </summary>
public sealed record WorkflowFacts(
    string FileName,
    bool HasReleaseTrigger,
    bool PublishesNpm,
    bool PublishesNuGet,
    bool DeploysWebsite,
    string? PagesArtifactPath);

/// <summary>
/// PUB-1 - parses a <c>.github/workflows/*.yml</c> file into
/// <see cref="WorkflowFacts"/>. No YAML library (the codebase parses YAML with
/// deterministic line/regex helpers, see <c>FrontmatterParser</c>): GitHub
/// Actions workflows have a well-known trigger + step vocabulary, so targeted
/// detection is both robust and dependency-free. Pure and static so it unit-tests
/// without a repository.
///
/// <para><b>Release trigger.</b> A package publish is triggered by pushing a
/// version tag (<c>on: push: tags: ['v*']</c>) or by a published release
/// (<c>on: release: types: [published]</c>). We detect a <c>tags:</c> entry with
/// a version-ish glob inside the <c>push:</c> block, or a <c>release:</c>
/// trigger.</para>
///
/// <para><b>Publish step.</b> npm when a step runs <c>npm publish</c> (or uses the
/// <c>JS-DevTools/npm-publish</c> action); NuGet when a step runs
/// <c>dotnet nuget push</c> / <c>nuget push</c> / <c>dotnet pack</c>.</para>
///
/// <para><b>Website deploy.</b> the modern Pages actions
/// (<c>actions/deploy-pages</c>, <c>actions/upload-pages-artifact</c>), the
/// classic <c>peaceiris/actions-gh-pages</c>, or a filename that names it
/// (<c>deploy-website</c>, <c>pages</c>). The upload action's <c>path:</c> is
/// captured so the website scope can follow a non-default source folder.</para>
/// </summary>
public static class PublishWorkflowParser
{
    private static readonly Regex NpmPublish = new(
        @"(^|\s)npm\s+publish\b|JS-DevTools/npm-publish",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex NuGetPublish = new(
        @"dotnet\s+nuget\s+push|(^|\s)nuget\s+push\b|dotnet\s+pack\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex PagesDeploy = new(
        @"actions/deploy-pages|actions/upload-pages-artifact|peaceiris/actions-gh-pages",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReleaseEvent = new(
        @"^\s*release\s*:",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // upload-pages-artifact's `with: path: <dir>` - captures the site source dir
    // so a non-default website folder still scopes correctly. Tolerates quotes.
    private static readonly Regex PagesArtifactPathRx = new(
        @"path\s*:\s*['""]?(?<path>[^'""\r\n]+?)['""]?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static WorkflowFacts Parse(string fileName, string content)
    {
        content ??= string.Empty;
        var fileLower = (fileName ?? string.Empty).ToLowerInvariant();

        var hasReleaseTrigger = HasTagPushTrigger(content) || ReleaseEvent.IsMatch(content);
        var publishesNpm = NpmPublish.IsMatch(content);
        var publishesNuGet = NuGetPublish.IsMatch(content);

        var pagesByAction = PagesDeploy.IsMatch(content);
        var pagesByName =
            fileLower.Contains("deploy-website") ||
            fileLower.Contains("deploy-pages") ||
            fileLower == "pages.yml" || fileLower == "pages.yaml" ||
            fileLower.Contains("gh-pages");
        var deploysWebsite = pagesByAction || pagesByName;

        string? pagesArtifactPath = null;
        if (pagesByAction)
        {
            var m = PagesArtifactPathRx.Match(content);
            if (m.Success)
            {
                var candidate = m.Groups["path"].Value.Trim();
                // Ignore the whole-repo default and obvious non-folders.
                if (candidate.Length > 0 && candidate != "." && candidate != "./")
                    pagesArtifactPath = candidate.TrimEnd('/');
            }
        }

        return new WorkflowFacts(
            fileName ?? string.Empty,
            hasReleaseTrigger,
            publishesNpm,
            publishesNuGet,
            deploysWebsite,
            pagesArtifactPath);
    }

    /// <summary>
    /// True when the workflow's <c>on:</c> block declares a <c>push:</c> tag
    /// trigger whose glob looks version-ish (<c>v*</c>, <c>v[0-9]*</c>,
    /// <c>[0-9]+.[0-9]+.*</c>). Scans the <c>tags:</c> list that follows a
    /// <c>push:</c> key; a bare <c>tags:</c> anywhere with a version glob also
    /// counts, since GitHub only allows <c>tags:</c> under <c>push:</c>.
    /// </summary>
    internal static bool HasTagPushTrigger(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var inTagsBlock = false;
        var tagsBlockIndent = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var indent = raw.Length - raw.TrimStart().Length;

            // A `tags:` key opens a list block; the values may be inline
            // (`tags: ['v*']`) or on following, more-indented lines.
            var tagsKey = Regex.Match(trimmed, @"^tags\s*:(.*)$", RegexOptions.IgnoreCase);
            if (tagsKey.Success)
            {
                var inline = tagsKey.Groups[1].Value;
                if (LooksVersionGlob(inline)) return true;
                inTagsBlock = true;
                tagsBlockIndent = indent;
                continue;
            }

            if (inTagsBlock)
            {
                // Still inside the tags list while lines stay more indented than
                // the `tags:` key. A list item is `- 'v*'`.
                if (indent > tagsBlockIndent)
                {
                    if (LooksVersionGlob(trimmed)) return true;
                    continue;
                }
                inTagsBlock = false;
                tagsBlockIndent = -1;
            }
        }
        return false;
    }

    /// <summary>True when a tag-list fragment references a version-ish glob.</summary>
    internal static bool LooksVersionGlob(string fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return false;
        // v-prefixed (v*, v1.*, v[0-9]*) or a numeric semver glob (1.*, [0-9]+.*).
        return Regex.IsMatch(fragment, @"v\*|v\[?0-9|v\d|\[0-9\][^/]*\.|^\s*-?\s*['""]?\d+\.\d",
            RegexOptions.IgnoreCase);
    }
}
