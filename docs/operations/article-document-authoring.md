# Article document authoring

This page defines the reusable article presentation for decision and evidence
documents. It deliberately does not choose a product label for the document
family. The existing descriptor filename and code types remain compatibility
contracts while naming changes are handled separately.

## Canonical source

The copyable source is
[`docs/app/templates/article-document-v2.html`](../app/templates/article-document-v2.html).
It is also embedded into the backend and used by
`ConceptWorkbenchContract.CreateScaffold`, so newly scaffolded documents and
the author-facing template have one source.

The template is self-contained. Its CSS is the `<style
data-article-template="v2">` block inside the HTML file. Keep that block inline:
the isolated viewer denies external styles, fonts, and network access. Add
document-specific CSS in a separate `<style>` block after the canonical block.

The shared frame provides:

- a serif reading face at approximately `70ch`;
- system fonts for headings, metadata, tables, captions, controls, and evidence;
- light and dark palettes selected by `prefers-color-scheme`;
- `.breakout` and `.media-breakout` for wide mockups and diagrams;
- responsive collapse and reduced-motion behavior;
- surface tints, badges, and dots instead of decorative left accent bars.

## Descriptor pattern

Add the optional `pattern` field to `workbench.json` when a document adopts the
v2 template:

```json
{
  "pattern": "concept"
}
```

Supported values are:

| Value | List icon | Article emphasis |
|---|---|---|
| `ui` | comparison grid | side-by-side variants, wide mockups, and large images |
| `concept` | evidence document | source links and evidence classes |

Missing values resolve to `concept`. Unknown values also resolve to `concept`
without invalidating the descriptor. This tolerant fallback keeps older files
readable and permits a future reader to understand a newer descriptor safely.
Inside the Studio viewer, the descriptor value is authoritative and is applied
as `data-document-pattern` on the isolated HTML root. For direct browser reads,
keep the same fallback value on the template's `<html>` element.

## Multi-page Dossiers

Article template v2 also supports an optional multi-page Dossier. Use it only
when one long entry page would mix distinct review jobs. The canonical folder
shape is:

This is the additive multi-page extension to the AGT-2536 template-v2
contract; it expands the page inventory without replacing its stable keys or
anchor rules.

```text
<dossier>/
  index.html
  workbench.json
  pages/
    dos-and-donts.html
    applied-surfaces.html
  assets/                         # optional local images
```

`index.html` remains the entrypoint and stable identity page. Add descriptor
ordered subpages through the optional `pages` array:

```json
{
  "entrypoint": "index.html",
  "pages": [
    { "title": "Dos and Don'ts", "path": "pages/dos-and-donts.html" },
    { "title": "Applied surfaces", "path": "pages/applied-surfaces.html" }
  ]
}
```

This additive field is valid on the supported `workbench.json` schema versions
1 and 2. Every entry requires a non-empty `title` and a Dossier-relative HTML
path under `pages/`. Paths must be unique, must exist, must stay inside the
Dossier folder, and receive the same 20 MiB limit and reparse-point protections
as the entrypoint. The catalogue projection prepends the entrypoint so list and
viewer navigation share one ordered page model.

The viewer selects a subpage through a validated `page` query on the existing
Dossier endpoint. Page selection is replaceable UI state: it does not change
the Dossier key, tab key, stable route, entrypoint, decision anchors, or
repository folder. Keep decision-point markup and other mutation authority on
`index.html`; subpages are evidence and reference surfaces. For direct browser
reading, repeat a small relative page navigation in each HTML file. Each page
keeps its template CSS inline because the isolated viewer denies external
styles and network dependencies.

The first reference implementation is the
[Admin Surface Design Guideline](admin-design-guideline/index.html), with
[Dos and Don'ts](admin-design-guideline/pages/dos-and-donts.html) and
[Applied surfaces](admin-design-guideline/pages/applied-surfaces.html).

## Pattern-specific authoring

For a `ui` document:

1. Put comparable alternatives in `.variant-grid` and wrap each option in
   `.variant`.
2. Use `.mockup`, `.breakout`, or `.media-breakout` for the actual visual proof.
3. Give every image useful alternative text and every comparison a caption.

For a `concept` document:

1. Put durable sources in `.evidence-list`.
2. Use `.evidence` for each link and classify it with `.evidence-class`.
3. Use `data-evidence-class="observed"`, `"inferred"`, or `"proposed"` so facts,
   interpretations, and suggested changes remain distinguishable.

Both patterns may use full-bleed figures. A large concept diagram is a valid
breakout, just as a UI mockup is.

## Migration policy and references

Do not rewrite active documents in bulk. Adopt the template and add `pattern`
only when a document is already being changed. The first references are:

- [`deck-icon-exploration`](deck-icon-exploration/index.html), `pattern: ui`;
- [`workbench-konzept`](workbench-konzept/index.html), `pattern: concept`.

When adapting an older document, preserve its evidence and local visual proof,
replace the common page frame with the canonical v2 block, then keep only the
CSS that is genuinely specific to that document.

## Website follow-up

The article presentation is also intended to become a central element of the
Agent Studio website. Create a separate WEB card after the naming decision.
That follow-up owns website composition and copy; this slice documents the
intent and does not build the website surface.
