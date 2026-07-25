---
lifecycleSchema: wiki-page-lifecycle/v1
pageKind: design
lifecycleState: in-progress
editedBy: "Codex / AGT-2137"
editedAt: 2026-07-21T05:46:33Z
lifecycleHistory:
  - state: in-progress
    editedBy: "Codex / AGT-2137"
    editedAt: 2026-07-21T05:46:33Z
    note: "Initial classification: Project Graph and mapped guides exist; the broader layer remains staged."
---

# Architecture and quality layer in Project Hub

Status: operator-vision concept, 2026-07-13. This document is the umbrella
contract for the read-only Project Graph slice (AGT-2127) and the mapped
Style-Guide slice (AGT-2128). The accompanying
[interactive Workbench](../quality/architecture-quality-layer/index.html)
shows the four Project Hub views with illustrative contract data.

The Workbench is the umbrella vision, not a claim that all four views land in
the first two slices. AGT-2127 adds the Project Graph surface; AGT-2128 adds the
applicable-guide surface through the existing Project Wiki/Hub navigation.
Run inventory, grading, and their eventual shared shell remain later slices.

## 1. Decision

Project Hub gets one **Architecture and Quality** area with four views:

1. **Project Map:** generated facts about components, technologies, coarse
   internal dependencies, and size.
2. **Guides:** repository-owned rules selected for a project, technology, and
   prompt task area.
3. **Analysis Runs:** a dated inventory projected from the existing Analysis
   Report contract and other explicitly registered run families.
4. **Component Grades:** evidence-backed, rubric-versioned assessments whose
   units are the components from Project Map.

The same current Project Map and applicable-guide selection are exposed in a
bounded, prompt-readable form. Project Hub is therefore both the human overview
and the first place a coding agent looks when it asks: **Where am I, what is
connected to what, and which rules apply here?**

This is a read-and-explain layer. It does not become a code graph, a database,
a workflow engine, or an automatic approval gate.

## 2. Why one layer, but not one source of truth

The four views answer related questions, but their evidence has different
owners. Combining the UI must not collapse those owners into one mutable model.

| Question | Durable truth | Projection in Project Hub |
|---|---|---|
| What exists now? | Repository manifests, solution/project files, workflow files, and source paths at a Git revision. | Generated Project Map snapshot and Markdown map. |
| What should the high-level architecture be? | The authored, at-most-ten-element [Architecture Model](../system/architecture/model.md), ADRs, and domain documents. | Intent links and future drift comparison beside discovered components. |
| Which practices apply? | Versioned guide pages with structured applicability metadata. | Applicable guide list and bounded prompt manifest. |
| Which inspections ran? | Existing [Analysis Reports](../system/reports/analysis-reports.md), QA/performance artifacts, and registered run descriptors. | Dated run inventory with status, revision, producer, and artifact links. |
| How healthy is a component? | A dated grading report with rubric version and evidence references. | Current grade, confidence/coverage, dimension detail, and trend. |

The Project Map describes **observed structure**. The Architecture Model
describes **authored intent**. Neither replaces the other. A later architecture
drift run may compare them, but discovery must never silently rewrite intent.

## 3. Layer 1: Project Map

### 3.1 Discovery boundary

Discovery walks only managed repository roots and recognizes bounded,
reviewable inputs:

- `.sln`, `.slnx`, and `.csproj`, including `ProjectReference` edges;
- `package.json`, declared workspaces, and `angular.json` projects;
- repository-local package dependencies where the target resolves to another
  discovered component;
- workflow definitions as a bounded, source-linked inventory. The v1 slice
  does not invent operational targets; a later parser may promote a definition
  to an operational component and emit `workflow-target` edges only when those
  targets are explicit;
- coarse file and line counts using documented include/exclude rules.

It may label technologies such as .NET, Angular, TypeScript, Node, or GitHub
Actions from those manifests. It must not infer class calls, import every
third-party package as a node, parse secrets, execute project scripts, or scan
outside the managed root.

Every edge carries a reason (`project-reference`, `workspace-package`, or
`workflow-target`) and a source file. Unknown or unresolved references remain
explicit findings; they are not guessed into place.

### 3.2 Current projection and history

