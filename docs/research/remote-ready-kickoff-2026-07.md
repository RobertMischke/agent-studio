# Remote-ready execution — kickoff (2026-07)

**Status.** **Committed — remote execution is a major goal** since 2026-07-07
(**ADR-0059**, [adr-archive.md](../architecture/decisions/adr-archive.md)).
This document is the plan of record and the working log for the theme.
**Phase 0 and Phase 1 are complete (2026-07-07)** — see "Phase 1 findings"
below; next up is Phase 2 (central URL + auth), gated on the security-overview
rewrite and D1.
It builds directly on the 2026-05 platform trilogy
([`wsl2-vs-windows-decision-2026-05.md`](./wsl2-vs-windows-decision-2026-05.md),
[`cli-orchestration-survey-2026-05.md`](./cli-orchestration-survey-2026-05.md),
[`path-forward-plan-2026-05.md`](./path-forward-plan-2026-05.md)) and on the two
concept docs that already sketch the architecture:
[`task-execution-and-log-architecture.md`](../concepts/task-execution-and-log-architecture.md)
(Server/Runner split, migration steps 1–6) and
[`parallel-task-execution.md`](../concepts/parallel-task-execution.md) §8.2C
(the binding multi-system lease contract). Nothing here contradicts those docs;
this kickoff turns their open steps into a concrete, phased plan.

---

## 1. Why now

Running everything on one Windows machine has become a real drag: the machine
carries the backend(s), two ng-serve frontends, every CLI agent process, every
Playwright run, dev-server previews, and the operator's own IDE + browser.
Symptoms: general slowness, expensive start/babysit cycles for the whole stack,
and resource contention between agent runs and interactive work.

**Goal:** move the heavy execution off the operator's machine.

- A **remote Linux host** runs the coding-agent CLIs (claude/codex) and
  Playwright.
- Tasks live on a **separated task server** reachable under **one central
  URL** — runners and UIs talk to that URL instead of a local folder.
- **SSH** is the expected access path to the runner host; Linux because it is
  easier to script and provision than Windows.
- The operator machine keeps only the browser (UI) and the dev seat
  (ADR-0015 stays untouched: the *dev seat* remains Windows-native; this plan
  is about where *execution* happens).

---

## 2. Target picture

```
[Operator machine]                [Central URL]                  [Linux runner host]
  browser (SPA) ────────────────► Task Server (OrchestratorApi   ◄──────── Runner process
  dev seat (IDE, git)             behind reverse proxy:            spawns claude/codex CLIs
                                  /api + /hubs, TLS, auth)         runs Playwright + dev servers
                                  owns task store + registry       has repo checkouts (git origin)
                                  owns git-backed evidence          local live logs, ships chunks up
                                                                    access: SSH
```

Three roles, per `task-execution-and-log-architecture.md`:

| Role | Owns | Runs where |
|---|---|---|
| **Task Server** | durable task state, lanes, registry, history/aggregated logs, lease issuing | any host with the central URL (can stay on the Windows box first) |
| **Runner** | CLI spawn, PTY probes, live log files, Playwright, dev-server previews | remote Linux host (later: N hosts) |
| **Client seat** | browser SPA, dev work | operator machine |

Code distribution is **via git `origin`, never via the task store**
(`parallel-task-execution.md` §8.2C): the runner fetches `task/<id>` branches;
the platform owns commit/push (ADR-0019/0050/0057 unchanged).

---

## 3. Ground truth — what couples us to one machine today

Condensed from a 4-angle code survey (2026-07-07). File references point at the
current `main`.

### 3.1 Task store = local filesystem, path-as-identity

- Store root `TaskRepository` (e.g. `C:\Projects\agent-taskboard-workspace`),
  flat layout `projects/<key>/tasks/<bucket>/<TASK-KEY>/`
  (`backend/Features/Tasks/TaskStorageLayout.cs`); lane is metadata in
  `task.json` (good — API-friendly), legacy lane folders still use
  `Directory.Move`.
