# Backend Style Guide — Feature Folders & Naming

> Authoritative reference for backend structure + type naming. Referenced from
> the root `AGENTS.md` and every host's `AGENTS.md`. New code MUST follow this.

## 1. Structure: feature folders, fractal, never technical layers

The backend is organized **by feature (sub-domain), recursively** — not by
technical role.

- **Every folder is a (sub-)domain**, e.g. `Features/Tasks/`,
  `Features/Tasks/Transitions/`, `Features/Cli/Execution/`.
- **A feature folder may contain feature folders** (fractal). `Tasks/` splits
  into `Transitions/`, `Attribution/`, `Merge/`, `Audit/`, … — each a sub-domain.
- **A leaf folder mixes roles by FILE**: its endpoint(s), service(s), domain
  types and request/response records sit side by side. That is not layering.
- When a leaf grows too large, split it into **sub-features**, never into
  technical sub-folders.

### FORBIDDEN at any depth (technical-role folders)
`Services/`, `Models/`, `Endpoints/`, `Handlers/`, `Dtos/`, `Helpers/`,
`Infrastructure/`, `Utils/` — as a *folder under a feature*. A boundary/arch
test should fail the build if one appears under `Features/`.

### ALLOWED
Sub-domain names: `Transitions/`, `Attribution/`, `Completion/`, `Execution/`,
`OutputParsing/`, `Quota/`, …

## 2. Naming: suffix by where a type lives

| Where | Suffix | Examples |
|---|---|---|
| **System / HTTP boundary** (FE↔backend, API in/out) | `Request` / `Response` | `CreateTaskRequest`, `TaskDetailResponse` |
| **Inside a feature folder** (domain type) | **none** | `Task`, `Commit`, `Worktree`, `Run` |
| **Pure host↔host exchange** (subsystem comms) | `DTO` | `TaskStateDTO` |

- **Never** use a generic `Model` / `Info` / `Data` suffix as a stand-in for a
  domain type. (Legacy `TaskInfo`/`TaskCommitInfo` are scheduled to become
  `Task`/`Commit` in the namespace-alignment phase — see
  [structure-target.md](./structure-target.md).)
- Requests/responses are the *contract* — they live in the feature whose
  endpoint owns them, next to that endpoint.
- Hosts talk to each other through DTOs only; domain types stay inside a feature.

## 3. Hosts (target)

Three deployables, each a thin `.Api` host over a `.Core` feature monolith:
`Studio` (serves the Angular FE) · `TaskServer` (runs CLI agent tasks) ·
`UpdateServer` (self-update, :5039). `Shared` is **minimal** — only cross-host
DTOs. Git is a **library** (`Features/Git` today). See
[structure-target.md](./structure-target.md) for the migration status.

## 4. Endpoints are frozen

Restructuring never changes an HTTP route string. Folder/namespace moves are
internal only; the API surface the frontend depends on stays identical.
