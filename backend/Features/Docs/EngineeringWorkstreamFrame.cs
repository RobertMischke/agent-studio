namespace AgentStudio.Docs;

/// <summary>
/// The fixed Engineering Workstream frame (concept: <c>docs/concepts/engineering-workstream.md</c>,
/// slice EW-1). Every project wiki carries the same five immutable areas so the
/// development story always lives in the same place:
///
///   Current Development State, Development Signals, System Knowledge,
///   Decision Log, Workstream Log.
///
/// The frame is a real folder structure under the wiki root - there is no
/// virtual organisation layer (see <c>docs/contracts/wiki-tree.md</c>). What
/// this type owns is the <b>identity</b> of the frame: which docs-root-relative
/// paths are frame folders and frame landing shells, and therefore which paths
/// the wiki mutation surface must refuse to move, rename, delete, or overwrite -
/// even when the request comes from an agent.
///
/// <para>
/// Immutability has two tiers:
/// <list type="bullet">
///   <item><see cref="IsStructural"/>: the frame root, the five area folders,
///   and the landing shells cannot be moved, renamed, or deleted. This keeps the
///   frame's shape stable.</item>
///   <item><see cref="IsContentLocked"/>: the landing shells (the HTML
///   orientation pages) additionally cannot be overwritten, because their layout
///   <i>is</i> the frame. Regular subpages created under an area folder are fully
///   mutable and keep normal git history.</item>
/// </list>
/// </para>
///
/// <para>
/// AGT-1984 relocation: the frame is anchored by a single wiki-relative constant
/// (<see cref="FrameRootRel"/>) and works purely on docs-root-relative paths. It
/// never hard-codes the <c>docs/</c> prefix or an absolute checkout path, so when
/// the wiki moves into its own branch-bound checkout only the wiki-root resolver
/// (<c>ProjectDocsService</c>) changes; this definition and its immutability
/// rules move unchanged.
/// </para>
/// </summary>
public static class EngineeringWorkstreamFrame
{
    /// <summary>
    /// Wiki-root-relative folder that contains the whole frame. This is the one
    /// place that pins the frame's location; everything else is derived from it,
    /// which is what keeps the frame relocatable for AGT-1984.
    /// </summary>
    public const string FrameRootRel = "engineering-workstream";

    /// <summary>
    /// User-facing label for the frame's root node in the wiki tree. The physical
    /// folder stays <see cref="FrameRootRel"/> ("engineering-workstream") so
    /// existing checkouts keep working; only the displayed name changed to
    /// "Workstream" (operator decision 2026-07-09). The frame is also pinned to the
    /// top of the wiki tree - see <see cref="IsFrameRoot"/>.
    /// </summary>
    public const string RootDisplayName = "Workstream";

    /// <summary>The frame overview / orientation landing shell.</summary>
    public const string OverviewShellRel = FrameRootRel + "/00-overview.html";

    /// <summary>
    /// One fixed area of the frame. <see cref="FolderRel"/> is the immutable
    /// container that holds the area's landing shell plus any operator/agent
    /// subpages; <see cref="IndexShellRel"/> is the immutable HTML orientation
    /// page for that area.
    /// </summary>
    public sealed record FrameArea(
        string Slug,
        string FolderRel,
        string IndexShellRel,
        string Title,
        string Purpose);

    /// <summary>
    /// The five areas in display order. The numeric folder prefixes drive the
    /// wiki tree ordering (and are stripped from the displayed label); the title
    /// and purpose feed the seeded landing shells and the concept doc.
    /// </summary>
    public static IReadOnlyList<FrameArea> Areas { get; } =
    [
        Area("10-current-development-state", "Current Development State",
            "What is actively being built right now: in-flight streams, their intent, and where they stand."),
        Area("20-development-signals", "Development Signals",
            "The health readout: drift, regressions, recurring failures, and the metrics worth watching."),
        Area("30-system-knowledge", "System Knowledge",
            "How the system actually works: durable architecture, contracts, and hard-won operational lessons."),
        Area("40-decision-log", "Decision Log",
            "Why the system is shaped the way it is: decisions taken, alternatives rejected, and their triggers."),
        Area("50-workstream-log", "Workstream Log",
            "The running narrative of the workstream: what happened, in order, over time."),
    ];

