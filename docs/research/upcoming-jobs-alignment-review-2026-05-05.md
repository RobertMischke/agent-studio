# Upcoming Jobs Alignment Review - 2026-05-05

## Scope

This review compares the upcoming Agent Software Studio job queue against the internal product documents:

- `README.md`
- `ROADMAP.md`
- `AGENTS.md`
- `docs/architecture/decisions/adr-archive.md`
- `docs/product/design-principles.md`
- `docs/architecture/bus/agent-message-bus.md`
- `docs/mockups/quality-system/`
- `docs/mockups/orchestrator-meta-cycle/`

It also checks whether the user's current request, a recurring or manually triggered "are we on track?" analysis, is represented in the product direction.

## Queue Snapshot

Observed workspace:

`C:\Projects\agent-taskboard-workspace\projects\agent-taskboard`

Snapshot on 2026-05-05:

| Lane | Count | Notes |
|------|------:|-------|
| `1-preparation` | 2 | Small human-intake backlog. |
| `2-ready` | 56 | Large implementation backlog. Many strategic slices are already queued. |
| `3-progress` | 2 | `client-identity-and-task-attribution` and `stuck-in-progress-cleanup-and-rule`. |
| `4-review` | 17 | Significant review backlog. Several items appear to have already produced docs, ADRs, or code and now need acceptance or follow-up. |

Additional observation: several folders under `2-ready` have no `job.json` (`chip-*`, `recov-*`, and one auto-cycle fix folder in the snapshot). The UI may ignore these, but the queue is easier to trust when the task repository has no lane folders that look like jobs but are not jobs.

## Overall Verdict

The product direction is broadly on track.

The current queue strongly matches the documented thesis:

- One coding task per project remains the hard boundary.
- The Task Access Layer is correctly positioned as the prerequisite for multi-client, fast reads, and cleaner mutation ownership.
- The Agent Message Bus, Product Runtime Observability, Token Usage, UX/UI, Test Quality, and Skills directions are aligned across roadmap, design mockup, schemas, and recent tasks.
- The meta-cycle concept already exists in docs and ADRs. It is not just a loose chat idea.

The main risk is not strategic drift. The risk is backlog and evidence hygiene:

- Too many large, adjacent themes are queued at once.
- Several completed planning or infrastructure items sit in `4-review`, which makes the board look less settled than the docs suggest.
- The workspace repository is very dirty, mostly due to task moves, new task folders, logs, and archived E2E artifacts. This is expected for a task repository, but it reduces confidence until committed or intentionally ignored.
- The dev checkout currently has a larger uncommitted client identity / task attribution change set. That likely belongs to the in-progress multi-client work. Do not mix meta-documentation commits with that code.
- Local `backend/appsettings.Local.json` still watches `Agent Software Studio` from the dev checkout, while the roadmap says dev should become a regression-test target and not appear in its own watched-project list. This may be intentional during transition, but it is a real drift signal.

## Theme Review

### 1. Task Access Layer and Multi-Client

Status: on track, high priority.

Evidence:

- `ROADMAP.md` has a dedicated Task Access Layer theme.
- `task-access-api-layer-extraction` is queued near the top of `2-ready`.
- The active dirty code set contains `ClientIdentity`, `ClientEndpoints`, frontend client service/interceptor work, and attribution tests. That is consistent with a multi-client direction.

Concern:

The Task Access Layer should stay ahead of broad UI additions. Otherwise every new surface reads job folders directly and makes the later migration more expensive.

Recommendation:

Keep `task-access-api-layer-extraction` and `client-identity-and-task-attribution` at the front of the queue. Treat them as platform prerequisites for companion, bridges, analysis reports, token attribution, and multi-client UI.

### 2. Agent Message Bus and Observability

Status: on track, partially delivered.

Evidence:

- `agent-message-bus-contract` and `agent-message-bus-store` are in `4-review`.
- Follow-up implementation slices are queued in `2-ready`: bridging existing events, project observability panel, supporting-agent events, and system-health reader.
- `docs/architecture/bus/agent-message-bus.md`, schemas, and roadmap language are aligned.

Concern:

Do not build several separate analysis/report timelines. The bus should become the common event spine. Analysis reports should reference bus messages and artifacts rather than duplicating the whole raw event stream.

Recommendation:

Accept or follow up the two review-lane bus tasks before starting several new report UI panels.

### 3. Product Runtime Observability

Status: on track, still early.

Evidence:

- Roadmap has the theme.
- Contract, capture, project surface, and runtime-log-analysis skill are queued.
- Marketing already frames this as "software that can explain itself while it is being built".

Concern:

This should remain separate from the Agent Message Bus data model. Runtime product events should be linked from agent messages, not mixed into the same schema.

Recommendation:

Keep the documented separation. First ship the runtime event contract and one capture path before adding dashboards.

### 4. UX/UI, Test Quality, Token Usage, and Skills

Status: on track, but broad.

Evidence:

