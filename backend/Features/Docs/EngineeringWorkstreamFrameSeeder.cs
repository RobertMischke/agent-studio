using System.Text;

namespace AgentStudio.Docs;

/// <summary>
/// Outcome of an <see cref="EngineeringWorkstreamFrameSeeder.EnsureFrame"/> pass:
/// which frame shells were newly written, which already existed (and were left
/// untouched), and any that failed to write. All paths are wiki-root-relative
/// (e.g. <c>engineering-workstream/00-overview.html</c>).
/// </summary>
public sealed record EnsureFrameResult(
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Existing,
    IReadOnlyList<string> Failed)
{
    public static readonly EnsureFrameResult Empty = new([], [], []);

    /// <summary>True when at least one frame shell was newly written this pass.</summary>
    public bool CreatedAnything => Created.Count > 0;

    /// <summary>A one-line summary for structured logging.</summary>
    public string Summary =>
        $"created={Created.Count} existing={Existing.Count} failed={Failed.Count}";
}

/// <summary>
/// The reusable "ensure-frame" primitive (AGT-2024). It idempotently materializes
/// the complete Workstream frame - the five immutable area folders plus their
/// landing shells and the overview shell (see
/// <see cref="EngineeringWorkstreamFrame"/>) - inside a target project's
/// <c>docs/</c> tree, so a project's frame is <b>self-provisioned</b> the first
/// time a wiki-writing pipeline step runs for it (operator decision 2026-07-10:
/// no separate onboarded flag, no manual bootstrap action).
///
/// <para>
/// The primitive is deliberately narrow and safe to call before every write:
/// <list type="bullet">
///   <item>It only ever creates the six known frame shells and their folders; it
///   never enumerates, moves, or deletes anything, so foreign files are always
///   untouched.</item>
///   <item>It <b>never overwrites</b> an existing shell - a partially present
///   frame is completed, and a fully present frame is a no-op. This preserves any
///   operator/agent edits made to a shell (even though the content lock normally
///   forbids them) and keeps the pass idempotent.</item>
///   <item>It never throws: a per-file write failure is recorded and the pass
///   continues, so a frame-seed hiccup can never break the wiki step that called
///   it. The caller logs <see cref="EnsureFrameResult.Summary"/>.</item>
/// </list>
/// New wiki-writing steps (today <c>WikiMaintenancePostStepRunner</c> and
/// <c>WikiLearningsPostStepRunner</c>; later the EW-2 collector and curator) call
/// this once, before their own writes.
/// </para>
/// </summary>
public static class EngineeringWorkstreamFrameSeeder
{
    /// <summary>
    /// Ensures the frame exists under <paramref name="docsRoot"/> (the project's
    /// wiki root, i.e. its <c>docs/</c> folder). Missing folders are created and
    /// missing shells are written in <paramref name="language"/>; existing shells
    /// are left exactly as they are. Returns what changed for logging.
    /// </summary>
    /// <param name="docsRoot">Absolute path of the project's <c>docs/</c> folder.</param>
    /// <param name="language">Language the newly written shells are rendered in.</param>
    public static EnsureFrameResult EnsureFrame(string docsRoot, EngineeringWorkstreamFrameLanguage language)
    {
        if (string.IsNullOrWhiteSpace(docsRoot)) return EnsureFrameResult.Empty;

        var created = new List<string>();
        var existing = new List<string>();
        var failed = new List<string>();

        // The overview shell (its folder is the frame root).
        EnsureShell(
            docsRoot,
            EngineeringWorkstreamFrame.FrameRootRel,
            EngineeringWorkstreamFrame.OverviewShellRel,
            () => EngineeringWorkstreamFrameContent.RenderOverview(language),
            created, existing, failed);

        // One landing shell per area folder, in frame order.
        foreach (var area in EngineeringWorkstreamFrame.Areas)
        {
            EnsureShell(
                docsRoot,
                area.FolderRel,
                area.IndexShellRel,
                () => EngineeringWorkstreamFrameContent.RenderArea(area, language),
                created, existing, failed);
        }

        return new EnsureFrameResult(created, existing, failed);
    }

    /// <summary>
    /// Ensures one shell exists: creates its folder (idempotent), then writes the
    /// rendered content only when the file is missing. A write failure is recorded
    /// and swallowed so the whole pass never throws.
    /// </summary>
    private static void EnsureShell(
        string docsRoot,
        string folderRel,
        string shellRel,
        Func<string> renderContent,
        List<string> created,
        List<string> existing,
        List<string> failed)
    {
        try
        {
            var folderFull = Path.Combine(docsRoot, ToNativePath(folderRel));
            Directory.CreateDirectory(folderFull);

            var shellFull = Path.Combine(docsRoot, ToNativePath(shellRel));
            if (File.Exists(shellFull))
            {
                existing.Add(shellRel);
                return;
            }

            File.WriteAllText(shellFull, renderContent(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            created.Add(shellRel);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, $"EngineeringWorkstreamFrameSeeder: could not ensure frame shell '{shellRel}'.");
            failed.Add(shellRel);
        }
    }

    /// <summary>Converts a wiki-root-relative ('/'-separated) path to the host separator.</summary>
    private static string ToNativePath(string relPath) =>
        relPath.Replace('/', Path.DirectorySeparatorChar);
}

/// <summary>
/// Resolves which <see cref="EngineeringWorkstreamFrameLanguage"/> a project's
/// seeded frame is rendered in (AGT-2024). A public / open-source repo always
/// gets English; an internal project can opt into a localized frame. The choice
/// is a project setting (<c>ProjectSettings.WorkstreamFramePublic</c>) with a
/// heuristic default.
/// </summary>
public static class WorkstreamFrameLanguageResolver
{
    /// <summary>
    /// Resolves the frame language. An explicit <paramref name="isPublicOverride"/>
    /// wins (true → English, false → the project's localized language). When it is
    /// null the heuristic default applies: English. English is the conservative
    /// default because any project may be published and the platform's written
    /// artifacts are English; an operator opts a project into a localized frame by
    /// setting <c>WorkstreamFramePublic = false</c>.
    /// </summary>
    public static EngineeringWorkstreamFrameLanguage Resolve(string? projectName, bool? isPublicOverride)
    {
        if (isPublicOverride is bool isPublic)
            return isPublic ? EngineeringWorkstreamFrameLanguage.English : LocalizedLanguage(projectName);

        // Heuristic default: treat an unmarked project as public (English). No
        // reliable "this repo is private" signal exists at the seed site, and
        // English is the safe default for anything that might be open-sourced.
        return EngineeringWorkstreamFrameLanguage.English;
    }

    /// <summary>
    /// The localized (non-public) frame language for a project. Today the single
    /// localized option is German (the team's language); the seam is here so a
    /// future per-project language choice slots in without touching call sites.
    /// </summary>
    private static EngineeringWorkstreamFrameLanguage LocalizedLanguage(string? projectName)
        => EngineeringWorkstreamFrameLanguage.German;
}
