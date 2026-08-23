# Telemetry layer: local usage events on a bus, orchestrator as consumer

Status: proposal, decision-pending. No `OrgTelemetryEvent`-shaped code exists
in the repository as of 2026-08-18. Extracted from the
[telemetry-layer dossier](../operations/telemetry-layer/index.html)
(`AGT-W38`, sourceTaskKey `AGT-2661`) so the settled design shape is
discoverable outside that dossier's rollout/pilot planning. The dossier
remains the system-of-record for the open bus-topology and positioning
decisions.

## Relationship to existing, already-shipped contracts

The proposal bridges two contracts that already exist and should stay
distinct:

- **Product Runtime Observability** —
  `docs/app/schemas/product-runtime-event.schema.json`, app-local
  `logs/runtime/*.jsonl`, read-only, no action authority.
- **Agent Message Bus** — `backend/Features/Bus/AgentMessageBusStore.cs`,
  `logs/bus/<project>/<date>.jsonl`, already the canonical historical channel
  consumed today by [token aggregation](token-aggregation.md).

The telemetry layer is the missing bridge between them: raw product events
are normalized and clustered, a derived bus observation references the raw
evidence, and a separate orchestrator policy decides on action. This
three-layer authority model — raw file is truth, bus is a derived
observation, orchestrator receipt is the action truth — generalizes cleanly
beyond this one proposal and is worth stating plainly regardless of which
bus-topology option is eventually chosen.

## Proposed envelope: `OrgTelemetryEvent/v1`

A small immutable envelope, additive to (not an edit of) the existing runtime
event schema:

- `schemaVersion` / `id` (sortable UUIDv7 or ULID);
- `occurredAt` (producer time) separate from `observedAt` (collector time);
- `source` (stable `appId`, component, version, instance, environment);
- `eventName` (stable kebab-case, e.g. `chat.message-send.failed`);
- `category` enum: usage, friction, error, performance, lifecycle, quality,
  delivery, security, health;
- `level` enum Trace..Fatal — describes the record, not action authority;
- `actor` (kind human/agent/system/unknown; app-local pseudonym by default);
- `contextRefs` — typed references only (project/task/run/route/etc.), no
  heavy payload;
- `fingerprint` — a hash of stable dimensions for clustering, excluding
  timestamps, prose, and raw IDs;
- `privacy` — classification, redactions, export policy; the collector
  rejects records marked `containsSecret`;
- a small, bounded `payload`.

## Log-level to action mapping

Level controls volume, not action:

- Trace/Debug never forward to the bus by default (local-only, short
  retention).
- Info forwards only allowlisted usage/lifecycle/quality/completion events.
- Warn is collected and deduplicated, but only emits a bus observation past a
  policy threshold or if acute.
- Error is clustered by fingerprint into one Problem observation, not one
  message per repeat.
- Fatal emits one High observation immediately after durable append and may
  open a needs-decision item — it does not grant restart authority.

## Privacy posture

Local-first, no cloud dependency by default. Never collect secrets (auth
headers, tokens, credentials). Never collect content by default (chat
messages, prompts, transcripts, audio, source, review-tool inputs). Reduced,
pseudonymous identity. Bounded paths and stacks — a redacted summary plus
reference, not raw. Export only through existing, explicit, reviewed
artifact-delivery workflows. Proposed retention: raw edge buffers 30 days
(Trace/Debug 3 days); derived bus/action data follows the existing workspace
task/Activity retention; checkpoints never delete an unacknowledged tail.

## Recommended bus topology (proposed, not approved)

The dossier's "Option C" hybrid, compared against file+poll-only (durable but
slow) and local-push-only (fast but couples app availability to Studio):

1. Each app atomically appends events to a local append-only JSONL buffer
   first — the source of raw truth.
2. An optional loopback HTTP wake-up (token-authenticated, local-interface
   bound, path-traversal-safe) notifies a collector.
3. The collector validates, redacts, fingerprints, and dedupes, advancing a
   durable checkpoint only after acceptance.
4. Agent Studio persists an intake receipt and emits one derived bus
   observation or error message with evidence references.
5. The existing SignalR `busMessageAdded` fan-out updates Activity; reconnect
   replays from durable bus files, not transient SignalR memory.

## Orchestrator consumption contract

Deterministic clustering produces a `TelemetrySignal` (fingerprint, window,
distinct-correlation count, evidence refs, privacy summary, linked open
card). The orchestrator picks from a bounded vocabulary only: observe,
prepare a card, ask (needs-decision), or invoke an already-approved recovery
recipe. Hard invariant: **the bus never performs the action step** — it is
inert; only the orchestrator acts, and only through existing APIs and
idempotency keys (one open card per `(appId, fingerprint, policyVersion)`).

This generalizes a pattern that is already live in two places: the
[Global Orchestrator Watcher](../operations/orchestrator-waechter/index.html)
concept (`AGT-W15`, sourceTaskKeys `AGT-2557`/`AGT-2581`) and its already-shipped
first instance, the visual QA guardian (`AGT-2654`): durable evidence, pure
policy, bounded existing-authority action, visible receipt.

## Ownership boundary (if the positioning decision is approved)

Agent Studio would own the app registry and IDs, cross-app normalization,
redaction, dedupe, clustering, derived bus observations, orchestrator policy,
task creation, and CI/CD convergence. Each product keeps its own domain event
semantics, analysis/scoring engines, conversation/content state, and pricing
logic. Agent Studio is explicitly not a runtime dependency for using an app,
nor a private-data warehouse.

## Living knowledge log

- 2026-08-18 (AGT-2671): extracted from the telemetry-layer dossier as part
  of an operator-mandated dossier curation pass. Design only; no runtime
  wiring exists yet.