- The integrated quality-system mockup is the single source for this direction.
- Roadmap explicitly says UX/UI, Test Quality, and Token Usage become project-level menu entries.
- The queue contains `integrate-creative-design-mockup`, token bubble/card work, token timeline, runtime analysis, source/code metrics, and testing/QA surfaces.

Concern:

This cluster can turn into a second product inside the product. The guardrail is action-driven reports: every button creates evidence, and follow-up work becomes normal queued tasks.

Recommendation:

Implement one vertical slice first: action button, Markdown report, structured JSON contract, history list, and raw fallback. Then reuse that pattern for UX/UI, QA, Security, Architecture, Token Usage, and meta-analysis.

### 5. Expanded Lifecycle Lanes

Status: on track, concept-first approach is right.

Evidence:

- Roadmap has the expanded lane model.
- Queue contains concept, ready intake, post-processing, grouping/collapse, and migration compatibility tasks.

Concern:

This touches the filesystem contract and the board mental model. It should not ship as ad-hoc new folders until the state model is settled.

Recommendation:

Keep V1 as virtual lanes or substates over the existing six filesystem states, as the roadmap already suggests.

### 6. Meta-Cycle and "Are We On Track?" Analysis

Status: concept exists, product surface incomplete.

Evidence:

- ADR-0022 captures the meta-cycle.
- `docs/mockups/orchestrator-meta-cycle/` defines a control panel and `MetaCycleReport`.
- `docs/schemas/meta-cycle-report.schema.json` exists.
- `orchestrator-meta-cycle-self-monitor` is in `4-review`.

Gap:

The recurring "are we on track?" review is represented as a meta-cycle and Layer 3 system monitor, but the product does not yet have a general first-class "Analysis Reports" area that can store manual and scheduled analyses across scopes.

The user request is broader than the current meta-cycle:

- manual trigger
- optional scheduled cadence
- Markdown report for human reading
- optional JSON contract for app parsing
- results stored for future agents
- own UI area
- reusable for queue health, roadmap drift, security, architecture, QA, token usage, and product-runtime analysis

Recommendation:

Create a new "Analysis Reports" product surface and queue implementation tasks. It should reuse the schema-first in-memory layer, Agent Message Bus references, and existing report contract language in `docs/product/design-principles.md`.

### 7. Dev / Stable Role Split

Status: direction is correct, current local config still shows transitional drift.

Evidence:

- Roadmap says dev is a regression-test target, not a self-task target.
- `separate-dev-from-stable-roles` is queued.
- `dev-as-playwright-target-only` is in `4-review`.
- `backend/appsettings.Local.json` in the dev checkout still lists `Agent Software Studio` as a watch path.

Concern:

Running self-tasks from dev increases crash and dirty-state confusion. This is exactly the class of risk the stable/dev split is meant to reduce.

Recommendation:

Finish and accept the dev/stable split tasks before long unattended runs. Until then, treat self-watch from dev as a known transitional risk.

## Priority Recommendation

Recommended next order:

1. Finish or accept the review-lane infrastructure tasks that already landed: Agent Message Bus contract/store, JSON schemas/in-memory layer, meta-cycle, crash recovery, stable restart, dev-as-Playwright target.
2. Run `fix-auto-cycle-respects-active-job` before any automation invokes `update-stable` around active jobs.
3. Keep `task-access-api-layer-extraction` and `client-identity-and-task-attribution` ahead of multi-client UI and bridge work.
4. Add the Analysis Reports surface as the common pattern for manual and scheduled meta-analysis.
5. Use the quality-system mockup as the UI vocabulary for analysis actions, report history, JSON parse status, and follow-up task creation.

## Documentation Drift Findings

Required syncs:

- `README.md`: mention meta-cycle and recurring/manual analysis reports beside meta documentation and supervision.
- `ROADMAP.md`: add an explicit Analysis Reports / Meta-Analysis theme.
- `AGENTS.md`: include Layer 2.5 meta-cycle in the multi-loop model.
- `docs/product/design-principles.md`: make analysis reports first-class action results, not just Skill outputs.
- Marketing docs: explain periodic/manual "is this on track?" analysis as part of the trust layer.

ADR status:

No new ADR is needed for this request. ADR-0022 already captures the load-bearing meta-cycle decision. This work is a narrative and roadmap sync plus implementation task creation.

## New Product Work To Queue

Create these jobs:

1. `analysis-report-contract-and-storage`
   - Define the generic analysis report contract: Markdown plus optional structured JSON, schema, artifact references, source prompt, scope, cadence, tags, and follow-up task links.
2. `project-analysis-reports-surface`
   - Add a project-level Analysis Reports menu area with manual trigger, scheduled runs, history, parse status, filters, and drill-down.
3. `roadmap-alignment-analysis-action`
   - Implement the "are we on track?" analysis as a named action that compares queue, README, ROADMAP, ADRs, design principles, mockups, and recent reports.

These should not replace the meta-cycle. The meta-cycle is one automated producer of analysis reports. Manual actions and scheduled project audits are additional producers that use the same report surface.
