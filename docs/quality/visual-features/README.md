# Visual Documentation Library

This folder is the source of truth for product screenshots that explain Agent
Studio features.

The goal is simple: every relevant feature should eventually have one
meaningful screenshot, one plain-language explanation, and one reproducible
capture recipe. The screenshot is not decoration. It is documentation evidence.

## Source of truth

[`manifest.json`](./manifest.json) owns the image inventory.

Each entry records:

- the feature the image explains
- the existing screenshot file
- the relevant product state shown in that screenshot
- the Playwright command and data preconditions that recreate it
- where the image is used in downstream surfaces such as the marketing site

Feature pages under [`features/`](./features) are the human-readable docs. They
must reference entries from the manifest, not invent separate image metadata.

## Pinned-data rule

Documentation and marketing images use the committed, sanitized snapshot in
`scripts/presentation-capture/pinned-seed.json`. The snapshot may be derived
from a real board and task, but capture never reads that workspace live. An
operator updates the snapshot explicitly with
`scripts/presentation-capture/export-pinned-seed.mjs`, reviews the anonymized
diff, and commits the new fixed state. These images use the `--pinned` filename
suffix. The `--real`, `--mocked`, and `--composite` suffixes remain the separate
provenance grammar for task-run evidence.

## Regenerating screenshots

Generate the current visual documentation set from the product repo root:

```sh
npm --prefix frontend run docs:presentation
```

The command resets an isolated workspace, runs the presentation and visual
library recipes, and then validates the manifest.
It writes the generated PNG files to [`docs/assets/images/`](../../assets/images).
It uses `PW_VISUAL_CAPTURE=marketing`, which hides local-only dev chrome and
live quota widgets before writing screenshots.

The compatibility wrapper delegates to the same command:

```sh
./scripts/visual-docs/generate.sh
```

Preconditions:

- frontend dependencies and the .NET SDK are installed
- the spec can write to `docs/assets/images/`
- marketing capture mode is acceptable for public docs, so local-only DEV
  markers are hidden from generated product images

The spec selects pinned `DEMO-9`; there is no fallback to ambient workspace
data.

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
