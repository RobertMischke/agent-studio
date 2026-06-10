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

### NEXT PHASES (deliberately deferred — higher risk, do with review)
4. **Namespace alignment** `OrchestratorApi.*` → `AgentStudio.<Host>.<Feature>`,
   and the legacy `*Info`/`*Model` → domain / `Response` renames. Mechanical but
   touches every file + every `using`; do per-feature, build-gated.
5. **`.Api` + `.Core` project split** per host, then the **3-host process split**
   (Studio ⟂ TaskServer ⟂ UpdateServer). This changes startup/DI/deploy and is the
   riskiest step — must keep the system runnable at every increment.
6. **Boundary/arch test** enforcing §1 of the styleguide (fail on a technical-role
   folder under `Features/`, or a cross-host domain leak).

> Folders were moved without changing namespaces (build-safe). Until phase 4,
> a file's namespace (`OrchestratorApi.Services.*`) may lag its folder
> (`Features/<domain>`); that is expected and is the next phase's job.
