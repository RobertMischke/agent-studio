# Visual Documentation Library

This folder is the source of truth for product screenshots that explain Agent
Studio features.

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

This implementation uses existing product data from the current Agent Studio
workspace. The images come from
`frontend/e2e/visual-evidence/readme-screenshots.spec.ts`, which opens the
board and selects an existing task with useful review and Git context. Do not
introduce synthetic product states for this library just to make a prettier
screenshot.

If a feature needs a new visual state, first add or identify a real data state,
then document the Playwright route to that state.

## Regenerating screenshots

Generate the current visual documentation set from the product repo root:

```sh
./scripts/visual-docs/generate.sh
```

The script runs the Playwright screenshot recipe and then validates the manifest.
It writes the generated PNG files to [`docs/images/`](../images/).

You can also run the Playwright recipe directly from the frontend folder:

```sh
cd frontend
PW_TARGET=dev npm run docs:visual
```

Preconditions:

- the dev frontend is reachable at the configured Playwright target
- the backend exposes at least one existing project with visible task cards
- the spec can write to `../docs/images/`

The spec uses existing task data. Preferred task keys are kept in the spec so
the generated set stays visually stable, but the run can fall back to the first
visible task card.

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
npm run sync:product-visuals -- --product ../../agent-taskboard-dev
```

That script reads this manifest, copies the referenced images, and regenerates
the website's product-visual data file.

## GitHub Pages

This folder is intentionally GitHub-Pages-friendly: Markdown files plus images
are enough for a first rendered documentation site. A later pass can add a
small static index, but the source of truth should stay here.