One discovery run produces an immutable snapshot with at least:

```text
schemaVersion, snapshotId, generatedAt, generatorVersion
projects[]: stable identity, repository identity, source revision, dirty/partial state
components[]: stable id, label, root path, kind, technologies, file/LoC metrics
relationships[]: from, to, kind, sourceRef
warnings[]: code, component/path, message
```

Stable component ids derive from project identity plus normalized manifest path,
not display names. A rename is therefore visible and reviewable instead of
being silently joined by fuzzy matching.

Project and technology identity are shared contracts rather than UI labels:

- `projectKey` is the immutable registry id (for example `PROJ-001`); mutable
  short codes such as `AGT` are explicit aliases and the display name is
  carried separately;
- `technologyKey` is a canonical lowercase slug such as `dotnet`, `csharp`,
  `angular`, or `typescript`; detected versions and labels such as `.NET 10`
  or `Angular 21` are separate presentation/evidence fields;
- repository roots are server-side containment inputs and are never part of a
  client DTO, generated document, URL, or prompt.

The Project Map, guide matcher, Hub, and prompt manifest use these same keys.
Aliases are explicit data; consumers never normalize display labels
independently.

One workspace snapshot may represent several repositories, so provenance is
per project. A focus-project SHA must never be displayed as if it covered every
node in the graph.

The current human/agent projection is
`docs/system/architecture/project-map.md`. The generator owns a clearly delimited
generated body and documents its regeneration command. Dated snapshots are
append-only evidence; the current page links to the snapshot and source
revision it represents. Opening Project Hub never triggers discovery.

The generated Markdown starts with a compact orientation block: components,
technologies, direct internal relationships, warnings, and regeneration
command. This lets an agent use it without loading the graphical UI.

### 3.3 Relationship to ADRs and the Architecture Model

Project Map nodes may link to matching ADRs, domain pages, and authored
Architecture Model elements, but these are annotations, not discovery facts.
The UI labels them **Intent**. A missing annotation is not a failed dependency.

A future drift run can compare:

- discovered components vs. authored ownership boundaries;
- observed internal relationships vs. `allowedDependencies`;
- component paths vs. relevant tests, schemas, and runtime signals;
- snapshot-to-snapshot structural change.

That comparison emits a normal drift/analysis report. It does not mutate the
map or model.

## 4. Layer 2: quality

### 4.1 Guide family and applicability

Guides are Markdown pages in one navigable `docs/quality/` family. Design hard
rules remain authoritative at
[`docs/quality/design/style-guide-hard-rules.md`](../quality/design/style-guide-hard-rules.md)
and are included from the family index instead of copied. A guide has
structured frontmatter equivalent to:

```yaml
styleGuideId: angular-components
title: Angular component guide
version: 1
summary: Rendering, identity, and token rules for Angular UI work.
promptSummary: Use standalone OnPush components, stable row identity, semantic tokens, both themes, no decorative left accents, and tabular numeric metrics.
appliesTo: {"projects":["*"],"technologies":["angular","typescript","scss"],"taskAreas":["frontend"]}
```

All six top-level fields shown above are required. A file with missing or
invalid metadata remains linkable as documentation but is excluded from
automatic matching with a deterministic warning.

The lean v1 matches `projects` and discovered `technologies` for the Project Hub
catalogue, then uses `taskAreas` when the existing intake selector prepares
prompt context. Path-level and task-kind selectors are valid future refinements,
but are not hidden prerequisites for AGT-2128. Matching is deterministic:

1. resolve the stable registry `projectKey`, accepting a current short code
   only as an explicit alias and never using the display name as identity;
2. read canonical `technologyKey` values from the current Project Map (or an
   explicit, provenance-labelled project setting when no snapshot exists);
3. match project and technology, then the existing intake task area for prompt
   use;
4. sort by stable `styleGuideId`;
5. include a bounded digest and source references in the prompt manifest.

An empty selector never means “all” accidentally. Wildcards are explicit.
Invalid metadata keeps the page readable in Wiki but excludes it from automatic
prompt injection and produces a visible validation warning.

### 4.2 Best-practice and prompt library

The best-practice library is not another storage tree. It is a guide category
within the same family. A page can be:

