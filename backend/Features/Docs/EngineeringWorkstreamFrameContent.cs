using System.Globalization;
using System.Text;

namespace AgentStudio.Docs;

/// <summary>
/// Language a seeded Workstream frame is rendered in. Public / open-source
/// target repos always get <see cref="English"/> (operator decision 2026-07-10:
/// "frame pages for public repos consistently English"); an internal project may
/// opt into a localized frame. The five area <b>identities</b> (folder slugs and
/// their titles) are a fixed English vocabulary in every language - only the
/// surrounding orientation copy is localized - so the frame's shape and the
/// immutability rules stay language-independent.
/// </summary>
public enum EngineeringWorkstreamFrameLanguage
{
    English,
    German,
}

/// <summary>
/// Renders the self-contained HTML orientation shells of the Workstream frame
/// (concept: <c>docs/concepts/engineering-workstream.md</c>). This is the single
/// content source the <see cref="EngineeringWorkstreamFrameSeeder"/> writes into a
/// target project's <c>docs/</c> when a wiki-writing pipeline step self-provisions
/// the frame (AGT-2024).
///
/// <para>
/// Every rendered shell obeys the same invariants as the hand-authored EW-1
/// shells the platform ships (see <c>EngineeringWorkstreamShellContentTests</c>):
/// self-contained (no scripts, external CSS/fonts/images, so it renders safely in
/// the wiki's script-disabled sandboxed iframe), themed for both light and dark
/// via <c>prefers-color-scheme</c>, and a bold orientation layout (hero, the
/// cross-area rail with the current area lit, and the immutability note). The
/// area titles and purposes come straight from
/// <see cref="EngineeringWorkstreamFrame.Areas"/> so the seeded frame can never
/// drift from the declared frame identity.
/// </para>
/// </summary>
public static class EngineeringWorkstreamFrameContent
{
    private const string DarkBg = "#11111b";

    /// <summary>
    /// The five per-area accent colours, in frame order. Purely decorative (the
    /// hero band, the card rail, the area accent stripe); kept here so the
    /// overview cards and the area heroes agree on a colour per area.
    /// </summary>
    private static readonly string[] AreaAccents =
        ["#89b4fa", "#f9e2af", "#94e2d5", "#cba6f7", "#f38ba8"];