- Change detection is a per-watch-path `FileSystemWatcher`
  (`TaskWatcherService.cs`) — does not survive a network mount reliably;
  SignalR change events (`TaskChangeNotifier`: `jobCreated/…`) already exist
  as the push replacement for remote consumers.
- **Absolute Windows paths are wire-level identifiers**: `?watchPath=` on
  nearly every `/api/tasks` route, `TaskKey = "<abs path>::<jobId>"`. ADR-0042's
  `PROJ-NNN` ids are the prepared substitute and must become the only
  addressing before a central URL makes sense.
- `task.json` writes are non-atomic read-modify-write; safety today = one
  process + in-process lane mutex. A single authoritative task server *fixes*
  this class — provided runners stop writing files directly.
- The workspace root is a **git repo**; `WorkspaceArtifactCommitService`
  commits run evidence, `TaskFileHistoryService` serves history via `git
  log/show/diff`. The task server must keep owning a git checkout.

### 3.2 CLI execution = in-process spawn, filesystem contract

- One engine (`CliExecutionServiceBase` + `CliBehavior` per CLI); the runner
  calls `StartAsync` in-process — the seam for a remote split is this
  interface, or moving the whole runner role to the Linux host (recommended
  first: move the runner, keep the interface).
- The CLI child writes artifacts **directly into the task store**
  (`JOB_RESULTS_DIR=<jobFolder>/results`), reads `prompt.md`, agents write
  `status.md`/`results/*` per protocol. Biggest single blocker: a remote
  runner needs either a local task-folder mirror or every write routed
  through the API (today only `POST /api/runner/logs` exists).
- Already Linux-friendly: home resolution (`USERPROFILE` **then** `HOME`),
  POSIX git guard (`AgentGitCommandGuard` writes a POSIX wrapper +
  `SetUnixFileMode`), Codex prompt prefix has an `isWindows` branch, env
  hardening is OS-neutral, `api.sh`/`_lib.sh` carry `lsof`/`ss`/`kill`
  fallbacks.
- Windows-only machinery that **disappears on Linux**: `.CMD` shim
  resolution, npm shim healing (`NpmShimHealer` is a documented no-op on
  non-Windows), `taskkill` fallback, handle-scrub spawner, CP1252 workarounds.
- **Missing on Linux**: a `TaskProcessReaper` equivalent (process groups /
  `setsid` + `kill -pgid`, else Playwright/dev-server grandchildren leak —
  the AGT-1791 bug class), and Porta.Pty needs a smoke test on the target
  distro (used only by quota/model-discovery probes, not by main runs).
