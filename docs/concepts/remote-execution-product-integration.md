# Remote execution in the product — user-facing integration concept

**Status:** historical product-integration slices, revised 2026-07-13. The
canonical three-component ownership and lifecycle model is now
[`distributed-agent-studio-target-architecture.md`](distributed-agent-studio-target-architecture.md).
This page remains useful for host onboarding and UI slices, but its earlier
"local Studio stays the brain" wording is superseded: Agent Studio is a
replaceable client, Task Server is the durable control plane, and Agent Runner
is the execution plane.

Originally a draft companion to
[`remote-ready-kickoff-2026-07.md`](../research/remote-ready-kickoff-2026-07.md)
(ADR-0059). The kickoff owns the *architecture* phases; this document answers
the *product* questions: how does a human decide "this project runs remote",
what do they see, and how is an existing project onboarded. Operational host
details live in
[`linux-runner-host.md`](../operations/setup/linux-runner-host.md).

---

## 0. Product stance (operator decision, 2026-07-07; sharpened 2026-07-08)

**The target picture, clarified 2026-07-13:** Agent Studio is the replaceable
human surface. The independently hosted Task Server remains the durable brain
and control plane. Remote hosts are execution arms that act only under
Task-Server authority, per project. Closing Agent Studio must not interrupt
Task Server or Runner. That is **Mode C (runner split)**. Mode A (full stack on
the remote host, viewed through a tunnel) was the Phase-1 proof and test bed,
not the plan. Phase 2 (central URL + auth) is the prerequisite for both Studio
and Runners to use the separated Task Server.

**Remote CLI execution is a first-class product concept**, not an ops
appendix. Consequences:

- The **public website** needs a strong "run your agents on a remote host"
  documentation path (CLI-based: SSH in, provision, seed credentials, connect
  — essentially a public-friendly distillation of the runbook). Messaging:
  *a strong model does the heavy lifting for you* — plus honest constraints:
  **tested on Ubuntu 24.04**; other distros/OSes are expected to work but are
  unverified. → gap **G11** below.
- **Configuration happens at project level** — the per-project runner
  assignment (§2 / G7) is the committed UX, not one global "remote mode".
- **Probes are a runner capability**: each runner probes *its own* CLIs
  (PTY-based quota/model discovery) and reports snapshots upstream. Proven on
  Linux 2026-07-07 for both claude (`/usage`: plan Max, both windows) and
  codex (`/status`: plan Pro, all four windows).

## 1. Three operating modes (a timeline, not alternatives)

### Mode A — full stack on the runner host, SSH tunnel (available today)

The whole product (task server + runner + board UI) runs on the Linux host;
the operator machine keeps only a browser and one SSH tunnel:

```
ssh -L 4010:127.0.0.1:4010 agent-runner   →   http://localhost:4010
```

- **Zero product changes needed** — this is the Phase-1 stack, proven
  2026-07-07 with the *Website* pilot project.
- Security model: nothing is exposed publicly; the tunnel is the only door.
- **The trade-off to understand:** the host is a *separate brain*. It has its
  own task store, own board, own history — tasks there do not appear on the
  Windows board and vice versa. That is acceptable for "develop project X
  entirely remote", and it is the reason Modes B/C exist.

### Mode B — central URL (kickoff Phase 2)

One task server (initially still the Windows box) becomes reachable under a
central URL behind a reverse proxy with TLS + auth (D4), and `watchPath`
addressing is replaced by `PROJ-NNN` ids (D1). One board, visible from
everywhere. Execution still happens wherever that backend runs.

### Mode C — runner split (kickoff Phase 3)

Runners become separate processes on N hosts, registering with the task
server (lease + fencing per §8.2C). Only here does "runs remote **per
project**" become a real product feature instead of a deployment choice.

## 2. Where the user decides "remote or not" (Mode C product model)

- **Runner registry:** each runner process registers with the task server
  (id, display name, host, capabilities such as `playwright`, `dotnet`,
  `node`; heartbeat). Surfaced in a small "Runners" settings page: name,
  status dot, active task count, last heartbeat.
- **Per-project execution assignment:** the project record (ADR-0042
  registry) gets an `executionRunner` field. UI: project settings →
  *Ausführung*: `Lokal (default)` | `<runner name>`. That dropdown **is** the
  remote/local decision — one setting, per project, changeable any time
  between runs.
