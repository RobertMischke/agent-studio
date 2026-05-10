# project-detail

The full per-project page: project shell (left rail + body), per-rail panels (security, uxui, observability, product-runtime, steering, token-usage), legacy project-detail panel, plus per-project overlays.

Cycle 9f Round 4 merged `project-shell` and `project-detail` into one feature folder per the design model — they're the same surface in the user's mental model.

## Public API

Imports via `from './features/project-detail'`. See [`index.ts`](./index.ts).

**State services**:

- `ProjectOverlaysService` (Cycle 9g) — open/close + URL-hash sync for the four per-project overlays (orch-feed / project-detail / project-shell / analysis-report).

**Container components**:

- `ProjectOverlaysComponent` — renders the 4 overlays in one place; the shell mounts it once.
- `ProjectShellComponent` — the project page's left-rail + body shell.
- `ProjectDetailComponent` — legacy per-project settings overlay.
- `AnalysisReportDrilldownComponent` — drill-down for one analysis report.
- `AutonomySliderComponent` — the autonomy slider used in project settings.

**Rail panels** (per-rail content shown inside ProjectShell):

- `SecurityPanelComponent` — slice 1 of the quality-system mockup.
- `UxuiPanelComponent` — slice 6.
- `ProjectObservabilityPanelComponent`, `ProjectProductRuntimePanelComponent` — observability + runtime telemetry.
- `ProjectSteeringDocsSectionComponent` — steering docs viewer (also embedded in ProjectShell).

**Project-shell config**:

- `DEFAULT_PROJECT_RAIL_KEY`, `ProjectRailKey`, `isProjectRailKey`, `toProjectSlug` — URL hash slug + rail-key validation.

**Types**: security + uxui panel response shapes (re-exported from their `.types.ts` files for the API services that wrap the backend).

## Notable

- **Cross-overlay nav** lives in `ProjectOverlaysService.openFeedFromShell` / `openFeedFromDetail` — the two patterns where clicking one overlay swaps to another.
- **Per-rail follow-ups** (security / uxui "Create follow-up task") bubble up to the shell because they trigger the create-job-dialog whose form state is in `features/board/state/create-job-form.service.ts`.
- The project-shell URL hash is `#/projects/<slug>` or `#/projects/<slug>/<rail-key>`. Slug → name resolution requires the workspace watch-paths; service exposes `syncShellFromHash(watchPaths)` for the shell to call on `hashchange`.

## Sub-folders

- `components/` — shell + 5 rail panels + the legacy project-detail + the analysis-report drilldown + 7 small section components (`project-*-section.ts`) that compose into the panels. Several panels are large (>1000 LOC) and candidates for further internal splits.