- **Existing but unused remote seams**: lease API
  (`POST /api/runner/lease/{acquire,renew,release}`, TTL 120 s, built for
  owners whose PID can't be probed) and log ingestion
  (`POST /api/runner/logs`). ADR-0044 explicitly hands the remote topology to
  this lease contract.

### 3.3 Topology = loopback everywhere, no auth

- Everything binds `127.0.0.1` (backend 5030/5031, frontends 4010/4011,
  update-service 5039). No static file hosting exists — "stable" is a
  permanently running `ng serve`. A central URL needs a reverse proxy
  (nginx/caddy) forwarding `/api` + `/hubs` (websocket upgrade) same-origin;
  the SPA uses only relative URLs (`baseUrl = '/api'`, `.withUrl('/hubs/jobs')`)
  and then works unchanged.
- **`X-Client-Id` is a registration boundary, not auth** (its own doc comment
  says so); reads are anonymous by design; the SPA hardcodes
  `local-default`. On a central URL this is an open door — real auth is a
  hard precondition (see D4).
- Known hardcoded URLs to template: the orchestrator boot prompt bakes in
  `http://127.0.0.1:5030/api/*` (already wrong for stable today), task-api
  skill scripts default to `127.0.0.1:5031`, CORS origins list 4010/4200,
  update-service targets `127.0.0.1:5031`.
- Playwright/E2E already has the right seams (`PW_BASE_URL`,
  `PW_BACKEND_URL`, `PW_TARGET`) and the screenshot pipeline is
  remote-clean on the serving side (relative `/api` URLs, harvested from
  `results/playwright/`); only capture assumes CLI + job folder share a
  filesystem — solved by running Playwright on the runner host next to the
  CLIs (which is exactly the plan).
- Project-URLs feature splits brain when remote: backend starts dev servers
  on its own host (`ProjectUrlProcessService`, Linux branch exists) while the
  browser probes `http://localhost:<port>` — needs host-qualified URLs or a
  preview proxy through the central URL.

### 3.4 Prior art to honor (and two docs to retire)

- `task-execution-and-log-architecture.md`: steps 1–3 partially landed
  (Job→Task rename, per-stream logs); **steps 4–6 (runner split, server log
  ingestion, standalone server) are precisely this project.**
- `parallel-task-execution.md` §8.2C is binding: one authoritative task
  server, leases with **fencing tokens** (TTL alone is insufficient), origin
  as the only code channel, per-runner identities, and the listed acceptance
  tests (split brain, partition behavior).
- ADR-0018 (companion relay) is the only component ever deployed on Linux
  (Dockerfile + Fly.io) and the working precedent for "central URL + bearer
  token, outbound-only from home".
- **To rewrite:** `docs/operations/security/overview.md` declares remote
  orchestration an explicit non-goal — this kickoff reverses that product
  statement and requires a new threat model. `onboard-a-project.md` ("remote
  URLs are not supported") falls with origin-based distribution.
- CI reality check: only `frontend-lint.yml` runs on ubuntu; there is **no
  Linux build/test of the backend at all** yet.

---

## 4. Decisions to make (in order)

| # | Decision | Options / leaning |
|---|---|---|
| **D1** | **Task addressing without local paths** | Replace `watchPath` params + `<path>::<id>` task keys with `PROJ-NNN` (+ `PROJ-NNN::<taskKey>`). Prepared by ADR-0042; prerequisite for everything else. |
| **D2** | **Runner ⇄ server transport** | Leaning: runner consumes the existing REST surface + SignalR change feed, writes only via API (lease, logs, artifact upload — the artifact/write endpoints must be built). Alternative (sync/mirror of task folders) contradicts §8.2C "no dual writers" and is rejected. |
| **D3** | **What moves first** | Leaning: keep Task Server on the Windows box initially (it already owns the store + git), move the **runner role** to Linux first. Standalone/relocatable server is step 6, not step 1. |
| **D4** | **Auth for the central URL** | Minimum: bearer tokens per client/runner identity (extend the existing client registry), TLS via reverse proxy, `/hubs` included. §8.2C additionally wants per-runner least-privilege git credentials. |
| **D5** | **Headless CLI auth on Linux** | Subscription OAuth without a browser is the known weak spot (risk 5.5 in `path-forward-plan`). Options: one-time interactive `ssh -L` login per host, or seeding `~/.claude/.credentials.json` + onboarded `~/.claude.json` (the clean-context allowlist documents the exact file set). Needs a decision on credential rotation. |
| **D6** | **Runner deploy/update story** | ADR-0021/0031 machinery is same-disk Windows; the Linux runner needs its own (systemd unit + git pull + health check is probably enough to start). |
| **D7** | **Preview/dev-server access** | Dev servers started by the runner run on the Linux host; browser needs host-qualified project URLs or a preview proxy under the central URL. |

---

## 5. Phased plan

**Phase 0 — SSH test environment (operator). ✅ done 2026-07-07.**
Provision a Linux host (decision 2026-07-07: **Ubuntu LTS**; hosting via
Hetzner — cloud VM vs. Server-Börse dedicated box still open, see kickoff
discussion). Needs: dotnet 10 SDK or runtime, node 22 + npm, git,
`claude`/`codex` CLIs, Playwright deps (`npx playwright install --with-deps`).
Operator provides SSH access (key-auth, one sudo-capable user); agents can
then script against it. No inbound ports beyond SSH until Phase 2 auth lands.

**Phase 1 — prove the pieces on Linux (no architecture change). ✅ done
2026-07-07 (carry-overs listed in the findings section).**
1. CI: add a `dotnet build + test` job on `ubuntu-latest` (cheapest first
   step; also fulfils the promise from the WSL2 doc that was never built).
2. On the SSH host: run backend + one project end-to-end locally (Linux-only
   smoke: `api.sh start`, one claude run, one Playwright run). Fix what
   breaks; expected: process-group reaping, Porta.Pty verification, path
   assumptions.
3. Provision CLI auth headlessly (D5) and document the runbook in
   `docs/operations/setup/`.

**Phase 2 — task server gets a central URL (still on Windows).**
Reverse proxy with TLS + auth (D4) in front of stable; retire `watchPath`
addressing (D1); template the orchestrator boot prompt's API base; SPA served
statically or via the proxy. Exit criterion: board + orchestrator fully usable
via the central URL from a second machine, with auth on.

**Phase 3 — runner split (the §8.2C gate).**
Runner process on the Linux host consumes the task server API only: lease with
fencing token, heartbeat, log chunk shipping, artifact upload endpoints (to be
built), git-origin code distribution. `.pickup-lock.json` retires in favor of
the lease API. This phase must pass the §8.2C acceptance tests before any
second runner appears.

**Phase 4 — Playwright + previews remote.**
E2E and screenshot capture run on the runner host (`PW_BASE_URL` →
central URL); preview proxy or host-qualified project URLs (D7).

Each phase lands independently; after every phase the current single-machine
setup keeps working (the runner role simply stays local until Phase 3 cuts
over).

---

## 6. First concrete steps once the SSH host exists

```bash
# 1. base provisioning (Ubuntu-ish)
sudo apt-get install -y git curl build-essential
# dotnet 10, node 22 via the usual channels; then:
npm i -g @anthropic-ai/claude-code @openai/codex
npx playwright install --with-deps chromium

# 2. one-time CLI auth (D5 — interactive over SSH port-forward, or seed files)
claude          # complete OAuth + onboarding once; verify: claude --version && /usage works
codex login

# 3. clone + smoke
git clone <repo> && cd agent-taskboard
./api.sh start                     # expect: boots on 5030
# create a throwaway task via REST, watch a claude run execute on Linux

# 4. report findings back into this doc (append a "Phase 1 findings" section)
```

Open items to test explicitly on first contact: Porta.Pty on the distro,
orphan containment (`setsid`/process groups), `~/.claude/projects/<encoded-cwd>`
heartbeat path encoding on Linux paths, and Playwright headless deps.

---

## Phase 1 findings (2026-07-07)

**Verdict: Phase 1 complete — every piece proven on Linux, including a full
end-to-end task run through the real runner.** Host: the Phase-0 Hetzner box
(Ubuntu 24.04); reproducible setup + headless-auth runbook:
[`linux-runner-host.md`](../operations/setup/linux-runner-host.md).

| Piece | Result |
|---|---|
| claude headless (D5 seeding) | ✅ `claude -p` → `RUNNER-OK` (2.1.202). Seeding `~/.claude/.credentials.json` + minimal `~/.claude.json` works exactly as D5 sketched — **no interactive OAuth needed** |
| codex headless | ✅ `codex exec` → `CODEX-OK` (0.142.5), seeded `~/.codex/auth.json` |
| Playwright | ✅ headless chromium screenshot |
| repo access | ✅ public repo, plain https clone (canonical URL is now `agent-studio.git` after the rename) |
| `dotnet build` (sln) | ✅ 0 errors on `10.0.301` |
| `dotnet test` | ⚠️ **3295/3337 passed (99.3%), 23 failed, 19 skipped** — clusters below |
| CI | ✅ `backend-ci.yml` on `ubuntu-latest` added (`e4a7bcbf`), green; test step is `continue-on-error` until the suite is Linux-green |
| backend boot | ✅ `dotnet run` with a Linux `appsettings.Local.json` + `seed-demo-workspace.mjs --root`; registry bootstrap (ADR-0042) seeded `PROJ-001..003` with Unix paths |
| **Porta.Pty** | ✅ **quota probe end-to-end on Ubuntu 24.04**: PTY spawn, wizard-gate walk, `/usage` parse → plan *Max* + both windows. The 2026-07 probe hardening carries over unchanged |
| **E2E task run** | ✅ task created via `POST /api/tasks` + `POST …/start` → runner spawned claude (pid observed), **worktree flow (ADR-0057) worked on Linux** (`/tmp/ass-worktrees/...`, `task/<id>` branch), file artifact produced, lane transitioned `2-ready → 3-progress → 4-auto-review` |

### Test-failure clusters (23)

1. **UpdateServiceIntegrationTests (8)** — same-disk Windows update machinery
   (ADR-0021/0031); expected, D6 replaces it with systemd on Linux.
2. **`Analyze_AgainstLiveDevCheckout_*` + CodePatternRuleLoader + SteeringDocs
   (5)** — machine-bound: expect the live dev checkout / docs tree of the
   operator machine.
3. **MergeEndpointsIntegrationTests (3)**, **ProjectRepoResolutionTests (3)**
   — likely path-separator/derivation assumptions; *these matter for the
   runner* and need real fixes.
4. Singles: `TaskFolderAccessIsolationTest`, `FilesystemLayerSnapshotService`,
   `WorktreeTaskLifecycle` (fs semantics), `ClaudeEventAdapter.RateLimitEvent`
   (needs a look — pure unit test, shouldn't be OS-bound).

### Gotchas learned (feed into Phase 2/3 design)

- **`watchPath` for in-repo layout is `<RootPath>/.orchestrator/jobs`**, not
  the RootPath, on create/start API calls — exactly the addressing confusion
  D1 (`PROJ-NNN`) is meant to kill.
- **Worktree base branch must exist**: `git worktree add` failed with
  `invalid reference: develop` on a repo without a `develop` branch; the
  runner correctly *refused* the run ("refusing shared main checkout").
  Follow-up: make the base branch configurable or fall back to the default
  branch.
- **Process containment confirmed as real**: `nohup … &` over ssh died on
  session teardown; `setsid` was required. Reinforces the Phase-1 risk item —
  the Linux runner needs process-group reaping + a systemd unit (D6).
- `DOTNET_ROOT=/usr/lib/dotnet` must be exported in non-login shells.
- Credential seeding shares the refresh token with the operator machine —
  rotation/drift risk documented in the runbook §3; durable per-host answer
  is part of D5's rotation decision (before Phase 3).

### Carry-over into Phase 1.x (before Phase 2 starts)

1. Classify + fix the 23 Linux test failures (machine-bound → tag/skip with
   reason; real bugs → fix); then drop `continue-on-error` in `backend-ci.yml`.
2. Worktree base-branch fallback (above).
3. `~/.claude/projects/<encoded-cwd>` heartbeat encoding on Linux was not
   explicitly inspected — verify during the next runner-host session.

---

## 7. Risks (carried over + new)

| Risk | Source | Mitigation |
|---|---|---|
| Headless subscription auth drift | `path-forward-plan` 5.5 | runbook + seeded credential file set; re-test on CLI updates |
| Split brain / dual writers | §8.2C risk table | task server is the only writer from Phase 3; fencing tokens |
| Anonymous reads on a public URL | topology survey | auth lands in Phase 2 *before* any port is exposed beyond SSH |
| Process leaks on Linux | no reaper equivalent yet | build process-group containment in Phase 1 |
| FileSystemWatcher over network FS | task-store survey | never mount the store remotely; API + SignalR events instead |
| Deploy complexity ×2 | ADR-0021/0031 are Windows-only | keep runner deploy dumb (systemd + git pull) until it hurts |

---

## 8. Documentation debt this creates

- Rewrite `docs/operations/security/overview.md` (remote orchestration is now
  a goal; new threat model, auth story, SSH access path).
- Update `docs/operations/setup/onboard-a-project.md` once origin-based
  distribution lands.
- New runbook: `docs/operations/setup/linux-runner-host.md` (Phase 1 output).
- ADR needed once D1–D4 are decided (successor to ADR-0044's explicit
  "remote topology goes through the lease contract" handoff).
