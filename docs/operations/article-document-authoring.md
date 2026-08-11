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
- `.evidence-figure` for screenshot-backed claims with per-image capture dates;
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

## Screenshot evidence figures

Use screenshots as evidence whenever a Dossier makes a claim about a visible
product surface. Prose may explain the finding, but it does not replace the
capture. Every screenshot-backed finding follows this contract:

1. Wrap the capture in `.evidence-figure.media-breakout` so the evidence uses
   the full article media width rather than the prose column.
2. Write a required `.figure-claim` caption that states the exact visible fact
   the image proves. Do not use captions such as "Screenshot" or "See above."
3. Add a `.figure-as-of` line for every image. Include a machine-readable
   `<time datetime="YYYY-MM-DD">` capture date and name the provenance as
   `real`, `mocked`, `composite`, or `pinned`.
4. Use at most one accented `.figure-annotation` for each finding in a capture.
   If one image needs several accents to make several claims, split it into
   separate figures. Prefer annotations rendered by the capture scenario over
   manual image editing.
5. Include light and dark captures when theme tokens, contrast, status, or
   visual hierarchy are part of the claim. A single theme is sufficient when
   the finding is demonstrably theme-independent.
6. Give every image useful alternative text. The alt text identifies the
   surface and finding; the caption carries the evidentiary claim.

The copyable template contains an inert
`<template data-article-example="evidence-figure">` block with the canonical
two-theme structure. It does not load placeholder images in a newly scaffolded
Dossier. The parallel `AOW-W1` Dossier extension is the first worked reference
for this standard. Refer to its integrated figure as the example once
available; do not duplicate its screenshots or dossier-specific CSS here.

### Capture and storage

Keep captures local to the Dossier. The publication convention is
`docs/operations/<slug>/assets/`. A concept delivery that still lives at
`docs/<slug>/` uses `docs/<slug>/assets/`; preserve that adjacent `assets/`
directory if the Dossier is later published under `docs/operations/`.

Use descriptive kebab-case filenames in this shape:

```text
<surface>-<finding>-<theme>--<provenance>.png
```

For example, use
`activity-stream-watcher-finding-light--pinned.png` and its `dark` counterpart,
not `screenshot-1.png`. Keep the capture date in the figure as-of line, where a
reader can see it, rather than relying on file metadata.

Use the [presentation capture runbook](setup/presentation-capture.md) and its
[`presentation-capture.spec.ts`](../../frontend/e2e/visual-evidence/presentation-capture.spec.ts)
for deterministic pinned documentation captures. For surfaces outside that
catalogue, follow the browser readiness and page-error checks in
[`scripts/stable-frontend-boot-probe.mjs`](../../scripts/stable-frontend-boot-probe.mjs)
before taking a capture. A task worktree may start the dev backend only from a
Playwright spec through
[`frontend/e2e/fixtures/dev-backend.ts`](../../frontend/e2e/fixtures/dev-backend.ts).
The boot probe establishes that the page is ready; it is not itself a capture
output.

Copy and replace this structure after the capture files exist:

```html
<figure class="evidence-figure media-breakout">
  <div class="evidence-captures">
    <div class="evidence-capture">
      <span class="capture-theme">Light</span>
      <img src="assets/surface-name-visible-finding-light--pinned.png"
           alt="Name the visible surface and the finding shown in the light theme.">
      <span class="figure-annotation">One finding</span>
    </div>
    <div class="evidence-capture">
      <span class="capture-theme">Dark</span>
      <img src="assets/surface-name-visible-finding-dark--pinned.png"
           alt="Name the same visible surface and finding in the dark theme.">
    </div>
  </div>
  <figcaption>
    <span class="figure-claim"><b>Evidence.</b> State the exact visible fact this figure proves.</span>
    <span class="figure-as-of">
      <span><b>Light:</b> captured <time datetime="2026-08-11">11 August 2026</time> from the named provenance.</span>
      <span><b>Dark:</b> captured <time datetime="2026-08-11">11 August 2026</time> from the named provenance.</span>
    </span>
  </figcaption>
</figure>
```

## Migration policy and references

Do not rewrite active documents in bulk. Adopt the template and add `pattern`
only when a document is already being changed. The first references are:

- [`deck-icon-exploration`](deck-icon-exploration/index.html), `pattern: ui`;
- [`workbench-konzept`](workbench-konzept/index.html), `pattern: concept`;
- the parallel `AOW-W1` Dossier extension, the first screenshot-evidence
  reference once its owning card is integrated.

When adapting an older document, preserve its evidence and local visual proof,
replace the common page frame with the canonical v2 block, then keep only the
CSS that is genuinely specific to that document.

## Website follow-up

The article presentation is also intended to become a central element of the
Agent Studio website. Create a separate WEB card after the naming decision.
That follow-up owns website composition and copy; this slice documents the
intent and does not build the website surface.
