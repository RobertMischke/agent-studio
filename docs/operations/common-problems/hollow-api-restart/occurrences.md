# Occurrences

Chronological log. Newest at the top. UTC timestamps. One row per observation.

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| 2026-08-23T12:34:00Z | Operator restart of `agent-taskboard-stable` | manual `bash api.sh restart` | `api.sh` | Two consecutive restarts both reported success; the OrchestratorApi process from 12:34 kept serving. A `project-settings.json` patch never took effect. |
| 2026-08-23T10:34:00Z | Rebuild of `agent-taskboard-stable` after a stop | manual `bash api.sh stop` | `backend/bin` | Zombie PID 28116 survived the stop and held the DLLs, so the rebuild could not copy into `backend/bin`. The stop had reported "API stopped." |
