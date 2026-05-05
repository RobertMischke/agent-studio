# JSON Schemas

Canonical home for cross-cutting structured-data formats. Each file is one Draft 2020-12 JSON Schema describing one concept that flows between layers (disk, backend, frontend, supervisor, companion app).

## Why this folder exists

Before this folder, structured records lived only as C# types and TypeScript interfaces. That meant:

- The disk format and the in-memory format could drift silently.
- Layer 3 review skills had no contract to read against.
- The companion app had to reverse-engineer types from the SignalR payloads.

Schemas in this folder are the single contract. C# records, TypeScript interfaces, and the in-memory store all derive from these schemas.

## Contents

- `supervisor-advisory.schema.json` - one entry in `logs/meta/<project>/observations.jsonl`.
- `supervisor-intervention.schema.json` - one entry in `logs/meta/<project>/interventions.jsonl`.
- `token-aggregate.schema.json` - per-project rolling totals of tokens, dollars, time-window.
- `agent-message.schema.json` - one record on the Agent Message Bus, append-only in `logs/bus/<project>/<date>.jsonl`. Contract in [`docs/agent-message-bus.md`](../agent-message-bus.md).
- `agent-participant.schema.json` - one actor on the bus (user, orchestrator, supervisor, coding agent, supporting agent, system-review, runtime, external).
- `agent-artifact-ref.schema.json` - typed pointer from a bus message to evidence on disk or another structured stream.
- `client-identity.schema.json` - one client of the Task Access Layer (human, agent instance, external tool, service). Stored under `<workspace>/identities/<id>.json`.
- `token-aggregate-by-client.schema.json` - per-client variant of `token-aggregate.schema.json`, additionally keyed by `clientId` for per-client legends in the workspace timeline.
- `drift-report.schema.json` - one project-level drift analysis report with scores, dimensions, evidence references, and follow-up task suggestions.

More schemas land here as concepts get formalised (audit findings, performance probes, companion snapshots). Keep one concept per file. Filename is `<concept-kebab>.schema.json`.

## Conventions

- Draft 2020-12. Set `$schema` accordingly.
- `$id` is `https://agent-taskboard.local/schemas/<concept>.schema.json`. The host is fictional; the path is what consumers compare against.
- All field names are camelCase to match the Web JSON serialisation policy in the backend (`JsonSerializerDefaults.Web`) and the TypeScript model files.
- All timestamps are ISO 8601 UTC with `Z` suffix. Type `string`, `format: date-time`.
- Enums spell the values in PascalCase to match the C# records (e.g. `Severity = High | Warn | Info`).
- Every schema has a top-level `description` and per-field `description`. The schema is doc, not just validation.

## Validation

The backend reads these documents through the file-backed in-memory store at [`backend/Services/State/InMemoryStore.cs`](../../backend/Services/State/InMemoryStore.cs). It validates every append (strict, rejects bad records), is lenient on read (skips a single bad legacy line so the projection never breaks), and exposes typed access by id, filtered queries, append-cursor reads, and optimistic concurrency. The full design rationale, including what intentionally stays out of scope (no database engine, no aggregate documents, no codegen step), lives in [ADR-0023](../architecture-decisions.md#adr-0023---json-schema-first-communication-formats-and-a-file-backed-in-memory-data-layer-2026-05-05).

Round-trips between the schemas and the C# records are locked by [`backend.Tests/SchemaRoundTripTests.cs`](../../backend.Tests/SchemaRoundTripTests.cs); the store contract itself is locked by [`backend.Tests/InMemoryStoreTests.cs`](../../backend.Tests/InMemoryStoreTests.cs).
