# Drift Reports

Single source of truth for the contract that turns project-level drift inspections into first-class, scored, durable reports. Drift is a project dimension beside Architecture, not just an Analysis Reports filter.

> **Language:** English. See [AGENTS.md](../AGENTS.md#documentation-language).
>
> **Schema home:** field-level rules live in [`docs/schemas/drift-report.schema.json`](schemas/drift-report.schema.json). The contract here is the prose; the schema is the validator.
>
> **Related:** the design rationale lives in [ROADMAP.md](../ROADMAP.md#drift-control), [docs/design-principles.md](design-principles.md#drift-is-a-scored-project-dimension), [docs/mockups/quality-system/](mockups/quality-system/README.md), [docs/architecture-model.md](architecture-model.md), and [docs/analysis-reports.md](analysis-reports.md). The neighbouring `AnalysisReport` shape (`docs/schemas/analysis-report.schema.json`) shares the producer model, the Markdown-plus-JSON convention, and the parse-failure semantics.

## 1. Purpose and non-goals

A drift report records one inspection of the gap between what the project says and what the project does. It compares two or more project surfaces (intent, specs, tasks and jobs, ADRs and source code, README and AGENTS, marketing claims, design references, tests, runtime behavior, process rules, schemas, token spend) and emits a triage score with evidence.

Purpose:

- Make Drift a first-class project dimension visible on the project page.
- Give the user a transparent triage signal: one score per dimension, one overall score, one band, every score linked to evidence.
- Reuse the same shape across producers: a manual button click, a scheduled cadence, the orchestrator meta-cycle, a supporting agent, and the Layer 3 external monitor.
- Carry references back to the raw evidence (jobs, runs, commits, screenshots, bus messages, runtime events, previous reports, repository docs) so a reviewer can drill down without the report copying the data.
- Carry an optional architecture-element projection so a marble-style architecture map can render per-element scores against the same evidence.

Non-goals (do not add, even if asked offhandedly):

- **Not a workflow engine.** A drift score does not move jobs between lanes, edit `job.json`, or fan out coding work. The runner remains the single state-machine authority.
- **Not a hidden decision engine.** The score is a triage signal. The user sees the inputs and the weights; the analyzer never hides the math behind the band.
- **Not a database.** Source of truth is many small Markdown + JSON document pairs on disk, read through the file-backed `InMemoryStore<T>` pattern. No SQL, SQLite, LiteDB, or EF.
- **Not a parallel orchestrator.** A `followUpTaskSuggestion` does not silently create a queued job. Follow-up creation is a deliberate, visible action that goes through the existing task creation entry point (the Task Access Layer once it lands; until then, the existing job-creation path).
- **Not a hidden steering or code mutator.** A drift report may suggest a README, AGENTS, ADR, or skill update. It must not silently rewrite those files. Source-code edits go through normal queued tasks.
- **Not a duplicate event store.** The Agent Message Bus remains the event spine. Reports reference bus messages by id; they do not copy raw transcripts or whole event streams.

## 2. Dimension vocabulary

Twelve dimensions are defined. Each dimension answers one question and points at a fixed set of source surfaces. Adding a dimension requires a schema bump and a contract update; producers must not invent new dimension names.

| Dimension | Question | Source surfaces |
|-----------|----------|-----------------|
| `Intent` | Does the product still do what its stated goals say? | `README.md`, `ROADMAP.md`, product thesis sections, project-level intent notes. |
| `Spec` | Do written specifications match the implementation? | `docs/`, mockup specs, schema docs, contract documents. |
| `TaskJob` | Do queued and closed tasks match the evidence in their job folders? | `1-preparation`/`2-ready`/`3-progress`/`4-auto-review`/`5-human-review`/`6-completed` lanes, `prompt.md`, `status.md`, `logs/cli-output.log`, run timeline. |
| `Architecture` | Does the source tree match the ADRs and the architecture model? | `docs/architecture-decisions.md`, `docs/architecture-model.md`, the architecture-model frontmatter, source tree, dependency graph. |
| `Documentation` | Do README, AGENTS, and per-area docs match the current code? | `README.md`, `AGENTS.md`, `frontend/AGENTS.md`, `.github/copilot-instructions.md`, `docs/`. |
| `Marketing` | Do external claims (website, README marketing copy, demo scripts) match shipped behavior? | Marketing markdown, demo scripts, external README sections. |
| `Design` | Do design references match the screenshots the agents are producing? | `docs/mockups/`, `docs/research/`, accepted/rejected design references, Playwright screenshots in `<job>/results/`. |
| `Test` | Do tests cover the areas the docs call risky? | `backend.Tests/`, `frontend/e2e/`, coverage reports, source-code maps, ADR risk callouts. |
| `Runtime` | Does runtime behavior match the expected domain behavior and performance signals? | Structured runtime events (when product-runtime-observability lands), backend logs, supervisor advisories, performance probes. |
| `Process` | Does how work actually flows match the documented process? | `docs/agent-task-contract.md`, `docs/skills-architecture.md`, `docs/commit-push-doctrine.md`, `AGENTS.md` workflow sections, recurring blocked reasons. |
| `Schema` | Do published JSON schemas match the C# / TypeScript shapes that flow through the layers? | `docs/schemas/`, backend records, frontend models, `SchemaRoundTripTests.cs`. |
| `Token` | Does declared token budget match observed spend? | `token-aggregate.schema.json` records, project token summaries, expensive-job lists. |

The vocabulary is shared with the architecture-model file's element-level fields; the relationship is many-to-many. One drift report can carry both a `dimensions[]` array (per-dimension scores) and an `architectureModel.elements[]` array (per-element scores under the Architecture dimension lens).

## 3. Status states for findings and dimensions

Five states cover the lifecycle of one finding or one dimension. The states are user-visible; the UI must let a reviewer see whether a drift item is new, accepted, ignored, already tracked, or resolved.

| State | Meaning | Effect on score |
|-------|---------|-----------------|
| `New` | Surfaced for the first time, no reviewer action yet. | Default weight applies. |
| `Accepted` | Reviewer acknowledges the drift but is not acting on it now (recorded risk). | Mild upweight: the finding is known and accepted, not an unresolved gap. |
| `Ignored` | Reviewer dismissed the finding. The analyzer must not re-promote it without a fresh evidence change. | Downweighted to near zero impact. The reviewer's call stands. |
| `Tracked` | A follow-up task exists. `trackedTaskId` points at the queued job. | Mild upweight relative to `New`: the gap is acknowledged and on the queue. |
| `Resolved` | Confirmed fixed. Next analysis should drop the finding or downgrade severity once evidence catches up. | Removes the finding from the active count. |

Status applies at two levels:

- **Per-dimension status** (`dimensions[].status`) summarises the dimension as a whole. A dimension whose findings are mostly tracked but have one new High should be `New` or `Tracked` depending on how the producer wants the UI to badge it; the per-finding status is the authoritative source.
- **Per-finding status** (`dimensions[].findings[].status`) is the granular truth. The UI may show one chip per finding and roll up to the dimension-level chip.

Status is metadata, not a tombstone: marking a finding `Resolved` does not delete it from the report. The next drift analysis decides whether the underlying drift still exists.

## 4. Score bands

Five bands map the numeric `overallScore` (0..100) to a UI badge. Bands are also influenced by the worst dimension severity; a Critical-severity dimension cannot live inside a `Healthy` overall band.

| Band | Numeric range | Severity guard | UI treatment |
|------|---------------|-----------------|--------------|
| `Healthy` | 85..100 | No `High` or `Critical` dimension severity. | Green. The project is broadly aligned with what it says. |
| `Watch` | 70..84 | At most one `Warn` dimension severity. | Yellow. Worth a periodic look. |
| `Warn` | 50..69 | Up to one `High` severity dimension. | Orange. Active follow-up is sensible. |
| `Critical` | 0..49 | Any `Critical` severity, or `High` severity in two or more dimensions. | Red. Drift has crossed a threshold the user should not ignore. |
| `Unknown` | n/a | Reserved. | Grey. Used when overall sourceCoverage is below the analyzer's reporting threshold (see Section 6) and the score is unreliable. The user should run a deeper analysis or relax the scope. |

The band is computed by the producer at write time and stored on the record. Consumers must not recompute it; the user-visible band is what the producer signed off on.

## 5. Scoring inputs and weights

The score is reproducible: every input lands on the record so a reviewer can rebuild the math from the evidence. The weighting is fixed defaults today; project-level overrides may ship later (see open questions in the quality-system mockup).

### 5.1 Per-dimension score

Each dimension carries a `scoreInputs` block with the following inputs. Defaults are tuned so a clean dimension scores 100 and a dimension with multiple stale, untracked, recurring `High` findings scores below 50.

| Input | Field | Default weight | Direction |
|-------|-------|----------------|-----------|
| Findings severity | `findingsBySeverity.{info,warn,high,critical}` | Info -1, Warn -5, High -15, Critical -35 per finding | Lower score |
| Confidence | `confidence` (0..1 on dimension) | Score multiplier: `score = base * (0.5 + 0.5 * confidence)` | Low confidence dampens the dimension |
| Source coverage | `sourceCoverage` (0..1 on dimension) | Hard floor: `sourceCoverage < 0.3` forces `Unknown` for the dimension regardless of base | Low coverage downgrades to Unknown |
| Affected surfaces | `affectedSurfaces[]` length | -2 per distinct surface beyond 1 (capped at -10) | Lower score the wider the blast radius |
| Recurrence | `recurrenceCount` (prior reports flagging this dimension) | -2 per prior occurrence (capped at -10) | Recurring drift is a stronger signal than a one-off |
| Finding age | `oldestFindingAgeDays` | -1 per 7 days beyond the first 7 (capped at -10) | Older drift is worse than fresh drift |
| Tracked share | `trackedFindings / totalFindings` | +5 per 0.5 of tracked share (max +10) | Tracked drift recovers some score; the gap is acknowledged |

The base score starts at 100. Each input is applied additively, then the confidence multiplier is applied, then the result is clamped to `[0, 100]`. The producer writes the final integer; the inputs let any consumer rebuild it.

Pseudocode:

```text
base = 100
base += sum(severity_weights)               // findings severity
base -= 2 * max(0, len(affectedSurfaces) - 1)
base = max(base, 100 - 10)                  // affectedSurfaces cap
base -= min(10, 2 * recurrenceCount)
base -= min(10, max(0, (oldestFindingAgeDays - 7) / 7))
base += min(10, 10 * trackedFindings / max(1, totalFindings))
score = clamp(0, 100, round(base * (0.5 + 0.5 * confidence)))

if sourceCoverage < 0.3:
    severity = "Info"
    band = "Unknown"
```

Status states modulate the contribution of individual findings before they enter the severity weighting:

- `Ignored` findings are excluded from the severity sum.
- `Accepted` findings count at half their severity weight (e.g. an Accepted High contributes -7.5 instead of -15).
- `Resolved` findings are excluded.
- `New` and `Tracked` count at full severity weight; `trackedFindings` adds the bonus described above.

### 5.2 Overall score

The overall score is the average of per-dimension scores, weighted by their `confidence` so a dimension the analyzer is unsure about does not dominate. Dimensions in `Unknown` band (sourceCoverage too low) are excluded from the average, but their existence is noted in the Markdown body so a reviewer is not lulled by an artificially high overall score.

```text
contributing = [d for d in dimensions if d.band != "Unknown"]
if not contributing:
    overallScore = 0
    scoreBand = "Unknown"
else:
    weights = [d.confidence for d in contributing]
    overallScore = round(sum(d.score * w for d, w in zip(contributing, weights)) / sum(weights))
    scoreBand = bandFor(overallScore, worstSeverity(contributing))
```

The producer is responsible for both numbers. Consumers must trust the record; they may render the inputs alongside but they do not recompute the band.

### 5.3 Architecture-element scores

When the report carries an `architectureModel` block, each element scores like a dimension: same severity ladder, same status states, same coverage floor. The architecture-element scores do not feed directly into the overall score; they roll up into the `Architecture` dimension via the producer's own logic. This split keeps the marble surface independent of the dimension grid: a project with a healthy architecture but stale documentation should show a green marble map and an orange `Documentation` dimension at the same time.

## 6. Document shape

One report = one Markdown file + one optional JSON sidecar with the same stem.

```
<workspace>/logs/drift/<project>/<reportId>.md          # human-readable artifact
<workspace>/logs/drift/<project>/<reportId>.json        # structured sidecar (optional)
```

`reportId` is a ULID or UUID v7 so lexical sort matches creation order. The sidecar's filename is the Markdown filename with `.json` in place of `.md`; consumers find the sidecar by direct lookup, not by parsing the Markdown.

### 6.1 Markdown is the human artifact

- The Markdown is what a reviewer reads in the project page's drill-down, in the activity log, in the companion app, and on disk months later.
- Lead with the score, the band, and the one-sentence verdict, then walk the dimensions, then the suggested follow-up tasks.
- Reference jobs, runs, commits, screenshots, bus messages, runtime events, ADRs, and previous reports by their stable ids; do not copy raw logs.
- Markdown remains valid evidence even when the JSON sidecar is missing or malformed. A reader who cannot parse the sidecar must still be able to read the report.

### 6.2 JSON is the app contract

- The schema is [`docs/schemas/drift-report.schema.json`](schemas/drift-report.schema.json).
- Required fields lock the surface the UI, the bus, and the system-review monitor read against: `schemaVersion`, `reportId`, `project`, `createdAt`, `producer`, `trigger`, `scope`, `overallScore`, `scoreBand`, `summary`, `parseStatus`, `dimensions`, `followUpTaskSuggestions`.
- Field names are camelCase to match `JsonSerializerDefaults.Web` and the existing schema policy.
- Enums spell PascalCase to match the C# records (consistent with [`docs/schemas/README.md`](schemas/README.md)).

### 6.3 Parse-failure behavior

Same three states as the analysis-report contract:

| `parseStatus` | Meaning | UI behavior |
|---------------|---------|-------------|
| `Structured` | Both files exist; the JSON validates against the schema. | Show overall score, band, dimension grid, marble map, follow-ups, drill-down chips. |
| `Unstructured` | Markdown exists; the JSON sidecar is missing. | Show the Markdown verbatim, label the report **Unstructured**, do not promise structured filters. |
| `MalformedJson` | Markdown exists; the JSON sidecar exists but failed to parse or validate. | Same as `Unstructured`, plus surface the parser error. The Markdown stays visible. |

A failed JSON parse never hides the Markdown. A reviewer can always read the human artifact, attach a manual follow-up, and move on. This rule is the load-bearing one - it is what makes Markdown the durable contract and JSON the additive convenience.

## 7. Storage and retention

### 7.1 Locations

- Per-project drift reports: `<workspace>/logs/drift/<project>/<reportId>.md` (and `.json` sidecar).
- Workspace-scoped drift reports: `<workspace>/logs/drift/_workspace/<reportId>.md` (and `.json` sidecar).
- The workspace's `<workspace>/logs/drift/` directory is owned by the drift-report layer. External writers that bypass the layer are not visible until the projection is invalidated for that (workspace, project) pair.

`logs/drift/` is a sibling of `logs/analysis/` (analysis reports), `logs/meta/` (supervisor), and `logs/bus/` (Agent Message Bus). Source code lives in the app repository; drift evidence lives next to the project. The directory is owned by the watched project's evidence, backed by the project organization / Task Access layer when that lands; until then the existing `InMemoryStore<T>` pattern is the source of truth.

### 7.2 In-memory projection

The backend reads the directory through the same file-backed `InMemoryStore<DriftReport>` pattern as `AnalysisReportStore` and the agent-message-bus store (ADR-0023). One projection per (workspace, project) pair; the workspace-scoped variant uses the synthetic project key `_workspace`. Disk is the source of truth; the projection is a view that can always be rebuilt by re-reading the files.

The projection serves:

- `Snapshot(workspace, project)` - all drift reports for the project, newest last.
- `GetById(workspace, project, reportId)` - one report by id.
- `Where(workspace, project, predicate)` - filter by trigger, scope, score band, severity, time window, parse status.
- `ReadSince(workspace, project, cursor)` - cursor-based tail for streaming consumers (UI auto-refresh, future Layer 3 consumer).

Reports are immutable once written. Mistakes are corrected by a follow-up report, not by editing the original. Status transitions on findings (e.g. `New` -> `Tracked`) happen by emitting a new drift report that supersedes the prior one, not by editing the existing record. This matches the analysis-report and bus contracts.

### 7.3 Retention

- Drift reports are not auto-deleted by the backend. They are evidence; they outlive the run that produced them.
- A retention policy ("keep 90 days", "keep last 200 per project") may ship later as a project setting; until then, reports persist indefinitely.
- A `Resolved` finding does not delete the report or the finding. Status is metadata, not a tombstone.

### 7.4 Migration note

The Task Access Layer (ADR-0024) is in phase 1 (contract only) at the time of writing. The first cut of the drift-report store reads job folders only via stable refs (path strings or `(project, jobId)` tuples) and does not call `JobScannerService.FindJob` or write to `job.json`. When the Task Access Layer ships its mutation phase, follow-up task creation moves to `ITaskAccess.Create` and the existing job-creation path is removed from this layer in the same commit.

## 8. Comparison to neighbouring records

| Record | Cadence | Producer | Owns | Schema |
|--------|---------|----------|------|--------|
| `AnalysisReport` | Per inspection | Manual / scheduled / meta-cycle / supporting-agent / external-monitor | Generic inspection narrative | `analysis-report.schema.json` |
| `DriftReport` | Per drift analysis | Same producer set as `AnalysisReport` | Project-level drift score with typed dimensions and architecture-element projection | `drift-report.schema.json` |
| `MetaCycleReport` | Per N completed jobs | Meta-cycle | The cycle's operational decision | `meta-cycle-report.schema.json` |
| `SupervisorAdvisory` | Per-tick, mid-run | Supervisor | Health observations | `supervisor-advisory.schema.json` |
| `AgentMessage` | Per event, continuous | All participants | The event spine | `agent-message.schema.json` |

`DriftReport` is a specialised analysis report shape. It is kept separate from `AnalysisReport` today because the drift-score surface needs typed dimension and architecture-element fields the generic shape does not expose. A future cleanup may fold drift under `AnalysisReport` with a typed `topic = "drift"` if the dimension fields can move into a structured payload without losing query-ability.

## 9. Implementation pointers

- Schema: [`docs/schemas/drift-report.schema.json`](schemas/drift-report.schema.json).
- Schema index: [`docs/schemas/README.md`](schemas/README.md).
- Backend records: `OrchestratorApi.Services.Drift.DriftReport`, `DriftDimension`, `DriftFinding`, `DriftScoreInputs`, `DriftArchitectureModel`, `DriftArchitectureElement`, `DriftFollowUpTaskSuggestion`.
- Backend validator: `OrchestratorApi.Services.Drift.DriftReportValidator`.
- Disk paths: `OrchestratorApi.Services.Drift.DriftReportPaths`.
- Tests: `backend.Tests/SchemaRoundTripTests.cs` (`DriftReport_*`), `backend.Tests/DriftReportValidatorTests.cs`.

## 10. Open questions

These are deliberately not part of the v1 contract. They live here so a future implementation cycle can pick them up without re-litigating the v1 shape.

1. Which weights become project-configurable, and where the override lives (project settings file vs. analyzer prompt).
2. Whether the analyzer should emit `Unknown` per-dimension on the report or leave the band derivation to consumers (current contract: producer signs off).
3. Whether `Marketing` drift moves to its own dedicated public-positioning report under Analysis Reports once that surface ships, or stays inside Drift.
4. Whether the architecture-element scores should also feed a separate compact "marble health" record for the Project Screen, or stay embedded in the drift report.
5. Whether a finding's `firstSeenAt` should be tracked in the analyzer state (a separate record) or recomputed from the prior reports on every run. Today it is producer-supplied; the analyzer is responsible for the lookback.
