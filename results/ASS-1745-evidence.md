# ASS-1745 — Nav-Umbau Schritt 3: Pipeline-Seite ueberarbeiten (T4a)

Reworked the project-level **Pipeline** page into a calm, text-only CSS-grid panel
(`app-project-pipeline-panel`) that owns every pipeline configuration that used to be
scattered across Project Settings. Mounted on the Pipeline rail in both shell hosts
(`project-overlays` + `project-hub-view`).

## What the page now does (all of today's pipeline config, in one place)

- **Step list, grouped pre / core / post** from `/api/projects/pipeline-catalogue`,
  with the phase sections rendered as `CORE` / `TOOL STEPS` / `ASPECT REVIEWS` / `DRIFT`.
- **Activation + ordering** per step: enable toggle (`pipeline-step-enabled-{id}`) and
  up/down move buttons (`pipeline-step-move-up|down-{id}`). Core is locked "always on".
- **Per-step model** via the shared CLI+model picker (`pipeline-step-agent-{id}`),
  persisted through `PUT /api/projects/{name}/pipeline-step`.
- **Prompt BINDING (not content)**: each prompt-bearing step shows a registry reference
  `⟐ <template>` + a **Manage in Prompts ↗** deep-link (`openPrompts` → Prompts rail).
  Content is managed only in the Prompt Registry (T3a). The legacy inline-override
  escape hatch renders an `INLINE OVERRIDE` badge + **Clear** when an inline prompt
  still exists.
- **Gate / run-condition controls**: gate mode select for orchestrator gate steps
  (`pipeline-step-mode-{id}`); a WHEN run-condition select + value input for the
  abort-review step (`pipeline-step-condition-{id}` / `-value-{id}`).
- **Cost / tokens per step kind**: a `COST & TOKENS · LAST 30 DAYS` rollup with a
  per-kind legend (`pipeline-cost-legend-{kind}`) and total (`pipeline-cost-total`)
  from `/api/projects/{name}/token-usage/pipeline-cost`.

## Screenshots

- `pipeline-page/pipeline-page-full--mocked.png` — full page, deterministic mocked
  catalogue/overrides/cost so every control is visible (pinned model, inline override,
  enabled run-condition, cost legend).
- `pipeline-page/pipeline-page-section--mocked.png` — tight crop of the panel only.
- `pipeline-page/pipeline-page-full--real.png` — full page against the **live backend**
  (no route mocks); proves the panel renders the real `standard-task-pipeline` catalogue.
- `pipeline-page/pipeline-page-section--real.png` — section crop, real data.

`--mocked` = panel data is route-mocked (component + SCSS are the real build).
`--real` = catalogue/overrides/cost come from the running stack.

## Verification

- `npm run build` (production) green; component/structure/CSS lints clean for the new
  files (only pre-existing baseline debt remains, in files this task did not touch).
- Unit spec `project-pipeline-panel.component.spec.ts` green (smoke + render + openPrompts).
- Playwright evidence specs green:
  `pipeline-page-evidence.spec.ts` (mocked) and `pipeline-page-evidence-real.spec.ts` (real).
- Contract specs updated to the renamed section testid (`project-detail-pipeline`) and to
  a stable `data-enabled` attribute instead of styling-dependent CSS-class assertions:
  `pipeline-step-config.spec.ts`, `pipeline-drift-steps.spec.ts` (`nav-rebuild-t5b.spec.ts`
  already compatible).
