namespace AgentStudio.Shared;

/// <summary>
/// Provenance label for an evidence screenshot. Derived from the filename
/// suffix convention so the reviewer can tell at a glance whether a shot was
/// taken against a running backend (<c>real</c>) or an e2e run with mocked
/// API routes (<c>mocked</c>). Composite images that stitch several shots
/// together carry <c>composite</c> plus the source of each part.
/// Documentation and marketing captures made from a versioned deterministic
/// workspace snapshot carry <c>pinned</c>; this is separate from task-run
/// evidence provenance.
///
/// See <c>docs/system/contracts/protocol-style.md</c> §4.4 for the filename grammar.
/// </summary>
public static class ScreenshotSources
{
    /// <summary>Captured against a live backend (the recommended UI-acceptance evidence).</summary>
    public const string Real = "real";
    /// <summary>Captured from an e2e run whose API routes were mocked.</summary>
    public const string Mocked = "mocked";
    /// <summary>A stitched image; the sources of its parts live on <see cref="ScreenshotSourceInfo.Parts"/>.</summary>
    public const string Composite = "composite";
    /// <summary>Captured from the versioned, deterministic documentation workspace snapshot.</summary>
    public const string Pinned = "pinned";
    /// <summary>No recognised source suffix on the filename. The UI stays honest and shows no real/mocked claim.</summary>
    public const string Unlabeled = "unlabeled";

    public static readonly string[] All = [Real, Mocked, Composite, Pinned, Unlabeled];
}

/// <summary>
/// Result of parsing a screenshot filename into its source label. For a
/// composite, <see cref="Parts"/> lists the source of each stitched part
/// (e.g. <c>["real", "mocked"]</c>); for every other label it is empty.
/// </summary>
public readonly record struct ScreenshotSourceInfo(string Source, IReadOnlyList<string> Parts)
{
    public static readonly ScreenshotSourceInfo UnlabeledInfo =
        new(ScreenshotSources.Unlabeled, []);
}

/// <summary>
/// Pure parser for the screenshot source-label filename convention. The label
/// is the final <c>--</c>-delimited segment of the filename (before the
/// extension):
///
/// <list type="bullet">
///   <item><c>name--real.png</c> → <see cref="ScreenshotSources.Real"/></item>
///   <item><c>name--mocked.png</c> → <see cref="ScreenshotSources.Mocked"/></item>
///   <item><c>name--composite.png</c> → <see cref="ScreenshotSources.Composite"/>, no parts</item>
///   <item><c>before-after--composite-real-mocked.png</c> → composite of a real and a mocked part</item>
///   <item><c>name--pinned.png</c> → <see cref="ScreenshotSources.Pinned"/></item>
///   <item>anything else / no suffix → <see cref="ScreenshotSources.Unlabeled"/></item>
/// </list>
///
/// The base name may contain single dashes (<c>before-after</c>); only the
/// double-dash boundary introduces the source segment, so existing filenames
/// without a <c>--</c> suffix are always <c>unlabeled</c>.
/// </summary>
public static class ScreenshotSourceParser
{
    private const string Boundary = "--";

    public static ScreenshotSourceInfo Parse(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return ScreenshotSourceInfo.UnlabeledInfo;

        var stem = StripExtension(fileName.Trim());
        var boundary = stem.LastIndexOf(Boundary, StringComparison.Ordinal);
        if (boundary < 0) return ScreenshotSourceInfo.UnlabeledInfo;

        var segment = stem[(boundary + Boundary.Length)..];
        if (segment.Length == 0) return ScreenshotSourceInfo.UnlabeledInfo;

        var tokens = segment.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return ScreenshotSourceInfo.UnlabeledInfo;

        var head = tokens[0].ToLowerInvariant();
        switch (head)
        {
            case ScreenshotSources.Real:
                return new ScreenshotSourceInfo(ScreenshotSources.Real, []);
            case ScreenshotSources.Mocked:
                return new ScreenshotSourceInfo(ScreenshotSources.Mocked, []);
            case ScreenshotSources.Composite:
                var parts = new List<string>();
                foreach (var token in tokens[1..])
                {
                    var t = token.ToLowerInvariant();
                    if (t is ScreenshotSources.Real or ScreenshotSources.Mocked) parts.Add(t);
                }
                return new ScreenshotSourceInfo(ScreenshotSources.Composite, parts);
            case ScreenshotSources.Pinned:
                return new ScreenshotSourceInfo(ScreenshotSources.Pinned, []);
            default:
                return ScreenshotSourceInfo.UnlabeledInfo;
        }
    }

    private static string StripExtension(string fileName)
    {
        var lastDot = fileName.LastIndexOf('.');
        var lastSep = fileName.LastIndexOfAny(['/', '\\']);
        return lastDot > lastSep && lastDot >= 0 ? fileName[..lastDot] : fileName;
    }
}
