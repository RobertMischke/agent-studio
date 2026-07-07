# Remote execution in the product — user-facing integration concept

**Status:** draft 2026-07-07, companion to
[`remote-ready-kickoff-2026-07.md`](../research/remote-ready-kickoff-2026-07.md)
(ADR-0059). The kickoff owns the *architecture* phases; this document answers
the *product* questions: how does a human decide "this project runs remote",
what do they see, and how is an existing project onboarded. Operational host
details live in
[`linux-runner-host.md`](../operations/setup/linux-runner-host.md).

---

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
  runner executed each run. No other UI changes — lanes, logs, history are
  already server-owned.
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

**Braucht es einen Produktplan? Ja — aber klein.** Recommendation: treat this
table as the plan seed, cut it into task cards per row (G1–G4 = one "Phase 2"
epic, G5–G8 = one "Phase 3" epic), and decide only one thing up front: when
Phase 2 starts (its gate is the security-overview rewrite). Everything else
is sequenced by the phases already committed in the kickoff doc.
