---
page: docs/concepts/mockups/remote-hosts-ux.html
knowledgeUiPage: concepts/mockups/remote-hosts-ux.html
category: concepts/mockups
pageType: HTML
producer: software-architecture-drift
cli: claude
lastEvaluatedUtc: 2026-07-14
scoreBand: Warn
overallScore: 56
priorReportId: 0120260622065152102d4c323d6
---

# Drift metadata - Remote Hosts UX mockups

Conceptual page-metadata note for
`docs/concepts/mockups/remote-hosts-ux.html`. The mockup itself was not edited.

## Verdict

**Warn.** The four screens still preserve the intended product boundaries,
but the page presents an older UX slice as if it were current. The implemented
Remote Hosts flow has gained a fifth onboarding step, write-capability proof,
expanded host lifecycle and telemetry, and explicit Task Server / Runner
ownership that the mockup does not show.

## What still matches

- Remote Hosts and Task Server are separate global settings surfaces, matching
  `frontend/src/app/features/shell/components/workspace-overlays/workspace-overlays.component.ts`.
- A project is assigned to one execution location and receives a readiness
  probe, matching
  `frontend/src/app/features/project-detail/components/execution-assignment-card/execution-assignment-card.html`
  and `frontend/e2e/project/project-execution-assignment.spec.ts`.
- The host view exposes heartbeat, capabilities, per-CLI quota, Re-Probe, and
  Drain, matching
  `frontend/src/app/features/remote-hosts/components/remote-host-card/remote-host-card.html`.
- The add-host flow retains an adjacent orchestrator chat, matching
  `frontend/src/app/features/remote-hosts/components/add-host-wizard/add-host-wizard.html`.
- The durable control-plane / execution-plane split is consistent with
  `docs/concepts/distributed-agent-studio-target-architecture.md`,
  `docs/domains/runner.md`, and `runner/RemoteTaskRunner.cs`.

## Drift findings

### D1 - Current-status framing is stale (Warn)

The mockup introduces itself only as four screens from
`remote-execution-product-integration.md` section 6. That source concept now
marks these slices historical and points to
`docs/concepts/distributed-agent-studio-target-architecture.md` as canonical.
The HTML has no status banner, canonical pointer, or explicit links, so the
Knowledge UI cannot distinguish historical UX intent from shipped behavior.

### D2 - Host onboarding contract has expanded (Warn)

The mockup shows four steps: Connect, Provision, CLI auth, and Smoke. The
implemented wizard has five steps and inserts a required **Push key** stage.
It verifies a per-host, per-repository write-enabled deploy key before CLI
authentication. Evidence:

- `frontend/src/app/features/remote-hosts/components/add-host-wizard/add-host-wizard.ts`
- `frontend/src/app/features/remote-hosts/components/add-host-wizard/add-host-wizard.html`
- `frontend/src/app/features/remote-hosts/models/runner-setup.model.ts`
- `docs/operations/setup/linux-runner-host.md`

The mockup also implies that chat directly performs SSH mutations. The current
component provides step guidance and explicit operator confirmations, while
runner setup is queued as a visible CLI task. The page should not promise an
execution capability that its current source refs do not demonstrate.

### D3 - Fleet status and lifecycle are under-specified (Watch)

The host table captures the original heartbeat, capability, quota, Re-Probe,
and Drain concepts. The current card surface also shows local and remote
execution locations together, daemon and claim state, available slots, git
push readiness, vitals, telemetry history, acute findings, retirement,
revival, and permanent deletion. Evidence:

- `frontend/src/app/features/remote-hosts/models/remote-host.model.ts`
- `frontend/src/app/features/remote-hosts/components/remote-hosts-panel/remote-hosts-panel.html`
- `frontend/src/app/features/remote-hosts/components/remote-host-card/remote-host-card.html`
- `docs/operations/remote-hosts.md`
- `docs/operations/setup/linux-runner-host.md`

This is additive product evolution, not an architecture violation, but it makes
screen 1 unsuitable as a complete current-state reference.

### D4 - Task Server screen is directionally accurate but UI-first (Watch)

The mockup's connected URL, store, evidence-git, client, and maintenance
concepts closely match the current Task Server panel. The current service still
documents a static seed shaped like a future `GET /api/task-server/status`
payload, so neither the mockup nor the implementation should be read as proof
that the complete server-status API is live. Evidence:

- `frontend/src/app/features/task-server/components/task-server-panel/task-server-panel.html`
- `frontend/src/app/features/task-server/services/task-server.service.ts`
- `frontend/src/app/features/task-server/models/task-server.model.ts`
- `frontend/src/app/features/task-server/components/task-server-panel/task-server-panel.spec.ts`

### D5 - Artifact language no longer follows repository policy (Watch)

The page's labels and notes are German, while `AGENTS.md` requires written
repository artifacts and user-facing UI strings to be English. The current
Angular surfaces use English. This is documentation / mockup drift, not runtime
architecture drift.

## Test and evidence freshness

Current behavior has direct unit and Playwright coverage:

- `frontend/src/app/features/remote-hosts/components/remote-host-card/remote-host-card.spec.ts`
- `frontend/src/app/features/remote-hosts/services/remote-hosts.service.spec.ts`
- `frontend/e2e/settings/remote-hosts.spec.ts`
- `frontend/e2e/project/project-execution-assignment.spec.ts`
- `backend.Tests/RemoteRunnerEndToEndTests.cs`

No recent reviewed-task evidence was supplied for this run, so code, tests,
contracts, and operations docs carry the score. Prior report
`0120260622065152102d4c323d6` is retained as the continuity id.

## Cross-cutting architecture finding

**High severity, project-wide:** no machine-readable architecture model was
found under the project `architecture/` store. The model contract and schema
exist at `docs/architecture/model.md` and
`docs/schemas/architecture-model.schema.json`, but there are no model elements
to score. This is independent of the mockup's `Warn` classification and should
remain a project-level follow-up rather than causing invented per-element
findings.

## Recommended action

**Page needs edits via a follow-up documentation task.** Add a historical
status banner and canonical links, translate the artifact to English, update
the wizard to five steps including push-key proof, and either refresh the
fleet / Task Server screens or label them as dated intent snapshots. Do not
change runtime code, schemas, or ADRs for this page-sync task.

Also queue a separate high-priority project task to author the missing
machine-readable architecture-model instance. No new project drift report was
posted during this evaluation: the page findings are documentation-specific,
and the missing-model condition was already supplied as known project context.
