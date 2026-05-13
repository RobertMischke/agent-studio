# Architecture Model

Single source of truth for the **authoring** contract of a project's compact high-level architecture map. The map is the input to software-to-architecture drift analysis and the marble-style Drift surface.

> **Language:** English. See [AGENTS.md](../AGENTS.md#documentation-language).
>
> **Schema home:** field-level rules live in [`docs/schemas/architecture-model.schema.json`](schemas/architecture-model.schema.json). The contract here is the prose; the schema is the validator.
>
> **Related:** [ROADMAP.md](../ROADMAP.md#drift-control), [docs/design-principles.md](design-principles.md#drift-is-a-scored-project-dimension), and the embedded `architectureModel` block in [`docs/schemas/drift-report.schema.json`](schemas/drift-report.schema.json).

## 1. Purpose and non-goals

A project may define one architecture model. The model lists at most ten high-level elements: each is a noun a reviewer can point at on a single screen ("Backend API", "Task Access Layer", "Runner / CLI execution"). For each element, the model records what it should do, where its files live, which rules it must follow, what it may depend on, and which tests, schemas, and runtime signals are evidence it still works.

Purpose:

- Give a project one durable description of its high-level shape that the Drift analyzer can compare the source tree, schemas, tests, and runtime evidence against.
- Drive the Drift surface's marble diagram: each element is one marble; per-element drift score, severity, evidence, and follow-up suggestions hang off the same id.
- Stay human-writable. A reviewer must be able to add or update an element in a text editor without a drawing tool.
- Stay reviewable in one glance. The hard limit of ten elements is the readability floor, not a soft cap.

Non-goals (do not add, even if asked offhandedly):

- **Not a workflow engine.** The model describes structure; it never moves jobs, runs CLIs, or edits source.
- **Not a fine-grained component diagram.** Anything that needs more than ten elements belongs in code, ADRs, or per-subsystem documents, not here. If the map starts to feel cramped at ten, the author picks; the schema rejects an eleventh.
- **Not a coordinate system.** The marble surface owns layout. `diagramHint` is a hint, not a coordinate.
- **Not a database.** Source of truth is one Markdown file per model. The Drift analyzer reads the frontmatter on demand.
- **Not a drawing tool replacement.** ASCII art in the body is welcome but optional; the structured block in frontmatter is what the analyzer reads.
- **Not source code's job.** The model lives with project evidence (the watched project's repository or workspace), never in the agent-taskboard source repo.

## 2. File shape

One model = one Markdown file with YAML frontmatter plus a free-form body.

- The **frontmatter** is the structured authoring block. It validates against [`docs/schemas/architecture-model.schema.json`](schemas/architecture-model.schema.json). Drift analyzers read this block.
- The **body** below the frontmatter is human prose. ASCII diagrams, narrative per element, and rationale go here. Analyzers may surface the body verbatim in the marble drill-down, but they do not parse it.

The schema is the validator. The prose below explains the *why* of each field; field-level rules (types, enums, lengths, patterns) are in the schema.

### 2.1 Required top-level fields

- `modelId` - stable kebab-case id. Drift reports reference this via `architectureModel.modelId`.
- `title` - human-readable model title.
- `project` - matches `DriftReport.project`.
- `updatedAt` - ISO 8601 UTC timestamp. The author bumps this when they change anything.
- `elements` - array of one to ten element records.
- `schemaVersion` - integer, currently `1`.

### 2.2 Optional top-level fields

- `owner` - human owner of the model (handle, name, or role).
- `summary` - one or two sentences. Renders as the diagram's subtitle.
- `diagramHint` - free-form layout hint for the marble surface ("two rows", "group runtime elements right"). The renderer may ignore it.

### 2.3 Element fields

Each element record carries:

| Field | Required | Purpose |
|-------|----------|---------|
| `elementId` | yes | Stable kebab-case id, unique within the model. The drift report keys per-element scores by this id. |
| `label` | yes | One-line display label for the marble. |
| `expectedRole` | yes | One or two sentences: what this element is supposed to own or do. The analyzer compares this against current code responsibility. |
| `ownershipBoundary` | yes | Glob patterns or path prefixes that define which files belong to this element. Used to attribute source changes. Example: `backend/Services/Runner/**`. |
| `guidelines` | optional | Free-form rule phrases. Example: "No SQL"; "One writer pattern"; "Validates every append". |
| `allowedDependencies` | optional | Other `elementId` values in this model, or named external systems. The analyzer flags dependencies outside this set as drift. |
| `sourceRefs` | optional | Repository-relative paths or fragment links to authoritative docs and ADRs (for example `docs/architecture-decisions.md#adr-0024`). |
| `relevantTests` | optional | Tests or test directories whose green-state is evidence this element still works. |
| `relevantSchemas` | optional | JSON Schemas defining data contracts owned or consumed here. |
| `runtimeSignals` | optional | Stable event names, log streams, metric names, or REST endpoints the analyzer can use to confirm runtime behavior. Suggested namespacing: `event:`, `log:`, `metric:`, `endpoint:`. |
| `notes` | optional | One-line caveat that should travel with the structured record. Longer narrative belongs in the Markdown body. |

The four "evidence" arrays (`sourceRefs`, `relevantTests`, `relevantSchemas`, `runtimeSignals`) are intentionally separate. They map to four distinct comparisons the analyzer makes:

1. Expected role vs current code responsibility (driven by `sourceRefs` plus `ownershipBoundary`).
2. Documented contracts vs current shapes (driven by `relevantSchemas`).
3. Documented expectations vs test coverage (driven by `relevantTests`).
4. Expected runtime behavior vs runtime evidence (driven by `runtimeSignals`).

A field that conflates them would force the analyzer to guess which comparison an entry belongs to. Keeping them separate keeps the per-element drift report parseable.

## 3. Storage

Architecture models describe the watched project's software, not the agent-taskboard source repo. They live with project evidence.

Two acceptable locations, in order of preference:

1. **In the watched project's repository**, at `architecture/<modelId>.md` (or a project-chosen sibling, for example `docs/architecture/<modelId>.md`). The model is part of the project's documentation and travels with its history. This is the default.
2. **In the workspace**, at `<workspace>/projects/<projectKey>/architecture/<modelId>.md`. Use this when the watched project is read-only, when the model is exploratory, or when the author has not yet committed it back to the project.

In both cases the path is what `DriftReport.architectureModel.sourceRef` points at (repo-relative for case 1, workspace-relative for case 2).

The agent-taskboard source repository (`agent-taskboard-dev/`) is **not** a storage location for project models. Example fixtures and test inputs that exercise the schema may live under `backend.Tests/Fixtures/` or similar, but a real project's model never does.

## 4. Linkage to Drift reports

A drift report references one architecture model by id. The embedded `architectureModel` block in [`drift-report.schema.json`](schemas/drift-report.schema.json) carries:

- `modelId` - matches the source model's `modelId`.
- `title` - matches the source model's `title`.
- `sourceRef` - path to the source `.md` file (per the storage rules in section 3).
- `elements[]` - one entry per element, keyed by `elementId`. Each entry adds the per-run scoring and tracking fields (`score`, `severity`, `sourceCoverage`, `status`, `summary`, `evidenceRefs`, `followUpTaskSuggestions`).

The drift report is a **projection at a point in time**. The authoring fields on each element (`expectedRole`, `guidelines`, `allowedDependencies`, `sourceRefs`) may be denormalized into the report so a reviewer reading the JSON does not need the source file open, but the source file remains authoritative. If the two disagree, the source file wins; the analyzer's next pass refreshes the report.

A project with no architecture model produces drift reports with `architectureModel: null` and no per-element scores. Architecture-dimension findings (inside `dimensions[]` with `type = "Architecture"`) still apply; only the marble surface is empty.

### 4.1 Element-id stability

`elementId` is the join key between the model and every drift report ever written against it. Renaming or removing an element invalidates historical per-element scores. The expected workflow:

- Stable changes (label tweak, expanded guidelines, more `relevantTests`): edit in place, bump `updatedAt`. Historical scores remain valid.
- Splitting one element into two: assign new ids to both halves; treat the old id as retired. Historical scores attach to the retired id and remain visible in trend views as "retired".
- Renaming for clarity: keep the old `elementId`; only change `label`. The id is internal; the label is what the user reads.

## 5. Authoring example

```markdown
---
modelId: agent-taskboard-core
title: Agent Software Studio - Core Architecture
project: agent-taskboard
updatedAt: 2026-05-05T12:00:00Z
owner: rmisc
summary: High-level shape of the local task processor. Ten elements covering backend, frontend, runtime, and observation surfaces.
diagramHint: two rows; runtime elements grouped on the right
elements:
  - elementId: frontend-shell
    label: Frontend App Shell
    expectedRole: Hosts the Angular PWA, routing, theme, and the kanban + project surfaces.
    ownershipBoundary:
      - frontend/src/app/**
    guidelines:
      - Standalone components only; no NgModules.
      - Signals for state.
    allowedDependencies:
      - backend-api
    sourceRefs:
      - frontend/AGENTS.md
      - docs/design-principles.md
    relevantTests:
      - frontend/e2e/**
    runtimeSignals:
      - endpoint:/api/jobs/grouped
  - elementId: backend-api
    label: Backend API
    expectedRole: ASP.NET Core API + SignalR hub. Owns REST endpoints and live push.
    ownershipBoundary:
      - backend/Endpoints/**
      - backend/Program.cs
    guidelines:
      - Web JSON casing.
      - One mutation entry point per concept.
    allowedDependencies:
      - task-access
      - runner
      - quota
    sourceRefs:
      - backend/AGENTS.md
    relevantSchemas:
      - docs/schemas/task-find-result.schema.json
      - docs/schemas/task-mutation-request.schema.json
  - elementId: task-access
    label: Task Access Layer
    expectedRole: Single owner of on-disk job state. Find / list / mutate / transition / subscribe.
    ownershipBoundary:
      - backend/Services/TaskAccess/**
    guidelines:
      - Files on disk are the source of truth.
      - One writer pattern.
      - No SQL, no LiteDB, no EF.
    allowedDependencies:
      - in-memory-store
    sourceRefs:
      - docs/architecture-decisions.md#adr-0024
    relevantTests:
      - backend.Tests/InMemoryStoreTests.cs
    relevantSchemas:
      - docs/schemas/task-find-result.schema.json
  # ... up to ten elements
schemaVersion: 1
---

# Agent Software Studio - Core Architecture

Two rows. Backend, runtime, and observation surfaces on the bottom row;
frontend and operator-facing surfaces on the top row.

```
+-----------------+        +------------------+
| Frontend Shell  |  --->  |   Backend API    |
+-----------------+        +------------------+
                                    |
                                    v
                           +------------------+
                           | Task Access      |
                           +------------------+
```

(Per-element narrative below, optional.)
```

The body of the file is for the human reader. The frontmatter is for the analyzer.

## 6. Validation rules

The schema enforces:

- One to ten elements (`minItems: 1`, `maxItems: 10`).
- `modelId` and `elementId` are kebab-case, max 64 characters.
- `updatedAt` is RFC 3339 / ISO 8601 UTC.
- `schemaVersion: 1` (literal).
- Required per-element fields: `elementId`, `label`, `expectedRole`, `ownershipBoundary` (with at least one entry).

The schema does not enforce, but a parser should warn on:

- Duplicate `elementId` within a model.
- An `allowedDependencies` entry that names neither a local `elementId` nor a recognised external system.
- An `ownershipBoundary` entry that does not match any path in the project.
- A `sourceRefs`, `relevantTests`, or `relevantSchemas` entry that does not resolve to a file.

Warnings are reported as drift findings on the next analyzer run; they do not block authoring.

## 7. What changes when

- New element added: bump `updatedAt`; the next drift run includes it with an "Unknown" or initial score.
- Element retired: leave the id in retired state in the analyzer's history; remove from this file.
- Schema change: bump `schemaVersion` and update [`drift-report.schema.json`](schemas/drift-report.schema.json) in the same commit. Old models keep working until they are migrated; the analyzer logs a `schema-version-mismatch` finding.

## 8. Implementation status and parser ownership

This document and [`docs/schemas/architecture-model.schema.json`](schemas/architecture-model.schema.json) are the contract slice. They are intentionally shipped without a backend parser, validator, or in-code projection. Architecture models describe the *watched project's* software and live with project evidence (section 3); the agent-taskboard backend never reads them at runtime today. The matching record type and round-trip test pattern used for other schemas (see [`docs/schemas/README.md`](schemas/README.md), "Validation") do not apply here because there is no in-process consumer yet.

Parser, validator, and per-element scoring code are owned by the first consumer: the **Software / Architecture Drift** action (queued at `agent-taskboard/2-ready/software-architecture-drift-analysis-action/`). That task's "Read: architecture model document" deliverable covers:

- YAML frontmatter parsing.
- Schema validation against `architecture-model.schema.json`.
- The author-time warnings listed in section 6 (duplicate `elementId`, unresolved `allowedDependencies`, `ownershipBoundary` patterns that match nothing, unresolved `sourceRefs` / `relevantTests` / `relevantSchemas`).
- Per-element scoring that emits the `architectureModel.elements[]` projection inside a [`drift-report`](schemas/drift-report.schema.json) (the C# record `DriftArchitectureElement` already exists in [`backend/Services/Drift/DriftReportContract.cs`](../backend/Services/Drift/DriftReportContract.cs)).

Treat this split the same way schemas were introduced for the supervisor, agent message bus, product runtime observability, and the drift report itself: contract first, consumer-driven parser second. If a different consumer needs to read architecture models before the drift action lands, that consumer owns its own reader and tests.
