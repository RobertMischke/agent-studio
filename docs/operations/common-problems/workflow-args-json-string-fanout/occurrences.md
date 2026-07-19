# Occurrences

Chronological log. Newest at the top. UTC timestamps. One row per observation.

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| 2026-07-07T00:00:00Z | Workflow script without an argument type guard | Claude workflow | workflow fan-out | A serialized array was sliced as a string into about 2,800 five-character chunks, reached the 1,000-agent cap, and consumed about 1.8 million tokens. Sixteen completed chunks still contained usable partial results. |
