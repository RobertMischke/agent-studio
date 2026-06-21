# API Project Identity and the watchPath Surface

Status: analysis recorded 2026-06-21. Documents how the HTTP API currently
identifies a project, why the raw `watchPath` should be encapsulated, and the
canonical-versus-legacy route split. Companion to the system-of-record map in
[../../domains/tasks.md](../../domains/tasks.md). Cleanup is tracked as a backlog
task (see "Cleanup task" below).

## The three project identifiers

A project can be named three ways, and only the registry-owned pair is meant for
the outside:

| Identifier | Example | Owner | Outward-facing? |
|---|---|---|---|
| `watchPath` | `C:\Projects\agent-taskboard-workspace\projects\agent-taskboard` | Storage / scanner | No (should be internal) |
| `projectId` | `PROJ-NNN` | Registry | Yes |
| `shortCode` | `ASS` | Registry | Yes |

`GET /api/projects` and `GET /api/workspaces` already expose `projectId` and
`shortCode`. The `ProjectLookupService` (frontend) and the registry (backend)
hold the mapping. So a clean "identify a project by its code, resolve the path
on the server" path is mostly already available, it is just not the one the task
API uses yet.

## Canonical route versus legacy alias

`/api/tasks` is canonical. `/api/jobs` is a thin compatibility alias mapped to
the same handlers in
[../../../backend/Host/EndpointMapping.cs](../../../backend/Host/EndpointMapping.cs)
(`MapTaskCrudEndpoints` + `MapTaskMergeEndpoints`). The domain is "Task"
throughout; the word "Job" survives only in the `CreateJobRequest` DTO and that
alias route. Prefer `/api/tasks` for any new call or script.

## Current state (IST, 2026-06-21)

The raw filesystem path is the de-facto external project key, which leaks the
disk layout to every client:

- `watchPath` appears as an endpoint parameter about 295 times across 36 backend
  files.
- The frontend constructs or passes `watchPath` about 567 times (excluding
  specs), concentrated in `task.service.ts`, `undo.service.ts`,
  `dev-tools.service.ts`, `git-hygiene.service.ts`,
  `code-review-activity.store.ts`, and `jobs-hub-client.service.ts`.
- `CreateJob` (`backend/Features/Tasks/TaskMutationService.cs`) matches the
  project by exact string equality on `watchPath`. When the field is empty it
  falls back to the first registered project, which is Runbook. So an omitted or
  slightly-off path silently lands the task in the wrong project. This is the
  same trap captured in
  [../common-problems/project-name-divergence-watchpath-vs-registry/README.md](../common-problems/project-name-divergence-watchpath-vs-registry/README.md).
- About 180 files still reference `/api/jobs`, mostly e2e specs, plus a few
  backend files, the `tools/perf-report/*.mjs` tooling, and several docs.

## Target

1. Identify projects across the API surface by `shortCode` or `projectId`. The
   server resolves the `watchPath` internally and never returns the raw path in
   a response (or returns it only as an opaque handle).
2. `/api/tasks` is the only public route. `/api/jobs` is removed, or kept as a
   deprecated HTTP 308 redirect with a defined grace period.

## Migration shape

This is epic-sized; split into slices rather than a big-bang change:

- Phase 1: migrate all `/api/jobs` consumers to `/api/tasks`, then retire the
  alias. Mechanical, low risk.
- Phase 2a: a central server-side resolver `project (shortCode | projectId) ->
  watchPath`.
- Phase 2b: endpoints accept `project` and resolve internally; `watchPath` stays
  accepted but deprecated, and is dropped from responses.
- Phase 2c: frontend sends `shortCode` or `projectId`; unwind
  `projectStorageByName` and `normalizeStorage` where possible.

API-contract changes are deliberate and belong in an ADR; endpoints are
otherwise treated as frozen. This is an identity and naming refactor only, with
no behavior change to lanes, the pipeline, or the runner.

## Cleanup task

Backlog task slug:
`api-aufraeumen-apijobs---apitasks--watchpath-hinter-projekt-kuerzel-kapseln`
(project ASS, lane `0-backlog`).

## Related

- [../../domains/tasks.md](../../domains/tasks.md) - system-of-record task map.
- [../../../.agents/skills/job-api/SKILL.md](../../../.agents/skills/job-api/SKILL.md) - how to call the task API today.
- [../common-problems/project-name-divergence-watchpath-vs-registry/README.md](../common-problems/project-name-divergence-watchpath-vs-registry/README.md) - the watchPath-versus-registry trap.
