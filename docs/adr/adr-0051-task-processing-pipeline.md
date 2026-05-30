# ADR-0051 - Task Processing Pipeline (CI/CD-style): configurable pre/post steps, timeline, per-step artifacts, DB-backed history

**Status.** Proposed (concept). This is a design deliverable, not an implementation. On acceptance it folds into [architecture-decisions.md](../architecture-decisions.md) as ADR-0051 and supersedes the relevant `Status` lines of [ADR-0045](../architecture-decisions.md#adr-0045---task-processing-as-a-first-class-pipeline-of-pre--core--and-post-steps-2026-05-29). Implementation is sliced in follow-up tasks (see [Slicing plan](#slicing-plan)).

**Date.** 2026-05-30.

---

## 1. Summary

Wrap every task run in a freely configurable, CI/CD-style pipeline of pre- and post-processing steps. Each step is one of two kinds (an LLM step or a script step), shares a common envelope (`failureMode`, `orchestratorReaction`, `order`, `enabled`), and produces a reviewable markdown artifact. The planned sequence is visible up front in the task timeline; the executing step shows live progress; each finished step shows status, duration, attempt count, its artifact, and the orchestrator's verdict. Per-step run telemetry is stored with history behind the API so the product can answer the CI/CD-style questions: how often does this step run, what is its p95 duration, what is its failure rate, how is it trending.

This is the configuration + history + UX layer on top of [ADR-0045](../architecture-decisions.md#adr-0045)'s `TaskPipeline` foundation. ADR-0045 made the pipeline a first-class in-process concept with a code-constant catalogue and a per-job `pipeline-execution.json`. This ADR makes it (a) **per-project configurable and reorderable**, (b) **AI-configurable**, (c) **two explicit step types** with a build-server semantics, and (d) **queryable across tasks over time**.

## 2. Motivating incident (the build gate)

On 2026-05-29, commit `0aa9242` was auto-pushed to `main` with a compile error: `EnsureUniqueSlug` was called but never defined. `main` did not build, and the broken commit propagated into the stable update. A `dotnet build` step configured as a **hard gate** in the post-work / pre-push pipeline would have caught it before the push: a script step, `failureMode: hard`, `orchestratorReaction: scripted` (exit 0 = pass, non-zero = fail, no LLM judgement needed). This pipeline is that gate, generalised to lint, build, baseline tests, smoke tests, and orchestrator-reviewed artifact steps.

## 3. The step model: two types, one envelope

Every step shares a common envelope. The body is one of two kinds.

### 3.1 Common envelope

| Field | Meaning |
|---|---|
| `id` | Stable slug, unique within the pipeline definition. |
| `label` | Human-readable name shown in the editor and the timeline. |
| `phase` | `pre` or `post`. (`core` is the agent run itself and is not user-editable; see ADR-0045.) |
| `order` | Integer sort key within the phase. Drag-to-reorder in the editor rewrites these. |
| `enabled` | When false the step is shown greyed in the timeline and skipped at runtime. |
| `type` | `llm` or `script`. The body discriminator. |
| `failureMode` | `hard` or `soft`. `hard` fails the pipeline (gate); `soft` records a warning and continues. Per step, user-configurable. |
| `orchestratorReaction` | `review` (default) or `scripted`. How the orchestrator treats the step's artifact. See [section 4](#4-orchestratorreaction-semantics). |
| `model` | LLM steps only. Per-step model override; reuses the shared CLI+model selector (ASS-544/562). Resolution order: step -> job -> project -> client default (same as ADR-0045 `PipelineStep.Model`). |
| `command` | Script steps only. The shell command (e.g. `dotnet build`, `npm run lint:scss`). |
| `timeoutMs` | Optional wall-clock cap; on breach the step fails with reason `timeout`. |
| `artifact` | Every step produces one markdown document. For LLM steps it is the model output; for script steps it is the captured stdout/stderr plus a verdict header. |

### 3.2 Type A: LLM step

- Inputs: a prompt + a model (per-step selection via the shared selector).
- Produces a markdown artifact (the model's output).
- **Progress is visible while it runs** (it is a CLI/LLM call, so the timeline streams elapsed time + token counts the same way the core agent run does).
- The four existing aspect runs (`aspect-requirement-fit`, `aspect-code-quality`, `aspect-documentation-impact`, `aspect-tests-and-evidence`) are LLM steps in this model. They already exist as `StepKind.Aspect` post-steps (ADR-0045).

### 3.3 Type B: script step

- A shell command. Exit-code driven.
- stdout/stderr are captured into the artifact, prefixed with a verdict header (`exitCode`, `pass|fail`, `durationMs`).
- This is how lint / build / baseline-tests / smoke-tests are expressed. `npm run lint:scss`, `dotnet build`, `dotnet test --filter Category=Smoke`, `npm run e2e:smoke`.
- The existing `post-lint-scss` step (ASS-563) is already a script-shaped `StepKind.Tool` step; it becomes a configured script step.

### 3.4 Reconciliation with ADR-0045's `StepKind`

ADR-0045 already has a `StepKind` enum (`Module`, `Core`, `Aspect`, `Orchestrator`, `Tool`) that binds a step to a concrete built-in service. That stays as the **internal implementation binding** for the steps the product ships by default. The new `type` (`llm` | `script`) is the **user-facing body discriminator** for configurable steps:

- A user-added script step persists as `kind: Tool, type: script`.
- A user-added LLM step persists as `kind: Module, type: llm` (or `kind: Aspect` when it is an aspect-style review).
- The built-in `core-agent-run` keeps `kind: Core` and is not user-editable.

So `StepKind` does not disappear; it gains a sibling `StepType` and a few envelope fields (`Phase`, `Order`, `Enabled`, `FailureMode`, `OrchestratorReaction`, `Command`, `Prompt`). The standard pipeline keeps working unchanged; it is simply re-expressed as the default project definition (see [section 6.1](#61-pipeline_definitions-source-of-truth--versioned-json)).

## 4. `orchestratorReaction` semantics

What the orchestrator does with a step's artifact after it finishes.

### 4.1 `review` (default)

The orchestrator reads the artifact and makes an LLM judgement: **pass**, **reopen-with-feedback** (reissue the core work with the artifact as steering), or **escalate** (hand to human review). This is the normal case and is the novel part of this design that has no CI equivalent.

It runs through the existing contract-bounded-agent pattern ([ADR-0032](../architecture-decisions.md#adr-0032)): the step's artifact + a typed input contract go in, a schema-validated output contract (`category`, `confidence`, `proposedAction`) comes out, and **the rule engine decides** the actual pipeline action via a fixed table. The agent classifies; it does not decide whether to halt. Schema-invalid output fails closed to escalate-human. Every reopen ticks the completion-loop budget (ASS-566) and is registered in [loop-inventory.md](../loop-inventory.md).

### 4.2 `scripted` (the exception)

Deterministic handling, no LLM review. Pass/fail strictly on the step's own result. A build step: exit 0 = pass, non-zero = fail. No tokens, no clock, no model call. This is the build-gate path from the motivating incident and the same deterministic spirit as the commit-attribution post-step ([ADR-0050](../architecture-decisions.md#adr-0050), which runs `scripted`).

### 4.3 The two axes are independent

`failureMode` and `orchestratorReaction` are orthogonal:

| Step | type | failureMode | orchestratorReaction | Effect |
|---|---|---|---|---|
| `dotnet build` | script | hard | scripted | Build break blocks the pipeline before push. Deterministic. The 0aa9242 gate. |
| `npm run lint:scss` | script | soft | scripted | Lint noise warns but does not block. |
| `aspect-code-quality` | llm | soft | review | Orchestrator reads the artifact, may reopen with feedback. |
| `smoke-tests` | script | hard | review | Failure blocks, but the orchestrator reads the captured output first and may reopen the core work with a targeted fix prompt rather than escalating. |

## 5. Configuration surfaces

### 5.1 Project level: the pipeline editor

The ordered list of pre-steps and post-steps for the project. Drag-to-reorder; add / remove / enable; set `type`, `model` (if LLM), `failureMode`, `orchestratorReaction` per step. Mutations follow the optimistic-UI default ([ADR-0046](../architecture-decisions.md#adr-0046)): the local signal updates synchronously, the PUT is fire-and-forget, a server rejection rolls back with a toast. Reorder reuses the kanban drag-snapshot pattern (`applyOptimisticReorder` / `revertOptimisticReorder`).

**AI-assisted config.** The operator describes the pipeline in natural language ("lint scss, build the backend, run smoke tests, then have the orchestrator review the diff against the prompt's intent"). An LLM proposes the ordered step list + per-step config as a draft definition; the operator tweaks and saves. The proposal is a draft `pipeline_definition` the operator confirms; it is never auto-applied. This is itself an LLM step bounded by ADR-0032 (schema-validated proposal, human confirms).

Mockup: [mockups/task-processing-pipeline/pipeline-editor.md](../mockups/task-processing-pipeline/pipeline-editor.md).

### 5.2 Task-detail level: the timeline (read / observe)

The timeline shows **all steps from the start** (the planned sequence, greyed/pending), then **live progress** on the executing step. Per step: state (pending -> running -> ok / failed / warn / skipped), duration, attempt count, a link to the artifact document, and the orchestrator's verdict. LLM steps show streaming progress while running.

This renders on the unified `timeline.jsonl` ledger ([ADR-0049](../architecture-decisions.md#adr-0049), ASS-560), which already carries `pre_step_started` / `pre_step_finished` / `post_step_started` / `post_step_finished` event kinds. The pipeline is the backbone of the timeline the operator has repeatedly asked for.

Mockup: [mockups/task-processing-pipeline/task-timeline.md](../mockups/task-processing-pipeline/task-timeline.md).

## 6. Data and persistence

### 6.1 The DB decision, and why it is NOT EF Core

The task framing says "we need a database behind the API" and assumed the backend "already uses SQLite/EF Core". **It does not use EF Core.** Two ADRs explicitly rule out a database engine as a source of truth:

- [ADR-0023](../architecture-decisions.md#adr-0023): "A database engine. No SQLite, no LiteDB, no EF, no embedded server." The cross-cutting data layer is file-backed JSONL + an in-memory `InMemoryStore<T>` projection.
- [ADR-0024](../architecture-decisions.md#adr-0024): "A database engine. No SQL, no LiteDB, no EF. Files plus an in-memory index."

There is exactly one SQLite usage in the codebase: [`ProjectChatIndex`](../../backend/Services/ProjectChat/ProjectChatIndex.cs), a per-project FTS5 index. Its docstring states the load-bearing precedent: *"the markdown files are the source of truth (ADR-0023). A missing or corrupt index is non-fatal - callers ask EnsureFresh to rebuild it."* It uses raw `Microsoft.Data.Sqlite` (already a `csproj` dependency), an idempotent `CREATE TABLE IF NOT EXISTS` bootstrap, and a rebuild-from-files path. No EF Core, no migrations framework.

**Decision: extend the `ProjectChatIndex` precedent, not introduce EF Core.** The pipeline keeps folders + JSONL as the source of truth and adds a **derived, rebuildable SQLite analytics index** for the cross-task aggregate queries that a `List<T>` filter serves poorly (p95, failure rate, trend over time, per-step histograms). This threads the needle:

- It honours ADR-0023/0024: no DB as source of truth, no EF, no aggregate documents.
- It serves the task's real requirement: CI/CD-style history + aggregates need indexed range/group queries that the in-memory projection was explicitly not designed for.
- It is precedented: SQLite-as-rebuildable-index already ships in `ProjectChatIndex`.
- It is cheap to reason about: drop the `.db`, it rebuilds from the JSONL truth on next access.

Source of truth stays on disk; the DB is additive.

### 6.2 `pipeline_definitions` (source of truth = versioned JSON)

Per project, the ordered steps + their config. **Versioned** so the history of config changes is kept (a new save writes a new version; old versions are retained for forensics and so a `step_run` can name the exact definition version it ran against).

Source of truth: `<TaskRepository>/.metadata/pipelines/<projectId>/<version>.json` (consistent with the registry under `.metadata/`, [ADR-0042](../architecture-decisions.md#adr-0042)) plus a `current.json` pointer. Validated against [`pipeline-definition.schema.json`](../schemas/pipeline-definition.schema.json). The default content is today's `standard-task-pipeline` ([PipelineCatalogue](../../backend/Services/Pipeline/PipelineCatalogue.cs)) serialised as version 1, so existing behaviour is the seeded default.

The derived DB mirrors each definition version into a `pipeline_definitions` table so `step_runs` can join to it for the aggregate views.

### 6.3 `step_runs` (source of truth = per-job JSONL; analytical mirror = DB)

One record per `(task, step, attempt)`: `startedAt`, `finishedAt`, `durationMs`, `status` (ok / failed / warn / skipped), and the type-specific payload: `exitCode` (script) or `inputTokens` / `outputTokens` / `cost` / `model` (llm). Plus `artifactRef`, `orchestratorVerdict`, the `runAttempt` / `runId` of the task it belonged to, and the `pipelineDefinitionVersion` it executed against.

Source of truth: this is already half-present on disk. ADR-0045's `pipeline-execution.json` holds per-step execution for the *current* run; ADR-0049's `timeline.jsonl` holds the *event stream* of step starts/finishes across runs. This ADR adds a per-job append-only `logs/step-runs.jsonl` (one row per finished step-attempt, validated against [`step-run.schema.json`](../schemas/step-run.schema.json)) as the durable, replayable, per-task truth. The derived DB's `step_runs` table is the **cross-task projection** built by walking every project's `step-runs.jsonl`, the same rebuild shape as `ProjectChatIndex.EnsureFresh`.

### 6.4 Aggregates / queries

Served from the derived DB. The CI/CD-style "how is this step behaving" view:

- Per step: execution count, avg / p50 / p95 / max duration, failure rate, warn rate.
- Trend over time: failure rate and p95 bucketed by day/week.
- Per project: slowest steps, flakiest steps (high warn-with-eventual-pass), most-reopened steps.

These are SQL `GROUP BY` + window queries over the `step_runs` table, exposed via `GET /api/projects/{projId}/pipeline/stats` and a per-step drill-down. They are read-only projections; a corrupt or missing `.db` is non-fatal and rebuilds from the JSONL truth.

### 6.5 SQLite DDL (the derived index)

Idempotent bootstrap, `ProjectChatIndex.EnsureSchema` style. No EF, no migration framework; schema changes are additive `ALTER TABLE` / `CREATE TABLE IF NOT EXISTS` guarded by a `meta.schema_version` row, with a full rebuild-from-JSONL as the fallback when an additive change is not possible.

```sql
-- Derived analytics index. Source of truth is on-disk JSONL/JSON.
-- Drop this file and it rebuilds from <project>/.metadata/pipelines/*
-- and <job>/logs/step-runs.jsonl on next access (EnsureFresh).

CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);  -- schema_version, last_built (ISO-8601 UTC)

-- Mirror of the versioned JSON definitions, for joins + the editor history view.
CREATE TABLE IF NOT EXISTS pipeline_definitions (
    project_id   TEXT NOT NULL,
    version      INTEGER NOT NULL,
    created_at   TEXT NOT NULL,          -- ISO-8601 UTC
    created_by   TEXT,                   -- client identity id
    step_count   INTEGER NOT NULL,
    body_json    TEXT NOT NULL,          -- the full definition document
    PRIMARY KEY (project_id, version)
);

-- One row per (task, step, attempt). The analytical core.
CREATE TABLE IF NOT EXISTS step_runs (
    id                 TEXT PRIMARY KEY,    -- stable hash of (jobKey, step_id, attempt)
    project_id         TEXT NOT NULL,
    job_id             TEXT NOT NULL,
    job_key            TEXT NOT NULL,       -- watchPath::jobId
    pipeline_def_ver   INTEGER NOT NULL,
    step_id            TEXT NOT NULL,
    step_label         TEXT NOT NULL,
    phase              TEXT NOT NULL,       -- pre | post
    step_type          TEXT NOT NULL,       -- llm | script
    failure_mode       TEXT NOT NULL,       -- hard | soft
    reaction           TEXT NOT NULL,       -- review | scripted
    run_id             TEXT,                -- the task run/attempt this belonged to
    attempt            INTEGER NOT NULL,
    status             TEXT NOT NULL,       -- ok | failed | warn | skipped
    started_at         TEXT NOT NULL,       -- ISO-8601 UTC
    finished_at        TEXT,
    duration_ms        INTEGER NOT NULL DEFAULT 0,
    exit_code          INTEGER,             -- script steps
    model              TEXT,                -- llm steps
    input_tokens       INTEGER,
    output_tokens      INTEGER,
    cache_read_tokens  INTEGER,
    cost_usd           REAL,
    artifact_ref       TEXT,                -- relative path to the markdown artifact
    orchestrator_verdict TEXT,             -- pass | reopen | escalate | (null for scripted)
    reason             TEXT                 -- short failure/skip reason
);

CREATE INDEX IF NOT EXISTS ix_step_runs_step    ON step_runs(project_id, step_id, started_at);
CREATE INDEX IF NOT EXISTS ix_step_runs_job     ON step_runs(job_key);
CREATE INDEX IF NOT EXISTS ix_step_runs_status  ON step_runs(project_id, step_id, status);
```

Location: `<TaskRepository>/.metadata/pipeline-history.db` (workspace-wide; `project_id` partitions it). One DB, not one-per-project, because the aggregate views are cross-project and the write volume (a handful of rows per task) is trivial. The connection footprint follows `ProjectChatIndex`: open-per-invocation, private cache, pooled.

### 6.6 Example aggregate query (per-step p95 + failure rate)

```sql
SELECT step_id,
       COUNT(*)                                            AS runs,
       AVG(duration_ms)                                    AS avg_ms,
       -- p95 via ordered offset (SQLite has no PERCENTILE)
       (SELECT duration_ms FROM step_runs s2
         WHERE s2.project_id = s1.project_id AND s2.step_id = s1.step_id
         ORDER BY duration_ms
         LIMIT 1 OFFSET (CAST(0.95 * COUNT(*) AS INT)))    AS p95_ms,
       1.0 * SUM(status = 'failed') / COUNT(*)             AS failure_rate
  FROM step_runs s1
 WHERE project_id = $projectId
 GROUP BY step_id
 ORDER BY failure_rate DESC, p95_ms DESC;
```

## 7. Integration with the rest of the system

| Concern | How this ADR integrates |
|---|---|
| Pipeline foundation | Extends [ADR-0045](../architecture-decisions.md#adr-0045) `TaskPipeline` / `PipelineStep` / `PipelineExecutionRecord`. Adds the envelope fields + `StepType` + per-project versioned definitions. |
| Timeline render | Renders on [ADR-0049](../architecture-decisions.md#adr-0049) `timeline.jsonl` (ASS-560). The `*_step_started/finished` kinds already exist. |
| Completion-loop | The loop (ASS-566) **wraps** the pipeline: each task attempt runs the full pipeline; `orchestratorReaction: review` steps that return `reopen` reissue the core work and tick the loop budget. Registered in [loop-inventory.md](../loop-inventory.md). |
| Orchestrator-review safety | `review` steps run through [ADR-0032](../architecture-decisions.md#adr-0032): agent classifies (schema-validated output), rule engine decides halt/reopen/escalate. |
| Per-step model | Reuses the shared CLI+model selector (ASS-544/562). No new picker. |
| Editor mutations | [ADR-0046](../architecture-decisions.md#adr-0046) optimistic-UI; reorder reuses the kanban drag-snapshot revert pattern. |
| Token/cost telemetry | `step_runs` carries per-step tokens + cost + model, folding in ASS-567's Overview ask. |
| Build gate | A `dotnet build` script step, `failureMode: hard`, `reaction: scripted`, in the post-work pipeline. Closes the 0aa9242 incident class. |
| Job-folder writes | Definitions live under `.metadata/`; per-job `step-runs.jsonl` is written through the Task Access layer's append path ([ADR-0024](../architecture-decisions.md#adr-0024)), never a direct folder write. |

## 8. Non-goals

- **EF Core / a DB as source of truth.** Explicitly rejected. The DB is a derived, rebuildable index over JSONL/JSON truth (ADR-0023/0024 hold). If the `.db` is deleted it rebuilds; it never holds the only copy of anything.
- **Intra-project parallelism of pipelines.** One task runs per project at a time (ADR-0001). Steps *within* a pipeline may run in parallel (ADR-0045's bounded fan-out), but two tasks' pipelines never run concurrently in one project.
- **A YAML pipeline file checked into the watched repo.** Definitions live in `.metadata/` as versioned JSON owned by the app, not as a `.ci.yml` in the target project. The product configures pipelines; it does not ask the target repo to.
- **A general workflow/DAG engine.** Steps are an ordered list per phase with intra-phase `dependsOn` edges (ADR-0045). No cross-task fan-in, no multi-branch, no conditional matrices.
- **Arbitrary shell as an LLM-proposed config.** AI-assisted config proposes steps the operator confirms; script commands run with the same trust boundary as today's runner, and a future hardening pass may add a command allow-list (cf. ADR-0032 `selfHealCommands`). Out of scope for the concept.
- **Distributed step execution.** Steps run in-process in one backend, bounded by a semaphore (ADR-0045).
- **Replacing `pipeline-execution.json` or `timeline.jsonl`.** Those stay as the current-run record and the event ledger. `step-runs.jsonl` + the derived DB are additive (the durable per-attempt history + the cross-task analytical projection).

## 9. Slicing plan

Incremental, each slice shippable and verifiable on its own. Earlier slices deliver the build-gate value without waiting for the editor or analytics.

1. **Slice 1 - script steps + per-job history + timeline render.** Add `StepType` + envelope fields to the model. Implement the script-step executor (exit-code driven, captured stdout/stderr artifact). Write `logs/step-runs.jsonl` per attempt. Render planned + running + finished steps on the existing timeline tab. Ship the `dotnet build` hard-gate script step as the first configured post-step (closes the 0aa9242 class). `orchestratorReaction: scripted` only. **No DB yet** (per-job JSONL is enough to render one task's timeline).
2. **Slice 2 - the derived analytics DB + aggregate views.** Add `pipeline-history.db` (the `ProjectChatIndex` pattern: `EnsureFresh`, rebuild-from-JSONL). Mirror `step_runs` + `pipeline_definitions`. Expose `GET /api/projects/{projId}/pipeline/stats` (count, p95, failure rate, trend). Per-step drill-down. CI/CD-style "how is this step behaving" panel.
3. **Slice 3 - LLM steps + live progress.** Implement the LLM-step executor reusing the core-run streaming + token capture. Stream elapsed + tokens onto the timeline while running. The four aspect runs re-expressed as configured LLM steps (already exist as `StepKind.Aspect`; this just makes them editable). `orchestratorReaction: review` wired through ADR-0032.
4. **Slice 4 - the project pipeline editor.** Per-project versioned definitions under `.metadata/pipelines/`. Drag-reorder, add/remove/enable, per-step `type` / `model` / `failureMode` / `reaction`. Optimistic-UI (ADR-0046). Definition versioning + the history view.
5. **Slice 5 - AI-assisted config.** Natural-language -> proposed step list (schema-validated draft, ADR-0032), operator confirms. The `orchestratorReaction: review` decision table hardening + the optional command allow-list.

## 10. Deliverables of this concept task

- This ADR (the model, the two step types, the common envelope, `orchestratorReaction` semantics, the DB choice + DDL, integration, slicing plan).
- UX mockups: [pipeline editor](../mockups/task-processing-pipeline/pipeline-editor.md) and [task timeline](../mockups/task-processing-pipeline/task-timeline.md), indexed at [mockups/task-processing-pipeline/README.md](../mockups/task-processing-pipeline/README.md).
- DB schema proposal: section 6 (SQLite DDL) + the two source-of-truth JSON schemas [`pipeline-definition.schema.json`](../schemas/pipeline-definition.schema.json) and [`step-run.schema.json`](../schemas/step-run.schema.json).
- Slicing plan: section 9.

## 11. Consolidates / supersedes (reference, do not duplicate)

- **ASS-526** (pipeline pre/post steps as first-class): this is its configuration + history layer. Implemented foundation is ADR-0045.
- **ASS-567** (per-step model + token/cost telemetry): folds into `step_runs` + the timeline.
- **ASS-560** (unified timeline): the timeline (ADR-0049) is where this renders.
- **ASS-566** (completion-loop): the loop wraps the pipeline; `review` steps gate completion.
- **ASS-563** (lint:scss post-step): becomes one configured script step (already a `Tool` step in the catalogue).
- The build-gate need (0aa9242 incident): a `dotnet build` script step, `failureMode: hard`, `reaction: scripted`.
- **ADR-0045**: this ADR's parent; its `Status` follow-up list (Settings pipeline panel, Overview pipeline view) is subsumed here.
- **ADR-0050**: the commit-attribution post-step is the worked precedent for a deterministic (`scripted`) post-step.