- a hard rule (machine-checkable or review-blocking when an existing gate says
  so);
- a recommended pattern (prompt-known, advisory);
- a reusable recipe/example (loaded only when relevant);
- a checklist for a named analysis or review.

The prompt manifest states which guides were selected, why each matched, its
revision, and what was omitted because of the size budget. Agents receive the
small digest plus links, never an unbounded concatenation of the Wiki.
The budget is global for the complete guide block, not a per-guide allowance;
ordering, truncation, and omissions are deterministic and observable.

### 4.3 Analysis Run inventory

The inventory is a projection, not a new report store. Its primary inputs are
the existing Analysis Report Markdown/JSON pairs under `logs/analysis/` and
registered QA, performance, discovery, visual-survey, and grading run families.

Each inventory row needs:

```text
run id, family/topic, project/scope, started/finished timestamps
trigger and producer, status, source revision, summary
artifact/evidence refs, previous comparable run id, optional task refs
```

The inventory groups comparable runs by family and project, making history and
trend visible. A malformed sidecar never hides the human artifact: the row is
shown as invalid with the parse error, matching the Analysis Report contract.
Opening the inventory does not run an analysis, spend model quota, or create a
task. Run and follow-up actions stay explicit.

Per-task pipeline documents such as `aspect-code-quality.md` remain review
evidence owned by that task/run and rendered through the existing
[Result document contract](result-view-and-case-templates.md#5-one-data-source-two-renderings-shipped).
The run inventory may link to them. It must not silently promote an aspect
verdict into a project-wide run or component grade. A grading producer may use
aspect evidence only after it records component attribution, rubric version,
coverage, and the exact source runs it aggregated.

### 4.4 Component grading

The grading unit is a stable Project Map component id. A grading run records:

- rubric id and version;
- source revision and Project Map snapshot id;
- per-dimension grade (`mechanics`, `tests`, `documentation`, `runtime`,
  `design/accessibility` where applicable);
- evidence coverage and confidence separately from the grade;
- findings and stable evidence references;
- prior comparable grade and an explanation for material movement.

The compact UI may show `A` through `E`, but `Unknown` is a first-class value.
An overall grade cannot be rendered when required dimensions lack evidence.
Grades expire or become **stale** when the component revision moves beyond the
graded source revision; they do not pretend to be live health.

The model follows the evidence-first stance of the former quality-system
taxonomy (now consolidated into this page): a grade guides attention. It does not automatically block integration or move a task. A hard
rule can still fail an existing lint/review gate, but the UI must distinguish
that gate result from a component grade.

### 4.5 Gate and evidence taxonomy

Project Hub preserves the causal vocabulary already exposed by the
[`Run -> Gate -> Review aspects -> Lane decision` verdict chain](../system/contracts/run-outcome.md):

| Signal | Scope | Can block or move work? | Role in this layer |
|---|---|---|---|
| Build/test/completion gate | One task/run | Yes, under its existing policy. | Linked evidence; never recomputed by Project Hub. |
| Review aspect, including code quality | One task/run | Advisory or policy input according to the pipeline. | Linked from runs/grades with source attribution. |
| Lintable hard rule | A source change | Yes when already wired to a lint/review gate. | Guide classification plus latest evidence. |
| Analysis finding | Project/task/time window | No automatic mutation in v1. | Run-inventory detail and possible explicit follow-up. |
| Component grade | One component at one revision | No. | Triage/trend signal with coverage and staleness. |
| Lane decision | One task | Yes; owned by task workflow. | Reference only, never a quality score. |

This prevents a green build from being displayed as an `A`, a model-backed code
quality aspect from being mislabeled as a gate, or an advisory grade from
quietly becoming workflow authority.

## 5. Project Hub and agent experience

### 5.1 Human surface

Architecture and Quality is a Project Hub area, not a new top-level product
destination. It exposes:

- **Map:** graph/list switch, technologies, size, relationship reasons,
  warnings, revision, and links to intent documents;
- **Guides:** applicable/all toggle, match reasons, validation state, and the
  source page;
- **Runs:** family/status filters, dated comparable history, artifacts, and an
  explicit run action where supported;
- **Grades:** component grid/list, rubric/source freshness, evidence detail,
  trend, and related findings.

The views share component ids, project scope, branch/revision provenance, and
the existing Project Hub/Wiki navigation. They do not share a synthetic global
“quality score”. Concrete labels remain visible, consistent with the current
quality taxonomy.

### 5.2 Agent landing contract

Agents get a deterministic orientation pack:

1. stable project identity and represented revision;
2. the compact current `project-map.md` orientation block;
3. applicable guide digests with match reasons and revisions;
4. relevant ADR/domain links;
5. freshness warnings when the map or guides cannot represent the working
   revision.

The pack is bounded and inspectable in the task prompt. It never includes local
absolute paths, credentials, full analysis histories, raw runtime logs, or
third-party dependency inventories. A task with no current map still runs with
an explicit “Project Map unavailable/stale” notice; missing orientation is not
silently treated as an empty architecture.

## 6. Contracts for the two first slices

### AGT-2127: Project Graph v1

The read-only first slice is accepted when:

- discovery is deterministic and tested against `.sln`/`.slnx`/`.csproj`,
  Node/Angular workspaces, local package edges, and workflows;
- every component and relationship points to its source manifest;
- third-party packages and class/import call graphs are excluded;
- file/LoC rules, ignored directories, command, generator version, revision,
  and dirty-state semantics are documented;
- current Markdown and dated snapshots come from the same typed result;
- capture writes the current projection atomically, assigns a stable
  `snapshotId`, links current to its dated snapshot, and is an explicit action
  rather than a Project-Hub GET side effect;
- canonical project/technology keys, per-project revision/dirty state,
  unresolved-reference warnings, and relationship source evidence survive in
  both graph and list projections;
- the Project Hub graph has an equivalent list/table, both themes, keyboard
  access, bounded rendering, and honest empty/partial/error states;
- the current managed-project sweep includes explicit success/warning/error
  rows for AGT, CAR, CAC, TE, and the registered website projects rather than
  silently omitting an unreachable repository.

The slice does not implement guide matching, grading, architecture drift, or a
general code graph.

### AGT-2128: Style-Guide layer v1

The first guide slice is accepted when:

- `docs/quality/` has one index and structured Angular/.NET pages while the
  existing design hard rules remain one linked authority;
- metadata validation and deterministic applicability matching have unit
  tests, including explicit wildcard, technology, task-area, invalid metadata,
  empty-selector, and no-match cases;
- Project Hub/Wiki shows applicable guides plus match reasons and source links;
- client DTOs expose stable project/technology keys but no repository root;
- discovery rejects symbolic-link/reparse escapes and enforces file-count and
  file-size limits before reading guide or manifest content;
- the existing prompt-known path consumes the same matcher/projection and
  exposes selected guide ids/revisions without duplicating prompt assembly;
- prompt size has one hard aggregate budget with deterministic omission
  metadata, and invalid or oversized guides are excluded visibly;
- Angular/.NET starter content is distilled from existing repository practice,
  clearly separating hard rules, recommended patterns, and examples;
- both themes, keyboard access, and real screenshots cover the UI slice.

The slice does not create a generic policy engine, rewrite guides through the
app, grade components, or automatically change lint configuration.

## 7. Safety, freshness, and performance invariants

- Repository files are the source; in-memory indexes are disposable.
- All reads are contained below registered roots and reject symbolic-link or
  reparse-point escapes.
- Discovery parses data; it does not execute package scripts, MSBuild targets,
  workflow actions, or arbitrary plugins.
- Absolute repository/watch paths are never written into generated docs,
  prompts, URLs, or run artifacts.
- Every generated or graded view carries source revision, generated time,
  schema/rubric version, and dirty/partial state.
- Current projections are replaced atomically; history is append-only.
- Project Hub reads cached/current artifacts. Regeneration and analyses are
  explicit commands/actions, never page-load side effects.
- Lists and graphs have component/edge caps and an equivalent complete list;
  oversized projects degrade to grouped/filtered views instead of freezing the
  browser.
- Both themes, narrow layouts, reduced motion, native keyboard flow, token
  colors, and the no-left-accent rule apply to every product slice.

## 8. Independent review incorporated

The first-slice contracts were checked a second time against the concrete
AGT-2127 and AGT-2128 implementations before integration. The review tightened
five boundaries that are easy to miss when the slices are built separately:

- Hub reads cannot trigger repository discovery; capture and atomic projection
  replacement are explicit;
- project identity, technology identity, and their display labels have one
  shared cross-slice contract;
- absolute paths, link escapes, and oversized manifest/guide inputs are
  rejected before they can enter DTOs, generated evidence, or prompts;
- prompt selection has one aggregate budget and a deterministic omissions
  trace;
- the illustrative Workbench shows discovered facts separately from authored
  intent and demonstrates only the lean v1 selectors (`projects`,
  `technologies`, and `taskAreas`).

## 9. Rejected alternatives

| Alternative | Why it is rejected |
|---|---|
| One mutable “architecture graph” edited in Project Hub | Conflates discovered fact with authored intent and creates a second source beside Git. |
| A language-server/code-call graph | Wrong level for orientation, expensive across languages, noisy, and outside the operator’s component/project question. |
| One giant Quality page/score | Hides which evidence is stale and collapses gates, grades, reports, spend, and advice into false precision. |
| Copy hard rules into every technology guide | Duplicates authority and guarantees drift. |
| A new database for run history | Existing report/artifact files already provide durable history; a projection is sufficient. |
| Inject the whole Wiki into every prompt | Unbounded, expensive, and less trustworthy than deterministic applicability plus explicit omissions. |
| Auto-run discovery/grading on Hub open | Surprising cost and latency; a read surface must remain read-only. |

## 10. Delivery slices and honest size

The complete layer is an epic-sized programme. The first two slices are useful
but do not imply grading or full analysis orchestration already exists.

| Slice | Size | Scope | Depends on |
|---|---:|---|---|
| **AQ-1 Project Map discovery + read-only Hub view** (AGT-2127) | L | Typed discovery, current Markdown, dated history, graph/list, first managed-project sweep. | Existing project registry/Hub. |
| **AQ-2 Guide family + matching + prompt-known** (AGT-2128) | M/L | Guide metadata, Angular/.NET seeds, applicable Wiki/Hub view, bounded prompt selection. | Existing Wiki and prompt-known hard-rule path. |
| **AQ-3 Orientation-pack contract** | M | One bounded resolver joining map revision, guides, and ADR/domain refs with traceable omissions. | AQ-1, AQ-2. |
| **AQ-4 Analysis Run inventory** | M/L | Index existing Analysis Reports and registered run families; comparable history and artifact drill-down. | Existing Analysis Report projection. |
| **AQ-5 Component grading contract + deterministic pilot** | L | Rubric/schema, evidence coverage, staleness, one non-LLM pilot dimension, Hub grade view. | AQ-1, AQ-4. |
| **AQ-6 Assisted grading and drift comparison** | L/XL | Supporting-agent grading, trend explanations, comparison to authored Architecture Model, follow-up previews. | AQ-3–5 plus drift/report consumers. |
| **AQ-7 Repeatable visual/performance survey families** | L | Register app survey, UI proof, and performance runs with history/evidence conventions. | AQ-4 and verify-instance work. |

Recommended order: AQ-1 and AQ-2 in parallel, then AQ-3, AQ-4, AQ-5. AQ-6
must not be hidden inside a “small grading UI” card.

## 11. Validation plan

- Golden fixtures for every discovery input and relationship type, stable ids,
  ignore rules, partial repositories, and deterministic output.
- Guide schema/matcher tests plus prompt snapshot tests proving selection,
  revision trace, ordering, size bounds, and path privacy.
- Contract tests joining component ids across current/history snapshots, run
  inventory, and grading reports.
- Browser tests for graph/list equivalence, guide match reasons, run history,
  grade freshness, empty/error states, keyboard flow, narrow width, and both
  themes.
- Security tests for path containment, reparse points, hostile manifests,
  oversized repositories, and no-execution discovery.
- Second-opinion architecture review against source-of-truth ownership,
  existing Analysis Report/Architecture Model contracts, slice boundaries, and
  prompt-cost/privacy risks before AQ-3 begins.
