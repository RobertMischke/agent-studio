# Docs Structure Migration

> **Archived on 2026-07-29:** retained as migration history. Use the
> [Wiki tree contract](../../system/contracts/wiki-tree.md) for the current
> physical documentation structure.

Status: implemented on 2026-06-11. This page records the reasoning and move
map for the physical `docs/` tree migration.

> Follow-up 2026-07-18: the `docs/wiki/` subfolder was dissolved; its content
> now lives directly under `docs/` (`concepts/`, `common-problems/`,
> `learnings/`, `home.json`). Path references below reflect the 2026-06 state.

## Goal

The Wiki should make the repository documentation feel like a maintained
knowledge base: most pages stay simple Markdown, selected visual overview pages
can be self-contained HTML, and operators can find the right page by domain
instead of by historical accident.

The current tree already has strong islands:

- `docs/operations/setup/` for onboarding and troubleshooting.
- `docs/system/schemas/` for wire and disk contracts.
- `docs/mockups/` for locked UI references.
- `docs/research/` for dated deep dives.
- `docs/wiki/common-problems/` for recurring runtime and workflow failures.
- `docs/quality/frontend/style-guide/` for UI vocabulary.
- `docs/quality/visual-features/` for screenshot-backed feature documentation.

The weak spots are mostly at the root. Many documents are useful, but their
placement mixes domains, reports, architecture decisions, frontend audits, CLI
contracts, and concept notes at the same level.

## Markdown and HTML rule

Use Markdown as the default format.

Markdown fits:

- contracts and operating rules,
- ADRs and concept notes,
- runbooks and troubleshooting,
- living knowledge logs,
- source-of-truth domain maps,
- docs that agents need to quote, diff, and patch often.

Use HTML only when the document is primarily visual or spatial.

HTML fits:

- architecture maps,
- system diagrams,
- visual reports,
- interactive-free mockups,
- dense dashboards that need layout, color, or diagram-like grouping.

HTML wiki pages must be self-contained and safe in the Wiki iframe:

- no JavaScript requirement,
- no external assets unless deliberately linked,
- readable on a white background,
- accessible headings and section labels,
- linked from `docs/start/README.md` like Markdown pages.

Current example: [`docs/system/architecture/maps/knowledge-map.html`](../../system/architecture/maps/knowledge-map.html).
It demonstrates a sandboxed HTML page next to Markdown architecture docs.

## Implemented top-level taxonomy

Target shape:

```text
docs/
  README.md
  domains/
    README.md
    runner.md
    pipeline.md
    tasks.md
    frontend.md
    cli.md
    tokens.md
    token-pricing.md
  architecture/
    README.md
    model.md
    decisions/
      README.md
      adr-archive.md
      proposed/
    backend-structure/
      styleguide.md
      structure-target.md
    bus/
      agent-message-bus.md
      implementation-state.md
    maps/
      knowledge-map.html
    runner-lanes/
  product/
    README.md
    design-principles.md
    orchestrator-chat.md
    orchestrator-chat-redesign-handoff.md
    companion-app-design.md
    skills-architecture.md
  frontend/
    README.md
    design-system.md
    style-guide/
    audits/
    performance.md
    testing.md
  cli/
    README.md
    supported-clis.md
    skills/
    audits/
    investigations/
  operations/
    setup/
    security/
    runtime/
    git/
    testing/
  contracts/
    README.md
    filesystem.md
    protocol-style.md
    agent-task.md
    run-outcome.md
  concepts/
  in-app-help/
    README.md
    lane-guides/
  reports/
    README.md
    analysis-reports.md
    drift-reports.md
    visual/
    html/
  research/
  schemas/
  mockups/
  assets/
  wiki/
    common-problems/
    concepts/
    learnings/
```

The migration deliberately uses real directories. The Wiki renders this physical
tree; old root-level document files were not kept as compatibility shims.

## Where content landed

