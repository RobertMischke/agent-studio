# Wiki grading run (global maintenance pass)

Status: Concept (living). Slice GRADE-1 implemented (2026-07-10, AGT-2051).

> Operator intent (2026-07-10): "Every wiki page should carry a machine-made
> assessment (grade + short feedback: is it outdated? contradictory? gaps?). Add
> a global trigger on the Pulse dashboard that walks all pages with a
> **relatively strong** model and writes those reports. Badly graded pages must
> surface in Pulse. The model is chosen at the trigger, defaulting from a new
> workspace maintenance-model setting, and the run rides the normal CLI rail so
> quota stays visible and there is no parallel storm."

This concept extends the deterministic [Wiki Pulse dashboard](wiki-pulse-dashboard.md)
(PULSE-1) with an **LLM grade per page**. Pulse's own drift bar stays
deterministic (commit-count heuristic, no LLM); the grading run is a separate,
opt-in, operator-triggered pass whose verdict *supplements* that heuristic.

## 1. Report per page ("meter document")

Each wiki page gets a machine-generated assessment stored in the **existing
companion sidecar** (`<source>.meta.json`, the
[wiki document companion schema](../app/schemas/wiki-document-companion.schema.json)),
**not** as a new wiki page. The verdict lands in a new, optional `grading`
block, kept deliberately separate from the deterministic `drift` block:

```jsonc
"grading": {
  "grade": "C",                 // A | B | C | D | unknown
  "assessment": "One-paragraph verdict shown in Pulse and the meta rail.",
  "outdated": true,             // does it describe stale behaviour?
  "contradictory": false,       // does it contradict itself / other pages?
  "gaps": true,                 // are there material gaps?
  "notes": ["evidence line", "evidence line"],
  "cli": "claude",
  "model": "claude-sonnet-5",
  "thinkingLevel": null,
  "method": "wiki-grading-run",
  "runId": "wg-...",
  "gradedAt": "2026-07-10T12:00:00Z",
  "sourceFingerprint": { "algorithm": "sha256", "hash": "…", "sizeBytes": 0, "lineCount": 0, "capturedAt": "…" },
  "ok": true                    // true when a real model reply was parsed
}
```

The `sourceFingerprint` is the sha256 of the graded page content. It is what
makes the run **idempotent**: on a re-run, a page whose fingerprint still matches
its stored `grading.sourceFingerprint` (and whose model is unchanged) is skipped
unless the operator forces a re-grade. When a page has no companion yet, the run
writes a **schema-valid minimal companion** (title, source fingerprint, report
path, unknown classification/drift, empty findings) carrying the `grading` block,
so the sidecar mechanism, tree chips, and generated report stay consistent.

## 2. Global trigger on the Pulse dashboard

The Pulse landing surface hosts a **Grade all pages** control. Starting a run:

- enumerates every wiki document (`.md` / `.html` / `.json`, companions and
  frame shells excluded), optionally capped by a `limit` for a cheap probe;
- grades each page and writes its `grading` block;
- reports live progress (`processed / total`, graded / skipped / failed, the
  current page), is **abortable** mid-run, and is **idempotent** on repeat.

One run per project at a time. The run is fire-and-forget on the server with an
in-memory status registry the UI polls; a backend restart ends a run (the
written companions are already durable on disk).

## 3. Model choice — where it comes from (the placement decision)

**Decision: model + level are chosen at the trigger, defaulting from a new,
dedicated workspace maintenance-model configuration class. Maintenance runs are
NOT the project pipeline models reused.**

Rationale:

- A wiki-grading pass is a **workspace-wide maintenance activity**, not part of
  any one task's pipeline. Reusing `ProjectSettings` / pipeline step models
  would couple a cross-cutting janitorial run to per-project delivery config and
  make "grade everything with one strong model" impossible to express in one
  place.
- It is therefore its **own configuration class**:
  `WikiMaintenanceModelService` persists `wiki-maintenance-model.json` beside
  `cli-model-routing.json` and `cli-quota-caps.json` at the workspace metadata
  root. It lives in the **consolidated CLI-management section** (the AGT-2039 /
  AGT-2040 area that already owns model routing and quota caps), because that is
  where operators reason about "which model runs which class of work".
- The default is a **relatively strong** model (`claude-sonnet-5`) rather than
  the cheap Haiku the automatic drift post-step uses: a maintenance grade is a
  low-frequency, high-value judgement, so it is worth a stronger model. The
  operator can raise it to Opus or lower it per workspace.
- At the trigger the operator still picks model + level (pre-filled from the
  maintenance default) via the shared `app-cli-model-selector`, so a one-off run
  can deviate without editing the workspace default.

Resolution order: **trigger choice → workspace maintenance default → platform
strong default (`claude-sonnet-5`)**.

## 4. Critical pages visible in Pulse

Pulse gains a **Critical pages** section built from the companion `grading`
blocks: every page graded `C` or `D`, worst first, each showing its grade, the
one-line assessment, the grading model, and a click-through to the page (opening
its companion report tab). This is the "LLM grade supplements the deterministic
drift heuristic" surface — the drift bar answers *how stale is the knowledge by
commit count*; the critical list answers *which pages a strong model judged
weak*. An ungraded wiki reads as an empty, healthy state with a hint to run a
pass.

## 5. Cost / quota respect

- The run rides the **normal one-shot CLI rail** (`ICliOneShot` /
  `CliOneShotRegistry`, the same rail the drift post-step uses), tagged
  `Source = "wiki-grading"`, so every call is recorded through
  `AdHocUsageRecorder` and mirrored onto the Agent Message Bus — spend shows up
  in the per-source usage breakdown like any other rail traffic.
- Pages are graded **sequentially with pacing** (concurrency 1, a short delay
  between pages): batching, no parallel storm.
- A `limit` on the run keeps a probe cheap (3–5 pages) before committing to the
  full tree.

## 6. Where it lives

- Backend: `AgentStudio.Docs.Grading.WikiGradingService` orchestrates a run
  (`backend/Features/Docs/Grading/`); `IWikiPageGrader` is the grader seam with
  a production `CliWikiPageGrader` (one-shot rail) and a deterministic
  `HeuristicWikiPageGrader` fallback used for offline probes and tests;
  `WikiCompanionStore` reads/merges/writes the `grading` block;
  `WikiMaintenanceModelService` owns the maintenance-model config. Endpoints in
  `WikiGradingEndpoints` (`POST /api/projects/{p}/wiki/grading/run`,
  `GET …/wiki/grading/status`, `POST …/wiki/grading/abort`, and
  `GET/PUT /api/cli/maintenance-model`). Pulse's `Critical` section is composed
  in `ProjectDocsService.GetWikiPulse` from the companion index.
- Frontend: the Pulse trigger + progress + critical tile live in
  `app-wiki-pulse`; the maintenance-model editor is a row in the CLI admin
  panel. Both reuse `app-cli-model-selector` and `CliCatalogStore`.
- Tests: `backend.Tests/WikiGradingServiceTests.cs` (real temp git repo, stub +
  heuristic grader) and the Pulse critical-section coverage in
  `WikiPulseTests.cs`; `wiki-pulse.component.spec.ts` and a mocked Playwright
  spec for the trigger + critical tile.

## 7. Scope boundary

In GRADE-1: the companion `grading` block, the global run (progress / abort /
idempotency / batching) over the one-shot rail, the maintenance-model config
class, the Pulse critical section, and the trigger UI. **Not** in GRADE-1:
scheduled/automatic grading (this is operator-triggered only), cross-page
contradiction graphs, and auto-created follow-up tasks from a bad grade.