- **Visibility on the board:** running task cards carry a small runner badge
  (host name) next to the CLI badge; the task detail run header shows which
  runner executed each run. The target keeps an ordered route when coding,
  continuation, and review use several runners, including A → B → A returns.
  Assignment changes, historical attribution, and controlled host switching are
  defined in the Wiki's
  [runner provenance and host handoff contract](completion-review-and-remote-runner-stability.html#provenance).
- **Failure surface:** a runner that misses heartbeats shows as offline; its
  projects fall back to `blocked: runner offline` instead of silently
  queueing (explicit beats implicit).

## 3. Onboarding an existing project for remote development

Formalized checklist — items 1–4 exist today (Mode A), 5–6 are judgement:

1. **Code channel to the host.** Public repo → plain `https` clone. Private
   repo → either a read-only deploy key *or* the zero-credential **SSH
   mirror** pattern (operator pushes into `~/git/<name>.git` on the host;
   results fetched back with `git fetch runner`). See runbook §6 for the
   security assessment of each.
2. **`develop` branch exists** — the worktree flow (ADR-0057) bases task
   branches on it and refuses runs otherwise. (Carry-over: configurable
   base / default-branch fallback.)
3. **`.orchestrator.yml` pointer in the repo root** with
   `projectKey: <key>` — tasks then live in the central task store
   (`<TaskRepository>/projects/<key>`), never inside the project repo.
   Recommended: commit this file; it is meaningful on every machine.
4. **WatchPath entry on the executing backend** (today:
   `appsettings.Local.json` on the host; Mode C: replaced by the project
   registry + runner assignment, no file editing).
5. **Toolchain + secrets on the host:** node/npm, dotnet, Playwright
   browsers as the project needs; any `.env`-style secrets provisioned
   deliberately (they inherit the host's threat model — runbook §6).
6. **Verification pieces:** if the project uses visual verification, the
   Playwright install must exist on the host (it does on the pilot host).

**Pilot (live since 2026-07-07):** the *Website* project
(`agent-studio-for-software-website`, private repo → SSH-mirror channel)
is onboarded on the Hetzner host as the first real remote-developed project.

## 4. Screenshots / "Bilder" and artifacts

The screenshot pipeline is already remote-clean *within one host*: agents
drop images into `results/playwright/` of the task folder, the server
harvests and serves them via relative `/api` URLs — the SPA renders them
wherever it runs. In **Mode A this works end-to-end today** (CLI, Playwright,
task store and API share the host).

The gap is **Mode C**: a remote runner's artifacts must reach the task
server without a shared filesystem → the planned **artifact upload endpoint**
(kickoff D2/Phase 3, alongside the existing `POST /api/runner/logs`). Until
that exists, screenshots are a same-host feature.

## 5. What must change in the product (gap list = seed of the product plan)

| # | Gap | Phase | Notes |
|---|---|---|---|
| G1 | Auth + TLS on central URL (bearer per client/runner) | 2 | D4; hard gate before any port opens |
| G2 | `PROJ-NNN` addressing replaces `watchPath` | 2 | D1, prepared by ADR-0042; kills the `.orchestrator/jobs`-path confusion observed in Phase 1 |
| G3 | Static SPA hosting + reverse proxy (`/api`, `/hubs` ws) | 2 | today "stable" is a permanent `ng serve` |
| G4 | Orchestrator boot prompt / skill scripts: templated API base | 2 | hardcoded `127.0.0.1:5030/5031` today |
| G5 | Runner as standalone process (REST + SignalR consumer) | 3 | the §8.2C gate: lease + fencing, heartbeat |
| G6 | Artifact upload endpoint (results, screenshots) | 3 | log ingestion exists, artifacts don't |
| G7 | Runner registry + per-project assignment UI | 3 | §2 above — the actual product feature |
| G8 | Runner deploy story (systemd + git pull + health) | 3 | D6 |
| G9 | Preview/dev-server proxy for remote previews | 4 | D7 |
| G10 | Linux-green test suite + worktree base fallback | 1.x | carry-overs from Phase 1 findings |
| G11 | Website: public "remote host" getting-started docs | 2 | first-class-citizen stance (§0); CLI-based path, Ubuntu-tested constraint stated honestly |

**Braucht es einen Produktplan? Ja — aber klein.** *(Superseded in part by §6/§7
below — the operator cut the work into two areas on 2026-07-07.)* Recommendation: treat this
table as the plan seed, cut it into task cards per row (G1–G4 = one "Phase 2"
epic, G5–G8 = one "Phase 3" epic), and decide only one thing up front: when
Phase 2 starts (its gate is the security-overview rewrite). Everything else
is sequenced by the phases already committed in the kickoff doc.

---

## 6. The two work areas (operator decision, 2026-07-07)

Mockups for every screen in this section:
[`mockups/remote-hosts-ux.html`](mockups/remote-hosts-ux.html).

### 6.1 Area 1 — Remote-host onboarding & management

**New admin page "Remote Hosts"** (top-level settings entry):

- **Host list**: name, address, status dot (heartbeat), active tasks,
  capabilities (dotnet / node / playwright), and **per-CLI quota** from the
  runner-owned probes (claude plan+windows, codex plan+windows) — the §0
  probe capability rendered as compact bars.
- **Add-host wizard**, four steps, each accompanied by an **orchestrator
  chat panel** (the "a strong model helps you" promise — the CLI assistant
  can execute every step for you or you follow the commands manually):
  1. *Connect* — SSH endpoint + key; connectivity check.
  2. *Provision* — automated checklist from the runbook §2 (git, node 22,
     dotnet 10, CLIs, Playwright); "fix it" runs the missing steps.
  3. *Authenticate CLIs* — credential seeding (runbook §3) with live
     verification (`RUNNER-OK` / `CODEX-OK`).
  4. *Smoke* — the runbook §4 battery with live pass/fail rows.
- Host actions: re-probe, drain (finish running tasks, accept no new),
  retire.

**Project assignment (the per-project onboarding step):** project settings
get an **Execution** card — dropdown `Local (default) | <host>`, plus
**"Try it out"**: a guided test that walks the §3 onboarding checklist
(code channel reachable, `develop` branch, toolchain, then one real no-op
task run on the host) and reports each item pass/fail. Assignment is only
offered when at least one host is healthy.

### 6.2 Area 2 — Task-server (Task API) management

Today the Task API is implicit: it lives wherever the backend runs, its
workspace is a config line, and it has no management surface. That ends:

- **New settings page "Task Server"**: shows the server the studio is
  connected to — **URL** (today `localhost`; Phase 2: the central URL),
  workspace root + size, git-evidence status (last commit, pending),
  project/task counts, client registry (who may mutate), and — with Phase 2
  auth — token management.
- **Onboarding step "assign URL"**: the studio explicitly connects to a task
  server URL instead of assuming loopback. Local stays the zero-config
  default; the URL field is how a second machine or a hosted task server
  joins (G1–G4 make this real).
- Management functions on the store itself (the "protocols"): archive
  sweeps, orphan/fixture cleanup, storage stats — surfaced here instead of
  living in operator scripts.

### 6.3 Decision D8 — dedicated hosts, not task-level scheduling (for now)

Question asked 2026-07-07: *why not schedule per task across hosts by
utilization?* Answer: **dedicated host per project first.** Rationale:

1. A task run needs a **warm environment** — repo clone, `node_modules`,
   Playwright browsers, secrets. Per-project dedication keeps that
   environment warm on exactly one host; task-level spreading would need
   every project provisioned on every host (or slow cold starts per task).
2. **Quota does not multiply**: CLIs share the account quota regardless of
   host count — utilization scheduling only balances CPU/RAM, which a
   dedicated 64-GB box has in abundance.
3. Nothing is lost: the §8.2C lease contract is per-task anyway, so
   task-level scheduling stays a **later option** — gated on "environment
   pools" (pre-provisioned project environments on N hosts). Revisit when a
   project's parallel load saturates its dedicated host.

## 7. MVP cut (operator decision, 2026-07-07)

Goal: a **presentable product** ("Minimal Viable Product Situation").
The pipeline, in priority order:

1. **Task-pipeline visual redesign** (in progress — V-design workbench).
2. **New orchestrator chat** (multichat concept: task-focused orchestrator,
   own history per context, 10–30 parallel sessions) — needs its own concept
   doc, then integration.
3. **Website update** — remote execution communicated as the big thing
   (G11; first content section shipped 2026-07-07), plus refreshed
   screenshots.
4. **Screen tooling** — reliable, good-looking product captures
   (screenshots/recordings) for presenting the MVP; evaluate what the
   existing Playwright pipeline can deliver vs. a dedicated capture tool.

Remote-host admin (§6.1) and task-server management (§6.2) are the
follow-on product epics after the MVP is presentable.

### 6.4 Placement in the existing admin system (operator feedback, 2026-07-08)

The §6 screens are **not standalone pages or modals** — they slot into the
existing administrative surface with its many tabs:

- **Task Server** → a tab at **workspace level**.
- **Remote Hosts** → a tab at **workspace level**, next to it.
- **Add-Host wizard** → launched *from* the Remote Hosts tab (its only
  entry point), rendered as the tab's detail flow.
- **Enablement state**: remote features are gated — a workspace-level state
  ("remote execution enabled") controls whether the Remote Hosts tab and
  the per-project Execution card appear at all. Default off; flipping it on
  is itself the first onboarding step. This keeps the local-only default
  experience untouched.
- **Prerequisite for building any of this**: the new chat
  (`@coding-agent/chat` composer) must be adopted everywhere first — the
  wizard's orchestrator panel and the multichat concept
  ([`multichat-orchestrator.md`](multichat-orchestrator.md), Phase 0) hang
  off the same component.
