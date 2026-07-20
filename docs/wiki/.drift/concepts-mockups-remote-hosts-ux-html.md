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

```json
{
  "verdict": "Warn: the mockup still preserves the Remote Hosts / Task Server boundary split, but presents a stale UX slice that omits the push-key onboarding step, expanded fleet lifecycle/telemetry, and English-language compliance the current implementation now has.",
  "scoreBand": "Warn",
  "overallScore": 56,
  "dimensions": [
    {
      "type": "Architecture",
      "score": 60,
      "severity": "Warn",
      "confidence": 0.85,
      "sourceCoverage": 0.6,
      "status": "New",
      "summary": "D1: page has no status banner or canonical pointer, so Knowledge UI cannot tell historical UX intent from shipped behavior.",
      "evidenceRefs": ["docs/concepts/mockups/remote-hosts-ux.html", "docs/concepts/distributed-agent-studio-target-architecture.md"],
      "recommendedActions": ["Add historical-status banner and canonical pointer to the mockup page"]
    },
    {
      "type": "Runtime",
      "score": 55,
      "severity": "Warn",
      "confidence": 0.9,
      "sourceCoverage": 0.8,
      "status": "New",
      "summary": "D2: implemented onboarding wizard has a fifth Push-key step verifying a write-enabled deploy key that the four-step mockup does not show.",
      "evidenceRefs": [
        "frontend/src/app/features/remote-hosts/components/add-host-wizard/add-host-wizard.ts",
        "frontend/src/app/features/remote-hosts/components/add-host-wizard/add-host-wizard.html",
        "frontend/src/app/features/remote-hosts/models/runner-setup.model.ts",
        "docs/operations/setup/linux-runner-host.md"
      ],
      "recommendedActions": ["Update onboarding wizard mockup to five steps including push-key proof"]
    },
    {
      "type": "Runtime",
      "score": 70,
      "severity": "Info",
      "confidence": 0.85,
      "sourceCoverage": 0.75,
      "status": "New",
      "summary": "D3: current host card surface adds execution-location merge, daemon/claim state, telemetry history, retirement/revival/deletion beyond the mockup's original fleet table (additive, not an architecture violation).",
      "evidenceRefs": [
        "frontend/src/app/features/remote-hosts/models/remote-host.model.ts",
        "frontend/src/app/features/remote-hosts/components/remote-hosts-panel/remote-hosts-panel.html",
        "frontend/src/app/features/remote-hosts/components/remote-host-card/remote-host-card.html",
        "docs/operations/remote-hosts.md",
        "docs/operations/setup/linux-runner-host.md"
      ],
      "recommendedActions": ["Refresh or label fleet/Task Server mockup screens as dated intent snapshots"]
    },
    {
      "type": "Schema",
      "score": 65,
      "severity": "Info",
      "confidence": 0.75,
      "sourceCoverage": 0.7,
      "status": "New",
      "summary": "D4: Task Server panel data contract still documents a static seed shaped like a future GET /api/task-server/status payload; neither mockup nor implementation proves the full server-status API is live.",
      "evidenceRefs": [
        "frontend/src/app/features/task-server/components/task-server-panel/task-server-panel.html",
        "frontend/src/app/features/task-server/services/task-server.service.ts",
        "frontend/src/app/features/task-server/models/task-server.model.ts",
        "frontend/src/app/features/task-server/components/task-server-panel/task-server-panel.spec.ts"
      ],
      "recommendedActions": ["Clarify server-status API liveness before treating Task Server screen as current-state reference"]
    },
    {
      "type": "Architecture",
      "score": 70,
      "severity": "Info",
      "confidence": 0.9,
      "sourceCoverage": 0.5,
      "status": "New",
      "summary": "D5: page labels/notes are German while AGENTS.md requires repository artifacts and UI strings to be English; current Angular surfaces already use English.",
      "evidenceRefs": ["AGENTS.md", "docs/concepts/mockups/remote-hosts-ux.html"],
      "recommendedActions": ["Translate remote-hosts-ux mockup artifact to English per AGENTS.md"]
    },
    {
      "type": "Architecture",
      "score": 20,
      "severity": "High",
      "confidence": 0.95,
      "sourceCoverage": 0.2,
      "status": "New",
      "summary": "Cross-cutting, project-wide: no machine-readable architecture model exists under the project architecture/ store, so no element-level score can be produced; independent of this page's Warn classification.",
      "evidenceRefs": ["docs/architecture/model.md", "docs/schemas/architecture-model.schema.json"],
      "recommendedActions": ["Author missing machine-readable architecture-model instance (project-level)"]
    }
  ],
  "architectureModel": {
    "elements": []
  },
  "followUpTaskSuggestions": [
    {
      "title": "Add historical-status banner and canonical links to remote-hosts-ux mockup",
      "summary": "The page presents four screens as current with no pointer to docs/concepts/distributed-agent-studio-target-architecture.md, which now supersedes them. A status banner and canonical link would let the Knowledge UI distinguish historical intent from shipped behavior.",
      "priority": "Normal",
      "relatedDimension": "Architecture"
    },
    {
      "title": "Translate remote-hosts-ux mockup artifact to English",
      "summary": "AGENTS.md requires repository artifacts and UI strings to be English, but the mockup's labels and notes are German while the shipped Angular surfaces are already English. Bring the mockup into policy compliance.",
      "priority": "Normal",
      "relatedDimension": "Architecture"
    },
    {
      "title": "Update onboarding wizard mockup to five steps including push-key proof",
      "summary": "The implemented add-host wizard verifies a per-host, per-repository write-enabled deploy key as a required fifth step before CLI auth. The mockup still shows only Connect, Provision, CLI auth, and Smoke, understating the setup contract.",
      "priority": "Normal",
      "relatedDimension": "Runtime"
    },
    {
      "title": "Refresh or label fleet/Task Server mockup screens as dated intent snapshots",
      "summary": "The current remote-host card and Task Server panel surfaces expose materially more state (daemon/claim, telemetry history, retirement/revival/deletion; server-status seed shape) than the mockup shows. Either refresh the screens or explicitly label them as historical intent.",
      "priority": "Low",
      "relatedDimension": "Runtime"
    },
    {
      "title": "Author missing machine-readable architecture-model instance",
      "summary": "No architecture model exists under the project architecture/ store even though docs/architecture/model.md and docs/schemas/architecture-model.schema.json define the contract. This blocks element-level drift scoring project-wide, not just for this page.",
      "priority": "High",
      "relatedDimension": "Architecture"
    }
  ]
}
```
