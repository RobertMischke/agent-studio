# Organization-Wide Telemetry Layer

Status: **proposed, not yet approved or implemented**. This page extracts the
durable architecture from the decision dossier below so the design survives
independently of the dossier's own lifecycle. It is not a record of current
product behavior.

Source: [`docs/operations/telemetry-layer/index.html`](../operations/telemetry-layer/index.html)
(AGT-W38, source task AGT-2661). The dossier's own header states "Scope:
concept only, no implementation" and closes with a request for seven operator
decisions plus the author's recommendation - not a recorded approval. Treat
every design below as a proposal.

## Why

Agent Studio applications (Coding Agent Chat, Token Economy, the website,
voice-lint, and Agent Studio itself) each accumulate their own logs and
signals with no shared, privacy-bounded event model and no common path from
"three related failures happened" to "one backlog card exists." This proposal
defines that shared layer.

## Event envelope (`OrgTelemetryEvent/v1`, a future schema)

- `schemaVersion`, `id` (sortable UUID v7 / ULID)
- `occurredAt` / `observedAt` kept separate (producer time vs. collector time)
- `source`: `appId`, component, version, instance, env, repo id
- `eventName`: stable kebab-case, e.g. `chat.message-send.failed`
- `category` enum: usage / friction / error / performance / lifecycle /
  quality / delivery / security / health
- `level` enum: Trace-Fatal - describes the record, not the response ("level
  is not policy": event name, category, privacy class, fingerprint,
  correlation count, cooldown, and recipe authority are evaluated together)
- `actor`: kind (human/agent/system/unknown), local pseudonym by default
- `contextRefs`: typed references only
- `fingerprint`: stable-dimension hash used for clustering
- `privacy`: classification, redactions, export policy
- `payload`: a small, bounded allowlist

## Log level to behavior mapping

| Level | Default behavior |
| --- | --- |
| Trace / Debug | Never forwarded by default |
| Info | Forwarded only for allowlisted usage/lifecycle/quality/completion events |
| Warn | Collected and deduped; emits one observation only past a threshold |
| Error | Clustered by fingerprint into one Problem observation |
| Fatal | One High observation emitted immediately after durable append |

## Delivery: recommended hybrid bus (Option C)

Three options were compared: file-first plus collector, local HTTP push plus
SignalR, and a hybrid. The hybrid is recommended: durable per-app JSONL
append, plus an optional best-effort local HTTP wake-up to Agent Studio; the
collector owns retry and dedupe. Five-step write contract:

1. The app appends atomically to its local JSONL file.
2. An optional loopback wake-up carries `appId`/offset/event id.
3. The collector validates, checkpoints, and fingerprints.
4. Agent Studio persists an intake receipt, then emits one derived bus
   observation with evidence refs (the bus itself never performs this step -
   see below).
5. `busMessageAdded` fans out over SignalR, with reconnect replay from durable
   bus files, not transient memory.

## Orchestrator consumption

A deterministic cluster service produces a `TelemetrySignal` from the derived
bus observations. Proposed pattern-to-action mapping:

| Pattern | Trigger | Action |
| --- | --- | --- |
| Repeated usage friction | >=5 hits / 3 correlations / 7 days | Feature card, needs-decision, one open card per `appId+fingerprint+policyVersion` |
| Error cluster | 3 hits / 2 correlations / 30 min, or 1 Fatal | Backlog healing card via Task API + idempotency key - never auto-starts a run |
| Performance regression | 3 windows over budget + 50% baseline | Trend observation; card only after the 2nd confirming window |
| Noisy warning | - | Digest, max one per day |
| Visual QA clear defect | - | Consume durable `verdict.json` receipts from AGT-2654 (already implemented), not screenshots or model prose |
| Pipeline/delivery alarm | - | Reuse existing hanging-gate/stalled-lane facts |

## Agent Studio as the hub

The proposal frames Agent Studio as "the organization management layer for
software delivery and operations... where CI/CD state, telemetry, alarms, and
orchestration become one inspectable operating picture across products." It
would own: the app registry and collector config, normalization / redaction /
dedupe / clustering, derived bus observations, guardian/orchestrator policy
and Task API card creation, and CI/CD convergence (promotion train, Actions
hygiene, deployment evidence). Each product keeps its own domain logic (QS
analysis engine, Chat conversation content, Token Economy pricing, voice-lint
audio analysis) - this layer only carries the cross-product signal.

## Relationship to already-decided/implemented pieces

- **AGT-2557 / AGT-W15 (Global Orchestrator Watcher,
  [`docs/operations/orchestrator-waechter/index.html`](../operations/orchestrator-waechter/index.html)):**
  the dossier positions this telemetry layer as supplying the Watcher's
  missing cross-product trigger substrate - the Watcher would consume clusters
  and receipts, not raw streams. Also still decision-pending as of this
  writing.
- **AGT-2654 (review pipeline visual QA, already delivered):** treated as the
  first implemented guardian consumer and reference shape for
  telemetry-triggered action, not overlapping content.

## Proposed rollout

Pilot: **Coding Agent Chat error-cluster pilot**, event
`chat.message-send.failed`. Acceptance story: three events with the same
normalized fingerprint across two distinct correlations inside 30 minutes ->
the collector replays once, Agent Studio shows one clustered Problem in
Activity, and the orchestrator creates exactly one Backlog healing card with
evidence references and an idempotency receipt.

Slices: T0 (contract fixture) -> T1 (chat edge instrumentation) -> T2 (hybrid
delivery/parity) -> T3 (one action/policy) -> T4 (two-week observe-only soak,
target >=95% of created cards judged useful). Proposed rollout order after the
pilot: Quality Studio -> Agent Studio/Runner -> Token Economy/website -> Agent
Studio website -> voice-lint -> remaining apps. None of these slices had
started as of this page's writing (2026-08-20).

## Living knowledge log

- 2026-08-20: Page created from the decision dossier during a curation pass
  (AGT-2671). The dossier's source task (AGT-2661) and both related tasks
  (AGT-2557, AGT-2654) are archived and delivered, but delivery of the
  *analysis* is not an operator approval of the *telemetry architecture* - no
  such approval exists in the dossier text. Status stays decision-pending.
