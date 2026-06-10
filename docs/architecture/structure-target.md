# Backend Structure — Target & Migration Status

Companion to [STYLEGUIDE.md](STYLEGUIDE.md). Describes the target project layout
and tracks the by-hand structure migration (2026-06-10).

## Target

```
/frontend                      Angular (FE/BE separation; unchanged)
/backend
  /Host                        Program.cs, EndpointMapping, SignalR Hubs, DI composition root
  /Features                    fractal feature folders (see STYLEGUIDE)
    /Cli                       Execution · OutputParsing · Completion · Pty · Quota · Routing
    /Tasks                     Transitions · Attribution · Scanner · Merge · Audit · Commits · …
    /Runner  /Pipeline  /Review  /Drift  /Supervisor  /Tokens  /Projects
    /Docs  /Prompts  /Conversation  /Bus  /Diagnostics  /Design  /Registry  /…
    /Git                       git/worktree abstraction (library-shaped)
  /Shared                      cross-cutting contracts/DTOs — kept MINIMAL
/tools, /companion, /update-service   separate deployables
```

Three hosts, each `.Api` (thin host) + `.Core` (feature monolith):
**Studio** (FE) · **TaskServer** (runs tasks) · **UpdateServer** (:5039).
Hosts talk via DTOs only. No physical technical layers — feature folders, fractal.

## Migration status

### DONE (2026-06-10, namespace-stable, build green + tests at each step)
1. **Consolidated `src/AgentTaskboard.{Shared,OutputParser,Runner}` into one
   backend project** — the three layer-projects are dissolved; 84 files moved;
   solution + project-references cleaned. (`2a32c2d9`)
2. **`Services/` technical layer removed → `backend/Features/<domain>/`**; ALL
   CLI consolidated under `Features/Cli/{Execution,OutputParsing,Pty,Quota,Routing}`;
   `GitService → Features/Git`. (`e5b52c78`)
3. **All 52 endpoints + 15 loose services co-located into their feature**; `Host/`
   created (Program/EndpointMapping/System/Hubs); `Features/` root has zero loose
   files (fractal sub-folders throughout). (`fc9ff1ae`)

Result: ONE backend project, fully feature-foldered, CLI consolidated, endpoints
with their features. No endpoint/route change.

### DONE (2026-06-10, phase 2)
4. **Namespace alignment** `OrchestratorApi.*` → **`AgentStudio.<Feature>`** —
   namespace now follows the folder exactly (one namespace per top-level
   feature; sub-feature folders share it). 696 files, scripted sweep,
   build-gated. Feature namespaces are project-wide global usings (folders are
   the isolation unit, not usings). The **assembly name stays `OrchestratorApi`**
   on purpose: start/stop/watchdog scripts match the process name.
   The `<Host>` namespace segment is deliberately deferred to the host split —
   host membership is decided when the `.Api`/`.Core` projects exist, so no
   guess gets baked in now (inserting one segment is then per-project trivial).
6. **Boundary/arch test** `backend.Tests/Architecture/FeatureFolderBoundaryTests`:
   forbids technical-role folders under `Features/` at any depth, enforces
   namespace==folder, and pins the retirement of the `OrchestratorApi` namespace.

### NEXT PHASES (deferred — need operator review)
5. **`.Api` + `.Core` project split** per host, then the **3-host process split**
   (Studio ⟂ TaskServer ⟂ UpdateServer) with DTO contracts at the host boundary.
   Riskiest step (startup/DI/deploy change); requires the host classification of
   the shared features (Tasks, Git, Bus, Tokens, Projects are used by both
   Studio and TaskServer today — the contested cut needs an operator decision).
7. **`*Info`/`*Model` → domain renames** (e.g. `TaskCommitInfo` → `Commit`).
   Wire-safe (JSON property names don't change), but `TaskInfo` → `Task`
   collides with `System.Threading.Tasks.Task` — the domain word for it
   (e.g. `TaskCard`?) is an operator pick before the rename runs.