    /// <summary>
    /// Shared, self-contained stylesheet used by every shell. It is the union of
    /// the overview and area class vocabulary (grid/card for the overview,
    /// cols/panel/rail/pill for the areas) so a single inline <c>&lt;style&gt;</c>
    /// serves both. Dark is the default; the <c>prefers-color-scheme: light</c>
    /// media query is the override. Design tokens mirror the studio system.
    /// </summary>
    private const string Style = """
:root {
  --ew-bg: #11111b;
  --ew-surface: #181825;
  --ew-surface-2: #1e1e2e;
  --ew-border: rgba(255, 255, 255, 0.09);
  --ew-border-strong: rgba(255, 255, 255, 0.16);
  --ew-fg: #cdd6f4;
  --ew-fg-strong: #f5f5ff;
  --ew-fg-dim: #a6adc8;
  --ew-fg-muted: #7f849c;
  --ew-accent: #89b4fa;
  --ew-shadow: 0 1px 2px rgba(0, 0, 0, 0.4), 0 8px 24px rgba(0, 0, 0, 0.28);
  --ew-space-1: 4px; --ew-space-2: 8px; --ew-space-3: 12px;
  --ew-space-4: 16px; --ew-space-5: 24px; --ew-space-6: 32px;
  --ew-radius: 12px; --ew-radius-sm: 8px;
}
@media (prefers-color-scheme: light) {
  :root {
    --ew-bg: #f4f5fb;
    --ew-surface: #ffffff;
    --ew-surface-2: #ffffff;
    --ew-border: rgba(15, 23, 42, 0.1);
    --ew-border-strong: rgba(15, 23, 42, 0.18);
    --ew-fg: #1e293b;
    --ew-fg-strong: #0f172a;
    --ew-fg-dim: #475569;
    --ew-fg-muted: #64748b;
    --ew-accent: #2563eb;
    --ew-shadow: 0 1px 2px rgba(15, 23, 42, 0.08), 0 10px 24px rgba(15, 23, 42, 0.08);
  }
}
* { box-sizing: border-box; }
html, body { margin: 0; padding: 0; }
body {
  background: var(--ew-bg);
  color: var(--ew-fg);
  font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
  line-height: 1.55;
  -webkit-font-smoothing: antialiased;
}
.ew-wrap { max-width: 1100px; margin: 0 auto; padding: var(--ew-space-6) var(--ew-space-5); }
.ew-hero {
  position: relative;
  padding: var(--ew-space-6);
  border: 1px solid var(--ew-border);
  border-radius: var(--ew-radius);
  background:
    radial-gradient(120% 140% at 0% 0%, color-mix(in srgb, var(--ew-accent) 24%, transparent), transparent 60%),
    var(--ew-surface);
  box-shadow: var(--ew-shadow);
  overflow: hidden;
}
.ew-hero::before {
  content: ""; position: absolute; inset: 0 0 auto 0; height: 3px;
  background: linear-gradient(90deg, #89b4fa, #f9e2af, #94e2d5, #cba6f7, #f38ba8);
}
.ew-eyebrow {
  display: inline-flex; align-items: center; gap: var(--ew-space-2);
  margin: 0 0 var(--ew-space-3);
  padding: var(--ew-space-1) var(--ew-space-3);
  border: 1px solid var(--ew-border-strong); border-radius: 999px;
  color: var(--ew-fg-dim);
  font-size: 0.72rem; font-weight: 600; letter-spacing: 0.09em; text-transform: uppercase;
}
.ew-eyebrow::before {
  content: ""; width: 7px; height: 7px; border-radius: 50%; background: var(--ew-accent);
}
.ew-hero h1 {
  margin: 0; font-size: clamp(1.7rem, 4vw, 2.4rem); line-height: 1.1;
  color: var(--ew-fg-strong); letter-spacing: -0.01em;
}
.ew-lede { margin: var(--ew-space-4) 0 0; max-width: 62ch; font-size: 1.02rem; color: var(--ew-fg-dim); }
.ew-lock {
  display: inline-flex; align-items: center; gap: var(--ew-space-2);
  margin-top: var(--ew-space-5); padding: var(--ew-space-2) var(--ew-space-3);
  border: 1px dashed var(--ew-border-strong); border-radius: var(--ew-radius-sm);
  color: var(--ew-fg-muted); font-size: 0.83rem;
}
.ew-section-label {
  margin: var(--ew-space-6) 0 var(--ew-space-4);
  font-size: 0.74rem; font-weight: 700; letter-spacing: 0.12em; text-transform: uppercase;
  color: var(--ew-fg-muted);
}
.ew-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: var(--ew-space-4); }
.ew-card {
  position: relative; display: flex; flex-direction: column; gap: var(--ew-space-2);
  padding: var(--ew-space-5); padding-left: calc(var(--ew-space-5) + 4px);
  border: 1px solid var(--ew-border); border-radius: var(--ew-radius);
  background: var(--ew-surface-2); box-shadow: var(--ew-shadow);
}
.ew-card::before {
  content: ""; position: absolute; left: 0; top: var(--ew-space-4); bottom: var(--ew-space-4);
  width: 4px; border-radius: 0 4px 4px 0; background: var(--ew-card-accent, var(--ew-accent));
}
.ew-card-num { font-size: 0.72rem; font-weight: 700; letter-spacing: 0.1em; color: var(--ew-card-accent, var(--ew-accent)); }
.ew-card h2 { margin: 0; font-size: 1.08rem; color: var(--ew-fg-strong); }
.ew-card p { margin: 0; font-size: 0.9rem; color: var(--ew-fg-dim); }
.ew-card-foot { margin-top: var(--ew-space-2); font-size: 0.76rem; color: var(--ew-fg-muted); }
.ew-cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: var(--ew-space-4); }
.ew-panel {
  padding: var(--ew-space-5); border: 1px solid var(--ew-border); border-radius: var(--ew-radius);
  background: var(--ew-surface-2); box-shadow: var(--ew-shadow);
}
.ew-panel h2 { margin: 0 0 var(--ew-space-3); font-size: 1rem; color: var(--ew-fg-strong); }
.ew-panel p { margin: 0; font-size: 0.9rem; color: var(--ew-fg-dim); }
.ew-rail { display: flex; flex-wrap: wrap; gap: var(--ew-space-2); margin-top: var(--ew-space-2); }
.ew-pill {
  display: inline-flex; align-items: center; gap: var(--ew-space-2);
  padding: var(--ew-space-2) var(--ew-space-3);
  border: 1px solid var(--ew-border); border-radius: 999px;
  font-size: 0.8rem; color: var(--ew-fg-muted); background: var(--ew-surface);
}
.ew-pill b { color: var(--ew-fg-dim); font-weight: 600; }
.ew-pill--here {
  border-color: color-mix(in srgb, var(--ew-accent) 60%, transparent);
  background: color-mix(in srgb, var(--ew-accent) 16%, var(--ew-surface));
  color: var(--ew-fg-strong);
}
.ew-pill--here b { color: var(--ew-fg-strong); }
.ew-pill-num { font-variant-numeric: tabular-nums; opacity: 0.7; }
.ew-foot {
  margin-top: var(--ew-space-6); padding-top: var(--ew-space-4);
  border-top: 1px solid var(--ew-border); font-size: 0.8rem; color: var(--ew-fg-muted);
}
.ew-foot code {
  padding: 1px 6px; border-radius: 5px;
  background: color-mix(in srgb, var(--ew-fg-muted) 16%, transparent); font-size: 0.9em;
}
""";

