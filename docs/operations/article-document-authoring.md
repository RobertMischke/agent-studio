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

## Multi-page Dossier extension

Template v2 supports a bounded Dossier family when one HTML entrypoint would
make distinct reference material difficult to scan. The folder contract is:

```text
<dossier>/
  index.html
  workbench.json
  pages/
    <stable-page-slug>.html
```

`index.html` remains the entrypoint and stable external reference. Each file in
`pages/` is a complete, self-contained HTML document with the same theme,
responsive, reduced-motion, and network-isolation rules as the entrypoint. Do
not use subpages to split one short article or to evade useful reading order.

### `workbench.json` pages schema

Declare ordered subpage navigation with the optional `pages` array:

```json
{
  "entrypoint": "index.html",
  "pages": [
    { "title": "Dos and Don'ts", "path": "pages/dos-and-donts.html" },
    { "title": "Applied surfaces", "path": "pages/applied-surfaces.html" }
  ]
}
```

The extension is additive for schema-v1 and schema-v2 descriptors. A missing
`pages` property continues to mean a single-page Dossier. The catalogue applies
these invariants:

- `pages` is an ordered array with no more than 12 entries;
- every entry contains a human-facing `title` and a stable Dossier-relative
  `path` below `pages/`;
- page titles are at most 120 characters;
- paths are unique, resolve inside the Dossier folder, exist, and end in
  `.html` or `.htm`;
- the entrypoint and all declared pages together stay within the 20 MiB Dossier
  HTML limit;
- all declared page files participate in dirty-state provenance and the
  Dossier fingerprint.

The Studio viewer renders Overview plus the declared order as flat host chrome
and swaps the isolated `srcdoc` without opening another editor tab. Its current
page drives relative Wiki-link resolution and the Open in Wiki target. Direct
browser readers need equivalent links in each file; template v2 provides the
`.dossier-page-nav` class for that small navigation block.

Page anchors remain page-local. Existing section anchor names, the entrypoint,
and the Dossier key must not change when a page is added. This keeps citations,
task references, and refresh-card prompts stable while the document grows.

A maintained reference may use `status: living-standard` with
`phase: maintenance`. It remains in current lists without an open decision gate;
this is the durable state used by standards that evolve through explicit
refresh and revamp cards.

The reference implementation is
[`admin-design-guideline`](admin-design-guideline/index.html), with
[`Dos and Don'ts`](admin-design-guideline/pages/dos-and-donts.html) and
[`Applied surfaces`](admin-design-guideline/pages/applied-surfaces.html).

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
