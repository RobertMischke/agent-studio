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

More schemas land here as concepts get formalised (audit findings, performance probes, companion snapshots). Keep one concept per file. Filename is `<concept-kebab>.schema.json`.

## Conventions

- Draft 2020-12. Set `$schema` accordingly.
- `$id` is `https://agent-taskboard.local/schemas/<concept>.schema.json`. The host is fictional; the path is what consumers compare against.
- All field names are camelCase to match the Web JSON serialisation policy in the backend (`JsonSerializerDefaults.Web`) and the TypeScript model files.
- All timestamps are ISO 8601 UTC with `Z` suffix. Type `string`, `format: date-time`.
- Enums spell the values in PascalCase to match the C# records (e.g. `Severity = High | Warn | Info`).
- Every schema has a top-level `description` and per-field `description`. The schema is doc, not just validation.

## Validation

The backend's in-memory store (planned in `json-schemas-and-in-memory-layer` task) loads all schemas at boot, validates every read and every write, and refuses to serve invalid records. Tests under `backend.Tests/SchemaValidationTests.cs` lock the round-trip of canonical examples.