| Former area | New home | Notes |
|---|---|---|
| `runner-domain.md`, `pipeline-domain.md`, `tasks-domain.md`, `frontend-domain.md`, `cli-domain.md` | `docs/system/domains/` | Root files were moved, and AGENTS/tests now point to the new paths. |
| `architecture-model.md` | `docs/system/architecture/model.md` | This is a core architecture input for drift analysis. It deserves the architecture folder. |
| `architecture-decisions.md` and `docs/system/architecture/decisions/proposed/*` | `docs/system/architecture/decisions/` | Long term: split accepted ADRs into one folder. Short term: keep the archive stable. |
| `architecture/STYLEGUIDE.md`, `architecture/structure-target.md` | `docs/system/architecture/backend-structure/` | These are backend structure docs, not general architecture docs. |
| `architecture-3-progress-lane-writers.md` | `docs/system/architecture/runner-lanes/` | It is an architecture constraint for runner/lane writers. |
| `agent-message-bus.md`, `bus-implementation-state.md` | `docs/system/architecture/bus/` | They describe the event spine and implementation status. |
| `bus-architecture-report.html`, `orchestrator-system-visual-report.html` | `docs/reports/html/` | HTML reports should not sit at the root. |
| `cli-skills/`, `supported-clis.md`, `cli-domain.md` | `docs/system/cli/` | Keep per-CLI pages under `skills/`; audits and investigations should be separate. |
| `cli-startup-cost-analysis-*`, `cli-model-selector-audit.md`, `codex-runner-investigation.md` | `docs/system/cli/audits/`, `docs/quality/frontend/audits/`, and `docs/system/cli/investigations/` | These are evidence documents, not everyday entry points. |
| `frontend-scss-quality*`, `frontend-architecture-review-*`, `perf-frontend.md`, `frontend-testids.md` | `docs/quality/frontend/` | Split between `audits/`, `performance.md`, and `testing.md`. |
| `design-system.md`, `design-principles.md`, `style-guide/` | `docs/quality/frontend/` and `docs/product/` | The visual system is frontend-owned; design principles are product-facing. |
| `concepts/` | `docs/concepts/` | Keep as future/current architecture concepts. Add subfolders by area. |
| `concept-docs/` | `docs/in-app-help/lane-guides/` | Backend route resolution was updated with the move. |
| `wiki/concepts/` | `docs/wiki/concepts/` | Keep. These are living explanatory pages with logs, not source-of-truth contracts. |
| `token-aggregation.md` and `wiki/concepts/token-aggregation.md` | `docs/system/domains/tokens.md` plus `docs/wiki/concepts/token-aggregation.md` | This pair is intentional: contract vs living explanation. Make that pairing explicit in indexes. |
| `analysis-reports.md`, `drift-reports.md` | `docs/reports/` | They define report contracts and UI behavior. |
| `product-runtime-*` | `docs/operations/runtime/` | Keep runtime observability together instead of split at root. |

## Duplicate or confusing pairs

These are not necessarily wrong, but they should be labelled clearly.

### Domain map vs concept page

Example: `token-aggregation.md` and `wiki/concepts/token-aggregation.md`.

Recommended rule:

- domain doc owns current contract and implementation pointers,
- wiki concept owns explanation, history, and living log.

Every pair should cross-link at the top.

### ADR archive vs proposed ADRs

`architecture-decisions.md` is the accepted historical archive. `docs/system/architecture/decisions/proposed/` has
newer proposed or sliced ADRs.

Recommended rule:

- accepted ADRs live in one decision archive,
- proposed ADRs live beside it with `status: proposed`,
- no accepted decision exists in two complete copies.

### UI design system vs style guide vs audits

`design-system.md`, `style-guide/`, `frontend-scss-quality*`, and audit pages
all talk about UI quality.

Recommended rule:

- `design-system.md`: conceptual system and tokens,
- `style-guide/`: component-level vocabulary,
- `frontend/audits/`: time-stamped findings and migration reports.

### CLI entry points vs investigations

`supported-clis.md`, `cli-domain.md`, `cli-skills/`, startup cost analysis, and
Codex investigations are all useful, but they answer different questions.

Recommended rule:

- entry point: supported CLIs and invariant contracts,
- per-CLI skills: operational details,
- investigations: dated evidence and root-cause notes.

## Migration result

- Root-level document files now live in physical categories.
- `docs/start/README.md` is a category index, not a long mixed flat table.
- Category READMEs were added for `architecture`, `domains`, `contracts`,
  `product`, `frontend`, `cli`, `operations`, `reports`, `in-app-help`, and
  `assets`.
- `docs/in-app-help/lane-guides/` replaced the old `docs/concept-docs/`
  location, and backend concept-doc lookup now targets the new path.
- `docs/operations/security/` replaced the old `docs/security/` location, and
  the Security docs service now targets the new path.
- `docs/system/architecture/decisions/adr-archive.md` replaced the old root ADR
  archive path.

## Living knowledge log

- 2026-06-11: Initial structure proposal created after reviewing the current
  `docs/` tree. The most visible issue is not lack of content, but root-level
  mixing of domains, reports, audits, HTML artifacts, and concept notes.
- 2026-06-11: Physical migration executed. The root folder now contains real
  categories rather than virtual groupings or compatibility shims.
