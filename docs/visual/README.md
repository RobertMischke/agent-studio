# Visual Documentation Library

This folder is the source of truth for product screenshots that explain Agent
Task Processor features.

The goal is simple: every relevant feature should eventually have one
meaningful screenshot, one plain-language explanation, and one reproducible
capture recipe. The screenshot is not decoration. It is documentation evidence.

## Source of truth

[`manifest.json`](manifest.json) owns the image inventory.

Each entry records:

- the feature the image explains
- the existing screenshot file
- the relevant product state shown in that screenshot
- the Playwright command and data preconditions that recreate it
- where the image is used in downstream surfaces such as the marketing site

Feature pages under [`features/`](features/) are the human-readable docs. They
must reference entries from the manifest, not invent separate image metadata.

## Existing-data rule

This first implementation uses the existing README screenshots under
[`docs/images/`](../images/). Those images come from the existing
`frontend/e2e/visual-evidence/readme-screenshots.spec.ts` Playwright recipe and
the existing "Sample Shop" demo workspace. Do not introduce synthetic product
states for this library just to make a prettier screenshot.

If a feature needs a new visual state, first add or identify a real data state,
then document the Playwright route to that state.

## Regenerating screenshots

Current screenshot source:

```sh
cd frontend
PW_TARGET=dev npx playwright test e2e/visual-evidence/readme-screenshots.spec.ts --project=chromium
```

Preconditions:

- the dev frontend is reachable at the configured Playwright target
- the backend watch paths include the existing `Sample Shop` demo workspace
- the spec can write to `../docs/images/`

The spec intentionally skips when `Sample Shop` is not configured. That keeps
normal E2E runs from fabricating demo data.

## Validating the library

```sh
node scripts/visual-docs/validate.mjs
```

The validation checks that every manifest entry has an existing feature doc,
an existing image, a capture recipe, and documented marketing usage.

## Marketing sync

The marketing website should not guess image paths. It should read this manifest
and copy the listed images to its public assets folder.

From the website repo:

```sh
cd 04-angular-static-final
npm run sync:product-visuals -- --product ../../agent-taskboard-visual-docs
```

That script reads this manifest, copies the referenced images, and regenerates
the website's product-visual data file.

## GitHub Pages

This folder is intentionally GitHub-Pages-friendly: Markdown files plus images
are enough for a first rendered documentation site. A later pass can add a
small static index, but the source of truth should stay here.
