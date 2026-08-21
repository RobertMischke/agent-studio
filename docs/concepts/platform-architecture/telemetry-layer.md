---
id: telemetry-layer-concept
title: "Telemetry layer: a proposed bridge between Product Runtime Observability and the Agent Message Bus"
status: active
category: concept
updatedAt: 2026-08-21
last-updated: 2026-08-21
reason: "Durable-content extraction pass on the AGT-W38 telemetry-layer dossier: capture what is already decided and coded (the bus/runtime-observability split) and clearly flag what the dossier only proposes, so a future reader does not mistake recommendation language for a shipped design"
taskKey: AGT-2671
related-tasks: [AGT-2661, AGT-2557, AGT-2654]
related-adrs: []
related-docs:
  - "docs/system/architecture/bus/agent-message-bus.md"
  - "docs/operations/runtime/observability.md"
  - "docs/operations/telemetry-layer/index.html"
---

# Telemetry layer: a proposed bridge, not a new bus

> **What this page is.** A concept page for the AGT-W38 "organization-wide
> telemetry layer" dossier. It exists to stop a common misreading: the
> dossier is thorough, dated, and full of "Approve" language, which can look
> decided at a glance. It is not. This page states plainly what is already
> decided and coded today (the split between Product Runtime Observability
> and the Agent Message Bus), and what the dossier only *proposes* on top of
> that split. New findings — especially any future operator decision on the
> dossier's open request — belong in the
> [Living knowledge log](#living-knowledge-log) at the bottom. The dossier
> itself is the system-of-record artifact for the proposal:
> [`docs/operations/telemetry-layer/index.html`](../../operations/telemetry-layer/index.html)
> (AGT-W38, source task AGT-2661).

## TL;DR

- **Decided and coded today:** two separate, already-shipped contracts.
  [Product Runtime Observability](../../operations/runtime/observability.md)
  captures what the *built software* did (`ProductRuntimeEventStore`,
  `<job|project>/logs/runtime/<date>.jsonl`). The
  [Agent Message Bus](../../system/architecture/bus/agent-message-bus.md)
  captures who *observed, decided, advised, or intervened*
  (`AgentMessageBusStore`, `logs/bus/<project>/<date>.jsonl`). The two
  streams are joined only by reference (`AgentArtifactRef` of kind
  `runtime-event`, or a shared `correlationId`); neither embeds the other.
- **Proposed, not decided:** AGT-W38 asks for a *new* layer that sits in
  front of both — a shared `OrgTelemetryEvent/v1` envelope, a per-app local
  collector, a "hybrid" file-plus-push bus, a deterministic clustering/policy
  step, and a positioning statement that Agent Studio is the cross-app
  operations hub. As of 2026-08-21 this is still `status: decision-pending`
  in the dossier's own `workbench.json`, and the dossier's final section is
  an open *request* for seven operator decisions, not a record that they
  were made.
- **Not coded anywhere.** `OrgTelemetryEvent`, `TelemetrySignal`, and any
  collector service do not exist in `backend/` or `frontend/`. If you are
  looking for the shared envelope or the hybrid bus in code, it is not
  there yet — you would be building it for the first time.
- **The proposal explicitly does not replace the bus.** It says outright
  that it "extends two existing contracts without merging them." If the
  proposal is ever approved, the Agent Message Bus keeps its current job
  (observability spine, inert, never an actor) and simply gains one more
  upstream producer: a derived `observation`/`error` message from the new
  collector, referencing raw evidence instead of duplicating it.

## The two contracts that already exist

| Contract | Answers | Storage | Doc |
|---|---|---|---|
| Product Runtime Observability | What did the built app do, when, how fast, with what outcome? Producer is the software under construction. | `<job>/logs/runtime/<date>.jsonl`, `<project>/logs/runtime/<date>.jsonl` | [`docs/operations/runtime/observability.md`](../../operations/runtime/observability.md) |
| Agent Message Bus | Who observed, decided, advised, intervened, or asked? Producer is an agent/orchestrator/supervisor/runtime/user. | `logs/bus/<project>/<date>.jsonl`, participant docs | [`docs/system/architecture/bus/agent-message-bus.md`](../../system/architecture/bus/agent-message-bus.md) |

Both are already file-first, append-only, schema-validated, and explicitly
**not** workflow engines: emitting an event or a bus message never moves a
job, starts a task, or grants capability. That non-goal is stated
independently in both source docs and is one of the few things the AGT-W38
dossier does not need to re-decide — it inherits it.

## What AGT-W38 proposes on top (decision-pending, uncoded)

Everything below is the dossier's *recommendation*, not a shipped design.
Treat every bullet as "proposed" even where the prose sounds settled.

- **Shared event envelope, `OrgTelemetryEvent/v1`.** A small immutable
  record (`schemaVersion`, `id`, `occurredAt`/`observedAt`, `source.appId`,
  `eventName`, `category`, `level`, `actor`, `contextRefs`, `fingerprint`,
  `privacy`, bounded `payload`) meant to unify telemetry across Agent
  Studio, Quality Studio, Coding Agent Chat, Coding Agent Runner, Token
  Economy, its website, the Agent Studio website, and voice-lint. The
  dossier is explicit that this is "a future schema, not an in-place edit"
  and that the existing runtime-event schema's `additionalProperties: false`
  means organization fields need an explicit schema decision, not a
  `payload` smuggle.
- **Local-first hybrid bus (Option C).** Every app still appends to its own
  durable local JSONL first (never lost, never blocks the app), then
  optionally sends a best-effort loopback push so Agent Studio can react
  immediately when reachable; a collector reconciles via checkpoint/replay
  either way. The dossier compares this against file-only (Option A, "good
  baseline, slower feedback") and push-only (Option B, "not sufficient as
  the only path") and recommends C. This is a *new* collector plus hybrid
  delivery path, not a description of the existing Agent Message Bus's own
  writer, which is already append-only-to-disk-then-in-memory-projection
  with no local-push option of its own.
- **Orchestrator action rules.** A deterministic cluster/policy step turns
  repeated fingerprinted signals into one of a bounded set of actions
  (observe, prepare a card, ask for a decision, or invoke an
  already-approved recovery recipe) — never a direct mutation. Concrete
  example thresholds (e.g. "same Error fingerprint 3 times across 2
  correlations in 30 minutes creates one Backlog healing card") are pilot
  parameters proposed for the Coding Agent Chat slice, not settled
  organization-wide policy.
- **Agent Studio as operations-hub boundary.** The dossier proposes that
  Agent Studio owns app registry/collector config, cross-app normalization
  and clustering, derived bus observations, and CI/CD-plus-telemetry
  convergence — while each product keeps its own domain event semantics,
  analysis engines, and content (Quality Studio's scoring, Chat's message
  content, Token Economy's pricing logic, voice-lint's audio/linguistic
  analysis). This is a positioning statement awaiting an operator decision,
  not an established boundary today.

## Relationship: bridge, not replacement

If you are trying to figure out whether the telemetry-layer dossier is the
same thing as the Agent Message Bus, the answer is no on both ends:

- It does not replace or restate the Agent Message Bus contract. The bus's
  participant model, message lifecycle, storage shape, and non-goals in
  [`agent-message-bus.md`](../../system/architecture/bus/agent-message-bus.md)
  are unaffected either way.
- It does not replace Product Runtime Observability either. Per-app raw
  events stay local and app-owned under the existing
  [`observability.md`](../../operations/runtime/observability.md) contract.
- What it adds, *if approved*, is the missing cross-app collection and
  action loop between them: a normalized envelope so many apps' events look
  the same, a durable local-first delivery path so Agent Studio downtime
  never blocks a producer, and a policy layer that turns clustered signals
  into bus observations and, ultimately, reviewable cards, through existing
  Task API and runner authority, never a new mutation path.

## Practical guidance for now

1. **Do not build against `OrgTelemetryEvent` or a collector.** Neither
   exists. If your task needs cross-app telemetry today, it does not have
   a home yet — flag it back to AGT-2661/AGT-W38 rather than inventing a
   local variant.
2. **Keep using the two existing contracts as-is.** Runtime facts about the
   software you are building go through
   [Product Runtime Observability](../../operations/runtime/observability.md).
   Agent decisions/observations/advisories go through the
   [Agent Message Bus](../../system/architecture/bus/agent-message-bus.md).
   Join them by reference (`AgentArtifactRef` kind `runtime-event`, or a
   shared `correlationId`) exactly as both docs already specify.
3. **If the dossier's decisions land, expect additive, not breaking,
   changes.** The bus keeps its current shape; it would simply gain a new
   upstream producer (the collector) emitting `observation`/`error`
   messages the same way any other bridged source does today.
4. **Watch `docs/operations/telemetry-layer/workbench.json`'s `status`
   field**, not the dossier's own "Recommended answer" prose, for whether
   this has actually been decided. As of this page's last update it is
   still `decision-pending`.

## Living knowledge log

Append new findings about the telemetry-layer proposal here, newest on top.
Keep each entry short: date, what was learned, and a pointer to the
code/commit/task.

- **2026-08-21 (AGT-2671).** Page created during a documentation-transfer
  extraction pass on the AGT-W38 dossier. Confirmed by grep that no section
  of the dossier carries an actual operator approval — every "accepted" /
  "decided" / "approved" hit is either the dossier's own recommendation
  language or an unrelated feature name (`AcceptedIntegrationBackstopPolicy`).
  Confirmed by repo-wide grep that `OrgTelemetryEvent` and `TelemetrySignal`
  do not exist in `backend/` or `frontend/` — none of the dossier's new
  mechanisms are coded. The dossier explicitly frames itself as extending,
  not merging into, the existing Product Runtime Observability and Agent
  Message Bus contracts, which is the one durable, already-true relationship
  fact this page exists to preserve.