    private static FrameArea Area(string folder, string title, string purpose) =>
        new(
            Slug: folder,
            FolderRel: $"{FrameRootRel}/{folder}",
            IndexShellRel: $"{FrameRootRel}/{folder}/index.html",
            Title: title,
            Purpose: purpose);

    /// <summary>The frame root plus every area folder (the immutable folders).</summary>
    private static readonly HashSet<string> FrameFolders =
        new(new[] { FrameRootRel }.Concat(Areas.Select(a => a.FolderRel)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>The overview plus every area landing shell (the immutable pages).</summary>
    private static readonly HashSet<string> FrameShells =
        new(new[] { OverviewShellRel }.Concat(Areas.Select(a => a.IndexShellRel)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Normalises a docs-root-relative path for comparison (slashes, trim).</summary>
    private static string Normalize(string? relPath) =>
        string.IsNullOrWhiteSpace(relPath)
            ? string.Empty
            : relPath.Replace('\\', '/').Trim().Trim('/');

    /// <summary>True when the path is a frame folder (root or one of the five areas).</summary>
    public static bool IsFrameFolder(string? relPath) => FrameFolders.Contains(Normalize(relPath));

    /// <summary>
    /// The frame area a docs-root-relative path lives under (the area folder
    /// itself or any node below it), or <c>null</c> when the path is the frame
    /// root, the overview shell, or outside the frame entirely. Backs the wiki
    /// Pulse change-feed area badge and the per-area drift grade bar, both of
    /// which classify a page by its owning area.
    /// </summary>
    public static FrameArea? AreaForPath(string? relPath)
    {
        var rel = Normalize(relPath);
        if (rel.Length == 0) return null;
        foreach (var area in Areas)
        {
            if (rel.Equals(area.FolderRel, StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith(area.FolderRel + "/", StringComparison.OrdinalIgnoreCase))
                return area;
        }
        return null;
    }

    /// <summary>True when the path is exactly the frame root folder (not an area or subpage).</summary>
    public static bool IsFrameRoot(string? relPath) =>
        Normalize(relPath).Equals(FrameRootRel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The display label a wiki tree node should carry, or <c>null</c> to fall back
    /// to the default (order-prefix-stripped folder name). Only the frame root is
    /// relabelled - to <see cref="RootDisplayName"/> ("Workstream"); the five areas
    /// keep the titles derived from their own folder names.
    /// </summary>
    public static string? DisplayTitle(string? relPath) =>
        IsFrameRoot(relPath) ? RootDisplayName : null;

    /// <summary>True when the path is a frame landing shell (overview or an area index).</summary>
    public static bool IsFrameShell(string? relPath) => FrameShells.Contains(Normalize(relPath));

    /// <summary>
    /// True when the node is part of the frame's fixed structure: a frame folder
    /// or a landing shell. Such a node may not be moved, renamed, or deleted.
    /// Subpages created under an area folder are not structural and stay mutable.
    /// </summary>
    public static bool IsStructural(string? relPath)
    {
        var rel = Normalize(relPath);
        return rel.Length > 0 && (FrameFolders.Contains(rel) || FrameShells.Contains(rel));
    }

    /// <summary>
    /// True when the node's <i>content</i> is locked (the landing shells). Their
    /// layout is the frame, so they cannot be overwritten through the wiki save
    /// endpoint. Subpages under an area remain freely editable.
    /// </summary>
    public static bool IsContentLocked(string? relPath) => IsFrameShell(relPath);

    /// <summary>
    /// True when the path is inside the frame at all (the root, an area, a shell,
    /// or any subpage below them). Used to phrase precise rejection messages.
    /// </summary>
    public static bool IsWithinFrame(string? relPath)
    {
        var rel = Normalize(relPath);
        return rel.Length > 0
            && (rel.Equals(FrameRootRel, StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith(FrameRootRel + "/", StringComparison.OrdinalIgnoreCase));
    }
}
