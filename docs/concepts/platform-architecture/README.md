---
id: platform-architecture-index
title: "Platform architecture index"
status: active
category: concept
updatedAt: 2026-08-21
last-updated: 2026-08-21
reason: "One coherent home for the durable execution and delivery architecture extracted from decision dossiers"
taskKey: AGT-2671
tags: [architecture, index, platform, distributed, delivery]
related-tasks: [AGT-2671]
related-adrs: []
related-docs:
  - "docs/concepts/README.md"
  - "docs/system/architecture/README.md"
  - "docs/system/domains/README.md"
---

# Platform architecture index

The durable architecture of the execution and delivery platform: who may write,
how a delivery reaches the integration branch, which processes own which state,
and what the organization telemetry contract would be.

Every page here was **extracted from a decision dossier** under
`docs/operations/`. Dossiers are decision instruments with a lifecycle; they
move to History once their decision is settled and their slices are delivered.
Architecture must outlive that lifecycle, so the durable mechanisms, invariants
and contracts live here and the dossier keeps the decision drama, the option
comparisons and the approval record. Each page links back to its source
dossier, and each source dossier carries a pointer to its page.

## Where this sits

| Layer | Owns | Location |
|---|---|---|
| Decision dossiers | The open question, the options, the recommendation, the operator decision | `docs/operations/<topic>/`, listed under Dossiers |
| **Platform architecture (this folder)** | The durable mechanism, invariants, contracts, failure modes, delivered versus open | `docs/concepts/platform-architecture/` |
| Domain maps | The current system of record for an area | [`docs/system/domains/`](../../system/domains/README.md) |
| ADRs | Load-bearing decisions and deliberate non-goals | [`docs/system/architecture/decisions/`](../../system/architecture/decisions/adr-archive.md) |

A page graduates out of this folder when its area gets a domain map or an ADR.
Until then this is the single place to read before changing any of these
mechanisms.

## Pages

| Page | What it covers | Source dossier | Status |
|---|---|---|---|
| [Fencing, leases, and authority](fencing-leases-and-authority.md) | Lease claim/renew/release/expire, the per-task monotonic fencing token, the global Authority Epoch as a soft-drain recovery generation, and how the two compose with the idempotency key. | [Hardening distributed execution](../../operations/haertung-verteilte-ausfuehrung/index.html) (`AGT-W7`) | Delivered behaviour (dossier §9, accepted) |
| [Rebase, merge, and promotion invariants](rebase-merge-and-integration-invariants.md) | The attribution-lens frame for why recovery rewrites SHAs, canonical integration preserves them, and promotion publishes an exact tested SHA; the merge-first/rerere/mapped-rebase integration ladder; the two bounded, already-shipped recovery mechanisms. | [Rebase, merge, and bounce steering](../../operations/rebase-merge-and-steering/index.html) (`AGT-W37`) | Mechanics delivered; automation slices open |
| [Task Server gate topology](task-server-gate-topology.md) | The target `GateSubject`/`GateAttempt`/`GateLease`/`GatePlan`/`GateReport` object model for claimable build/test gates, the materialization sequence, and the timeout/fallback taxonomy. | [Remote Gate Target Architecture](../../operations/remote-gate-zielbild/index.html) (`AGT-W18`) | Decided target, not yet implemented |
| [Batch Gate](batch-gate-mechanics.md) | What the Batch Gate proposal is (one suite for a closed delivery wave) and, more importantly, which four adjacent mechanisms it depends on are already shipped versus still open. | [Batch Gate](../../operations/batch-gate-concept/index.html) (`AGT-W36`) | Proposal decision-pending; page is mostly "not yet decided" by design |
| [Telemetry layer](telemetry-layer.md) | Why this is a proposed bridge between the already-shipped Product Runtime Observability and Agent Message Bus contracts, not a new bus, and what stays out of scope until an operator decides. | [Telemetry layer](../../operations/telemetry-layer/index.html) (`AGT-W38`) | Proposed, decision-pending, not coded |

## How to read a status column

- **Delivered behaviour** means the page was verified against the code during
  extraction and describes what the platform does today. Where the source
  dossier disagreed with the code, the code won and the divergence is recorded
  in that page's living knowledge log.
- **Decided target** means an operator-approved shape that other
  system-of-record docs already treat as adopted, but no code implements it
  yet. Do not confuse this with shipped behaviour.
- **Proposed / decision-pending** means no operator decision exists yet.
  Nothing in those sections is implemented. Do not build against them without
  an approval, and treat "Approve" language inside the dossier as
  recommendation language, not a record that approval happened.

## Adding a page

1. The content must be durable: a mechanism, an invariant, a contract, a
   failure mode or a naming rule. Decision drama stays in the dossier.
2. Verify every cited path, route and symbol against the checkout before
   writing it down. Prefer the code over the dossier when they disagree, and
   note the divergence.
3. Add the back-link header to the source dossier, add a pointer to this page
   in that dossier's `workbench.json` summary, add a row to the table above,
   and add a row to [`docs/start/README.md`](../../start/README.md).
4. End the page with a **Living knowledge log** section, newest entry on top.

## Living knowledge log

Append new findings here, newest on top.

- **2026-08-21 (AGT-2671).** Folder created during the operator-ordered
  org-wide dossier curation pass. A prior interrupted attempt at this same
  task had drafted an equivalent folder and five pages on an unmerged salvage
  branch (`agent-studio/salvage/agent-runner-01/AGT-2671/...`, 2026-08-18/19);
  this pass re-verified every claim against the 2026-08-21 checkout rather
  than replaying that branch, since two days of further commits (notably the
  AGT-W34 S2/S3 slices landing, and continued AGT-W39 log entries) had made
  parts of it stale. See each page's living knowledge log for what changed.