    /// <summary>
    /// Renders the frame overview shell (<c>engineering-workstream/00-overview.html</c>):
    /// the hero, the immutability note, and one card per area (title, purpose,
    /// and the addressing folder slug) in frame order.
    /// </summary>
    public static string RenderOverview(EngineeringWorkstreamFrameLanguage language)
    {
        var copy = Copy.For(language);
        var areas = EngineeringWorkstreamFrame.Areas;

        var cards = new StringBuilder();
        for (var i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            var slug = AreaSlug(area);
            cards.Append("    <article class=\"ew-card\" style=\"--ew-card-accent:")
                 .Append(AreaAccents[i % AreaAccents.Length]).Append("\">\n")
                 .Append("      <span class=\"ew-card-num\">").Append(OrdinalLabel(i + 1)).Append("</span>\n")
                 .Append("      <h2>").Append(Escape(area.Title)).Append("</h2>\n")
                 .Append("      <p>").Append(Escape(area.Purpose)).Append("</p>\n")
                 .Append("      <span class=\"ew-card-foot\">").Append(copy.OverviewCardFolder).Append(' ')
                 .Append(slug).Append("/</span>\n")
                 .Append("    </article>\n");
        }

        return $"""
<!doctype html>
<html lang="{copy.HtmlLang}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{Escape(EngineeringWorkstreamFrame.RootDisplayName)}</title>
<style>
{Style}
</style>
</head>
<body>
<main class="ew-wrap">
  <header class="ew-hero">
    <span class="ew-eyebrow">{Escape(EngineeringWorkstreamFrame.RootDisplayName)}</span>
    <h1>{copy.OverviewHeadline}</h1>
    <p class="ew-lede">{copy.OverviewLede}</p>
    <span class="ew-lock" aria-label="{copy.LockAria}">{copy.OverviewLock}</span>
  </header>

  <p class="ew-section-label">{copy.OverviewSectionLabel}</p>
  <section class="ew-grid">
{cards.ToString().TrimEnd('\n')}
  </section>

  <p class="ew-foot">{copy.OverviewFoot}</p>
</main>
</body>
</html>
""";
    }

    /// <summary>
    /// Renders one area landing shell (<c>engineering-workstream/&lt;area&gt;/index.html</c>):
    /// the hero with the area's own title and purpose, the cross-area orientation
    /// rail with the current area lit, and a short "what belongs / what does not"
    /// panel pair.
    /// </summary>
    public static string RenderArea(EngineeringWorkstreamFrame.FrameArea area, EngineeringWorkstreamFrameLanguage language)
    {
        var copy = Copy.For(language);
        var areas = EngineeringWorkstreamFrame.Areas;
        var index = 0;
        for (var i = 0; i < areas.Count; i++)
        {
            if (string.Equals(areas[i].Slug, area.Slug, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        }
        var number = index + 1;
        var slug = AreaSlug(area);

        var rail = new StringBuilder();
        for (var i = 0; i < areas.Count; i++)
        {
            var here = i == index ? " ew-pill--here" : "";
            rail.Append("      <span class=\"ew-pill").Append(here).Append("\"><span class=\"ew-pill-num\">")
                .Append(OrdinalLabel(i + 1)).Append("</span> <b>").Append(Escape(areas[i].Title)).Append("</b></span>\n");
        }

        return $"""
<!doctype html>
<html lang="{copy.HtmlLang}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{Escape(area.Title)}</title>
<style>
{Style}
</style>
</head>
<body>
<main class="ew-wrap">
  <header class="ew-hero" style="--ew-accent:{AreaAccents[index % AreaAccents.Length]}">
    <span class="ew-eyebrow">{string.Format(CultureInfo.InvariantCulture, copy.AreaEyebrow, OrdinalLabel(number), OrdinalLabel(areas.Count))}</span>
    <h1>{Escape(area.Title)}</h1>
    <p class="ew-lede">{Escape(area.Purpose)}</p>
    <span class="ew-lock" aria-label="{copy.LockAria}">{copy.AreaLock}</span>
  </header>

  <p class="ew-section-label">{copy.AreaWhereYouAre}</p>
  <div class="ew-rail">
{rail.ToString().TrimEnd('\n')}
  </div>

  <p class="ew-section-label">{copy.AreaWorkingHere}</p>
  <section class="ew-cols">
    <article class="ew-panel">
      <h2>{copy.AreaBelongsTitle}</h2>
      <p>{copy.AreaBelongsBody}</p>
    </article>
    <article class="ew-panel">
      <h2>{copy.AreaNotTitle}</h2>
      <p>{copy.AreaNotBody}</p>
    </article>
  </section>

  <p class="ew-foot">{string.Format(CultureInfo.InvariantCulture, copy.AreaFoot, slug)}</p>
</main>
</body>
</html>
""";
    }

    /// <summary>The area folder slug without the frame-root prefix (e.g. <c>10-current-development-state</c>).</summary>
    private static string AreaSlug(EngineeringWorkstreamFrame.FrameArea area) =>
        area.FolderRel[(EngineeringWorkstreamFrame.FrameRootRel.Length + 1)..];

    /// <summary>Two-digit ordinal label used by the cards and rail (01..05).</summary>
    private static string OrdinalLabel(int n) => n.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>Minimal HTML-text escaping for the copy that lands inside elements.</summary>
    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>
    /// The localized orientation copy. The five area titles and purposes are NOT
    /// here - they are the fixed English frame identity from
    /// <see cref="EngineeringWorkstreamFrame.Areas"/>; only the surrounding
    /// orientation chrome is translated.
    /// </summary>
    private sealed record Copy(
        string HtmlLang,
        string LockAria,
        string OverviewHeadline,
        string OverviewLede,
        string OverviewLock,
        string OverviewSectionLabel,
        string OverviewCardFolder,
        string OverviewFoot,
        string AreaEyebrow,
        string AreaLock,
        string AreaWhereYouAre,
        string AreaWorkingHere,
        string AreaBelongsTitle,
        string AreaBelongsBody,
        string AreaNotTitle,
        string AreaNotBody,
        string AreaFoot)
    {
        public static Copy For(EngineeringWorkstreamFrameLanguage language) =>
            language == EngineeringWorkstreamFrameLanguage.German ? German : English;

        private static readonly Copy English = new(
            HtmlLang: "en",
            LockAria: "This frame is immutable",
            OverviewHeadline: "The development story, always in the same five places",
            OverviewLede:
                "This is the fixed frame for a project's engineering knowledge. Five areas, "
                + "in the same order, in every project wiki: what is being built now, how healthy "
                + "it is, how the system works, why it is shaped that way, and what happened over "
                + "time. Open an area for its purpose and add subpages beneath it.",
            OverviewLock:
                "Fixed frame &middot; areas and their landing pages cannot be renamed, moved, or "
                + "deleted, not even by an agent. Subpages beneath them are free to add and edit.",
            OverviewSectionLabel: "The five areas",
            OverviewCardFolder: "Area folder:",
            OverviewFoot:
                "Concept: <code>docs/concepts/engineering-workstream.md</code>. This overview and "
                + "each area landing page are part of the immutable frame; everything else under an "
                + "area is an ordinary wiki subpage with full git history.",
            AreaEyebrow: "Workstream &middot; Area {0} of {1}",
            AreaLock:
                "Fixed frame page &middot; this area and its landing page cannot be renamed, moved, "
                + "or deleted. Add subpages beneath it and they keep full git history.",
            AreaWhereYouAre: "Where you are",
            AreaWorkingHere: "Working in this area",
            AreaBelongsTitle: "What belongs here",
            AreaBelongsBody:
                "Subpages that carry this area's payload: one page per topic, kept current, each "
                + "with full git history. The frame gives the address; the subpages carry the content.",
            AreaNotTitle: "What does not",
            AreaNotBody:
                "Anything that belongs to one of the other four areas. Keep each area to its own "
                + "purpose so the development story stays findable.",
            AreaFoot:
                "Part of the fixed Workstream frame. Concept: "
                + "<code>docs/concepts/engineering-workstream.md</code>. Create subpages under "
                + "<code>{0}/</code> to add content.");

        private static readonly Copy German = new(
            HtmlLang: "de",
            LockAria: "Dieser Rahmen ist unveraenderlich",
            OverviewHeadline: "Die Entwicklungsgeschichte, immer an denselben fuenf Orten",
            OverviewLede:
                "Dies ist der feste Rahmen fuer das Engineering-Wissen eines Projekts. Fuenf "
                + "Bereiche, in derselben Reihenfolge, in jedem Projekt-Wiki: was gerade gebaut "
                + "wird, wie gesund es ist, wie das System funktioniert, warum es so geformt ist "
                + "und was ueber die Zeit geschah. Oeffne einen Bereich fuer seinen Zweck und lege "
                + "Unterseiten darunter an.",
            OverviewLock:
                "Fester Rahmen &middot; Bereiche und ihre Landeseiten koennen nicht umbenannt, "
                + "verschoben oder geloescht werden, auch nicht von einem Agenten. Unterseiten "
                + "darunter koennen frei angelegt und bearbeitet werden.",
            OverviewSectionLabel: "Die fuenf Bereiche",
            OverviewCardFolder: "Bereichsordner:",
            OverviewFoot:
                "Konzept: <code>docs/concepts/engineering-workstream.md</code>. Diese Uebersicht und "
                + "jede Bereichs-Landeseite sind Teil des unveraenderlichen Rahmens; alles andere "
                + "unter einem Bereich ist eine gewoehnliche Wiki-Unterseite mit voller Git-Historie.",
            AreaEyebrow: "Workstream &middot; Bereich {0} von {1}",
            AreaLock:
                "Feste Rahmenseite &middot; dieser Bereich und seine Landeseite koennen nicht "
                + "umbenannt, verschoben oder geloescht werden. Lege Unterseiten darunter an, sie "
                + "behalten die volle Git-Historie.",
            AreaWhereYouAre: "Wo du bist",
            AreaWorkingHere: "In diesem Bereich arbeiten",
            AreaBelongsTitle: "Was hierher gehoert",
            AreaBelongsBody:
                "Unterseiten, die den Inhalt dieses Bereichs tragen: eine Seite pro Thema, aktuell "
                + "gehalten, jede mit voller Git-Historie. Der Rahmen gibt die Adresse; die "
                + "Unterseiten tragen den Inhalt.",
            AreaNotTitle: "Was nicht",
            AreaNotBody:
                "Alles, was zu einem der anderen vier Bereiche gehoert. Halte jeden Bereich bei "
                + "seinem eigenen Zweck, damit die Entwicklungsgeschichte auffindbar bleibt.",
            AreaFoot:
                "Teil des festen Workstream-Rahmens. Konzept: "
                + "<code>docs/concepts/engineering-workstream.md</code>. Lege Unterseiten unter "
                + "<code>{0}/</code> an, um Inhalt hinzuzufuegen.");
    }
}
